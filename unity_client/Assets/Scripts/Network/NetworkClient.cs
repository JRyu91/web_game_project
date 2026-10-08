// field/server.js·field/net.js 프로토콜을 그대로 따르는 유니티 클라이언트.
// Unity 내장 JsonUtility + 고정 DTO(NetMessages.cs) 로 직렬화한다 — System.Text.Json/Newtonsoft 는
// 이 런타임 프로파일에 없어서(컴파일 에러로 확인됨) 새 패키지를 추가하는 대신 내장 기능으로 처리.
using System;
using System.Collections.Concurrent;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.WebSockets;
#endif
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.Network {

public class NetworkClient : MonoBehaviour {
    public static NetworkClient Instance { get; private set; }

    public bool Connected { get; private set; }
    public bool Joined { get; private set; }
    public string Me { get; private set; }        // 서버가 발급한 접속 id
    public string Channel { get; private set; }
    public int Index { get; private set; }
    public int Cap { get; private set; }

    public event Action<WelcomeMsg> OnWelcome;
    public event Action OnConnecting;
    public event Action<RosterEntry[]> OnRoster;
    public event Action<string, string> OnChat;      // who, text
    public event Action<string> OnSystem;
    public event Action<Snapshot> OnResume;           // 이관 상태 복원
    public event Action<string, float, int> OnPos;    // id, x, face
    public event Action<TransferMsg> OnTransfer;
    public event Action OnFull;
    public event Action<string> OnDrain;
    public event Action OnDisconnected;
    public event Action<SpawnMsg> OnSpawn;
    public event Action<InvMsg> OnInv;                // F안: 서버 권위 상태(inv 응답 단일 타입)
    int _seq;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void WebConnect(string url, string join, int generation);
    [DllImport("__Internal")] static extern void WebSend(string text);
    [DllImport("__Internal")] static extern void WebClose();
    [Serializable] class WebEnvelope { public int generation; public string text; }
    public void WebEvent(string json) {
        var e = JsonUtility.FromJson<WebEnvelope>(json);
        _inbox.Enqueue((e.generation, e.text));
    }
#else
    ClientWebSocket _ws;
    CancellationTokenSource _cts;
    readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
#endif
    void AbortTransport() {
#if UNITY_WEBGL && !UNITY_EDITOR
        WebClose();
#else
        _cts?.Cancel();
        try { _ws?.Abort(); } catch (ObjectDisposedException) { }
#endif
    }
    readonly ConcurrentQueue<(int generation, string text)> _inbox = new ConcurrentQueue<(int, string)>();
    int _generation;

    string _name, _accountId, _gender = "male";
    int _level = 1;
    Func<Snapshot> _getSnapshot;
    float _stateTimer, _posTimer;
    const float STATE_INTERVAL = 1f;
    const float POS_INTERVAL = 0.15f;

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Connect(string wsUrl, string accountId, string name, int level, string gender, Func<Snapshot> getSnapshot, bool resume = false) {
        int generation = ++_generation;
        AbortTransport();
        Connected = false; Joined = false;
        OnConnecting?.Invoke();
        _name = name; _accountId = accountId; _level = level; _gender = gender; _getSnapshot = getSnapshot;
        var join = new JoinMsg { id = accountId, name = name, level = level, gender = gender, resume = resume };
#if UNITY_WEBGL && !UNITY_EDITOR
        WebConnect(wsUrl, JsonUtility.ToJson(join), generation);
#else
        _ = ConnectAsync(wsUrl, join, generation);
#endif
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    async Task ConnectAsync(string wsUrl, JoinMsg join, int generation) {
        var cts = new CancellationTokenSource();
        var ws = new ClientWebSocket();
        _cts = cts; _ws = ws;
        try {
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
        } catch (Exception e) {
            if (generation == _generation) Debug.LogWarning($"[net] connect failed: {e.Message}");
            Disconnected(generation);
            return;
        }
        if (generation != _generation) { ws.Abort(); return; }
        _inbox.Enqueue((generation, "{\"type\":\"transport_open\"}"));
        await SendJson(join, ws, cts.Token, generation);
        _ = ReceiveLoop(ws, cts.Token, generation);
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token, int generation) {
        var buf = new byte[16384];
        while (generation == _generation && ws.State == WebSocketState.Open) {
            try {
                var seg = new ArraySegment<byte>(buf);
                // 프레임이 나뉘어 오면(inv 60칸 등 큰 메시지) EndOfMessage 까지 이어 붙인다
                var ms = new System.IO.MemoryStream(); WebSocketReceiveResult result;
                do { result = await ws.ReceiveAsync(seg, token); ms.Write(buf, 0, result.Count); }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
                if (result.MessageType == WebSocketMessageType.Close) break;
                _inbox.Enqueue((generation, Encoding.UTF8.GetString(ms.ToArray())));
            } catch (Exception e) {
                if (generation == _generation) Debug.LogWarning($"[net] recv error: {e.Message}");
                break;
            }
        }
        Disconnected(generation);
    }

#endif

    void Disconnected(int generation) {
        _inbox.Enqueue((generation, "{\"type\":\"closed\"}")); // Unity 상태 변경은 Update의 메인 스레드에서 실행한다.
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    Task SendJson(object msg) {
        if (Connected) WebSend(JsonUtility.ToJson(msg));
        return Task.CompletedTask;
    }
#else
    Task SendJson(object msg) => SendJson(msg, _ws, _cts?.Token ?? CancellationToken.None, _generation);

    async Task SendJson(object msg, ClientWebSocket ws, CancellationToken token, int generation) {
        if (generation != _generation || ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
        try {
            await _sendLock.WaitAsync(token);
            try { if (generation == _generation && ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
            finally { _sendLock.Release(); }
        } catch (Exception e) { if (generation == _generation) Debug.LogWarning($"[net] send error: {e.Message}"); }
    }

#endif

    void Update() {
        while (_inbox.TryDequeue(out var message)) if (message.generation == _generation) Dispatch(message.text);
        if (!Connected || !Joined) return;

        _stateTimer += Time.deltaTime;
        if (_stateTimer >= STATE_INTERVAL) {
            _stateTimer = 0;
            var snap = _getSnapshot?.Invoke();
            _ = SendJson(new StateOutMsg { level = _level, snapshot = snap });
        }
        _posTimer += Time.deltaTime;
        if (_posTimer >= POS_INTERVAL) {
            _posTimer = 0;
            var snap = _getSnapshot?.Invoke();
            if (snap != null) _ = SendJson(new PosOutMsg { x = snap.x, face = snap.face });
        }
    }

    void Dispatch(string text) {
        var head = JsonUtility.FromJson<TypeOnly>(text);
        switch (head?.type) {
            case "transport_open":
                Connected = true;
                break;
            case "welcome": {
                var m = JsonUtility.FromJson<WelcomeMsg>(text);
                Joined = false;
                Me = m.id; Channel = m.channel; Index = m.index; Cap = m.cap;
                OnWelcome?.Invoke(m);
                if (m.roster != null) OnRoster?.Invoke(m.roster);
                break;
            }
            case "roster": {
                var m = JsonUtility.FromJson<RosterMsg>(text);
                if (m.roster != null) OnRoster?.Invoke(m.roster);
                break;
            }
            case "chat": {
                var m = JsonUtility.FromJson<ChatInMsg>(text);
                OnChat?.Invoke(m.who, m.text);
                break;
            }
            case "system":
                OnSystem?.Invoke(JsonUtility.FromJson<SystemMsg>(text).text);
                break;
            case "resume": {
                var m = JsonUtility.FromJson<ResumeMsg>(text);
                if (m.state != null) OnResume?.Invoke(m.state);
                break;
            }
            case "pos": {
                var m = JsonUtility.FromJson<PosInMsg>(text);
                OnPos?.Invoke(m.id, m.x, m.face);
                break;
            }
            case "transfer":
                OnTransfer?.Invoke(JsonUtility.FromJson<TransferMsg>(text));
                break;
            case "full":
                OnFull?.Invoke();
                break;
            case "drain":
                OnDrain?.Invoke(JsonUtility.FromJson<DrainMsg>(text).text);
                break;
            case "spawn":
                if (Connected && Joined) OnSpawn?.Invoke(JsonUtility.FromJson<SpawnMsg>(text));
                break;
            case "inv": {
                var m = JsonUtility.FromJson<InvMsg>(text);
                if (!Connected) break;
                if (m.req == "join") Joined = m.ok && m.state != null && m.state.equip != null && m.state.inv != null && m.state.level >= 1;
                else if (!Joined) break;
                if (m.req == "join" && !Joined) {
                    m.ok = false;
                    if (string.IsNullOrEmpty(m.code)) m.code = "bad_state";
                    m.state = null;
                }
                OnInv?.Invoke(m);
                if (m.req == "join" && !Joined) { AbortTransport(); Disconnected(_generation); }
                break;
            }
            case "closed":
                Connected = false; Joined = false;
                OnDisconnected?.Invoke();
                break;
        }
    }

    public void SendChat(string text) => _ = SendJson(new ChatOutMsg { text = text });
    public void SwitchChannel(int to) {
        if (Game.Gameplay.PlayerController.Local?.PendingKillRewards == true) { OnSystem?.Invoke("처치 보상 저장이 끝난 뒤 이동할 수 있습니다"); return; }
        if (!Connected || !Joined) return;
        _ = SendJson(new SwitchMsg { to = to });
    }
    public void SyncLevel(int level) => _level = level;
    // inv/kill/equip/enhance/buy/sell/potion/summon. 반환 = seq
    public int Request(InvReq r) { if (!Connected || !Joined) return 0; r.seq = ++_seq; _ = SendJson(r); return r.seq; }
    public int DisassembleMany(int[] uids) { if (!Connected || !Joined) return 0; var r = new DisassembleManyReq { seq = ++_seq, uids = uids }; _ = SendJson(r); return r.seq; }
    public int SellMany(int[] uids) { if (!Connected || !Joined) return 0; var r = new SellManyReq { seq = ++_seq, uids = uids }; _ = SendJson(r); return r.seq; }

    void OnDestroy() {
        _generation++;
        Connected = false; Joined = false;
        if (Instance == this) Instance = null;
        AbortTransport();
    }
}
}
