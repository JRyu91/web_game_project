// 씬의 루트 진입점. 로컬 플레이어 생성, 몬스터 스폰, 서버 접속(field/server.js), 원격 플레이어 유령 렌더,
// 최소 HUD(OnGUI — 캔버스/프리팹 없이 코드만으로 그려서 에디터 GUI 조작 없이도 완결되게 함) 를 전부 묶는다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Game.Data;
using Game.Network;

namespace Game.Gameplay {

public class GameManager : MonoBehaviour {
    [Header("접속")]
    public string ServerUrl = "ws://localhost:8080";
    public string AccountId = "local_dev";
    public string PlayerName = "모험가";
    public string Gender = "male";

    public MonsterSpawner Spawner;
    PlayerController _player;
    readonly Dictionary<string, GameObject> _ghosts = new Dictionary<string, GameObject>();
    readonly Dictionary<string, RosterEntry> _roster = new Dictionary<string, RosterEntry>();
    readonly List<string> _chatLog = new List<string>();
    sealed class SpawnRegistration { public MonsterController monster; public int tier; public string receipt; public bool dead; public int killSeq; public float retryAt; }
    int _monsterId;
    readonly Dictionary<int, SpawnRegistration> _spawns = new Dictionary<int, SpawnRegistration>();
    Snapshot _resumeState;
    string _systemMsg = "";

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string WebConfig();
    [Serializable] class BrowserSession { public string id, name, gender, wsUrl; }
#endif
    void Start() {
#if UNITY_WEBGL && !UNITY_EDITOR
        var session = JsonUtility.FromJson<BrowserSession>(WebConfig());
        AccountId = session.id; PlayerName = session.name; Gender = session.gender; ServerUrl = session.wsUrl;
#endif
        Application.runInBackground = true; // 방치형: 창이 포커스를 잃어도 계속 진행
        var playerGo = new GameObject("Player");
        playerGo.transform.position = new Vector3(10f, 0, 0); // 맵 [0,80] 안
        _player = playerGo.AddComponent<PlayerController>();
        _player.Init(true, Gender, () => Spawner.Alive);
        SetGameplayReady(false);
        _player.OnHpChanged += (hp, max) => { };
        _player.OnLevelChanged += (lv, exp, need) => NetworkClient.Instance?.SyncLevel(lv);
        Spawner.OnMonsterSpawned += RegisterMonster;
        Spawner.OnMonsterDied += MonsterDied;
        Spawner.OnMonsterRemoved += MonsterRemoved;

        var netGo = new GameObject("NetworkClient");
        var net = netGo.AddComponent<NetworkClient>();
        BindNetwork(net);
        net.Connect(ServerUrl, AccountId, PlayerName, _player.Level, Gender, () => _player.GetSnapshot());

        new GameObject("GameUI").AddComponent<GameUI>().Init(Camera.main, _player, Spawner, net, () => StartCoroutine(PostSave())); // F안: 서버 inv → 인벤·강화·상점·소환
        SetupWorld(Camera.main, WorldConfig.MapWidth - WorldConfig.MapMargin - 2f);
        StartCoroutine(SaveLoop());
    }

    void BindNetwork(NetworkClient net) {
        net.OnSpawn += SpawnRegistered;
        net.OnConnecting += () => { ClearGhosts(); _spawns.Clear(); _player.PendingKillRewards = false; _resumeState = null; SetGameplayReady(false); Loading = true; _systemMsg = "서버 접속 중 — 플레이 일시정지"; };
        net.OnWelcome += m => { SetGameplayReady(false); Loading = true; _systemMsg = $"채널 {net.Channel} 접속 — 저장 상태 확인 중"; };
        net.OnInv += m => {
            if (m.req == "kill") ResolveKill(m);
            if (m.state != null && Enum.TryParse<Game.Rendering.Zone>(m.state.zone, out var zone)) {
                var current = Game.Rendering.ZoneController.Current;
                if (current != null && current.Zone != zone) current.SetZone(zone, current.Tod);
            }
            if (net.Joined && m.state != null) {
                SetGameplayReady(!m.state.HasPendingDrop);
                if (m.state.HasPendingDrop) _systemMsg = "가방이 가득 찼습니다. 장비를 정리하면 보관된 드롭을 회수합니다";
            }
            if (m.req != "join") return;
            if (!net.Joined) { SetGameplayReady(false); _systemMsg = "저장 상태 확인 실패 — 플레이 일시정지"; return; }
            _player.ApplyState(m.state);
            if (_resumeState != null) { _player.RestoreTransport(_resumeState); _resumeState = null; }
            SetGameplayReady(!m.state.HasPendingDrop);
            Loading = false;
            foreach (var monster in Spawner.Alive) RegisterMonster(monster);
            _systemMsg = $"채널 {net.Channel} 접속 (정원 {net.Cap}명)";
        };
        net.OnSystem += t => _systemMsg = t;
        net.OnChat += (who, text) => { _chatLog.Add($"{who}: {text}"); if (_chatLog.Count > 8) _chatLog.RemoveAt(0); };
        net.OnRoster += UpdateRoster;
        net.OnPos += (id, x, face) => UpdateGhost(id, x, face);
        net.OnTransfer += m => StartCoroutine(Transfer(m));
        net.OnResume += s => _resumeState = s;
        net.OnFull += () => _systemMsg = "채널이 가득 찼습니다. 새로고침 후 다른 채널을 선택하세요";
        net.OnDisconnected += () => { ClearGhosts(); SetGameplayReady(false); _systemMsg = "서버 연결 끊김 — 플레이 일시정지"; Loading = false; };
    }

    void RegisterMonster(MonsterController monster) {
        var net = NetworkClient.Instance;
        if (monster == null || net == null || !net.Joined || _spawns.Values.Any(r => r.monster == monster)) return;
        int id = ++_monsterId; monster.RegistrationId = id;
        _spawns[id] = new SpawnRegistration { monster = monster, tier = monster.Def.tier };
        net.Request(new InvReq { type = "spawn", monsterId = id, tier = monster.Def.tier });
    }

    void SpawnRegistered(SpawnMsg message) {
        if (!_spawns.TryGetValue(message.monsterId, out var registration)) {
            if (message.monsterId > 0 && message.monsterId <= _monsterId && message.ok && !string.IsNullOrEmpty(message.receipt)) NetworkClient.Instance?.Request(new InvReq { type = "despawn", receipt = message.receipt });
            return;
        }
        if (!message.ok || string.IsNullOrEmpty(message.receipt) || message.tier != registration.tier) {
            _spawns.Remove(message.monsterId);
            _player.PendingKillRewards = _spawns.Values.Any(r => r.dead);
            if (registration.monster != null) Spawner.Remove(registration.monster);
            _systemMsg = "몬스터 등록 실패: " + message.code;
            return;
        }
        registration.receipt = message.receipt;
        // The server's minimum lifetime also covers a kill that happened before the spawn reply.
        StartCoroutine(ReportRegisteredKill(message.monsterId, registration));
    }

    IEnumerator ReportRegisteredKill(int id, SpawnRegistration registration) {
        yield return new WaitForSecondsRealtime(0.3f);
        while (_spawns.TryGetValue(id, out var current) && current == registration) {
            if (registration.dead && registration.killSeq == 0 && Time.realtimeSinceStartup >= registration.retryAt)
                registration.killSeq = _player.GainKillReward(registration.tier, registration.receipt);
            yield return null;
        }
    }

    void ResolveKill(InvMsg message) {
        var pair = _spawns.FirstOrDefault(p => p.Value.killSeq != 0 && p.Value.killSeq == message.seq);
        if (pair.Key == 0) return;
        if (message.ok) { _player.ConfirmKillReward(); _spawns.Remove(pair.Key); }
        else if (message.code == "save_failed" || message.code == "too_fast" || message.code == "rate") {
            pair.Value.killSeq = 0; pair.Value.retryAt = Time.realtimeSinceStartup + 3;
        } else { _spawns.Remove(pair.Key); _systemMsg = "처치 보상 확인 실패: " + message.code; }
        _player.PendingKillRewards = _spawns.Values.Any(r => r.dead);
    }

    void MonsterDied(MonsterController monster) {
        var registration = _spawns.Values.FirstOrDefault(r => r.monster == monster);
        if (registration != null) { registration.dead = true; _player.PendingKillRewards = true; }
    }

    void MonsterRemoved(MonsterController monster) {
        foreach (var pair in _spawns.Where(p => p.Value.monster == monster).ToArray()) {
            if (!string.IsNullOrEmpty(pair.Value.receipt)) NetworkClient.Instance?.Request(new InvReq { type = "despawn", receipt = pair.Value.receipt });
            _spawns.Remove(pair.Key);
        }
    }

    IEnumerator Transfer(TransferMsg message) {
        SetGameplayReady(false);
        yield return null;
        var uri = new Uri(ServerUrl);
        ServerUrl = new UriBuilder(uri) { Path = message.@base }.Uri.ToString().TrimEnd('/');
        NetworkClient.Instance.Connect(ServerUrl, AccountId, PlayerName, _player.Level, Gender, _player.GetSnapshot, true);
    }

    // ── E안 2단계: 포털·로딩 스피너·보스 HP 바 (SpriteRenderer, 카메라 자식 HUD — 캡처에도 찍힘) ──
    // Resources.Load 키(에셋 MANIFEST §3) — 바뀌면 여기만
    const string KeyPortal = "Sprites/FX/e_anim/portal", KeySpinner = "Sprites/FX/e_anim/spinner", KeyHpFrame = "Sprites/UI/ui_boss_hp_frame";
    const int HudOrder = 100, HudScale = 2; // HP 바 정수 2배
    public bool Loading = true;             // 권위 inv.join 상태 수신 전 스피너
    Sprite[] _portalF, _spinF;
    SpriteRenderer _portal, _spinner, _hpFrame, _hpFill;

    // 텍스처 통째로 1장 = 1프레임. 임포터가 Multiple(자동 슬라이스)로 잡아도 조각 대신 전체가 나오게(스피너 프레임 0·3 소실 원인)
    static Sprite[] Frames(string dir) => Resources.LoadAll<Texture2D>(dir).OrderBy(x => x.name)
        .Select(t => Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 40)).ToArray();
    static SpriteRenderer Sr(string name, Transform parent, int order) {
        var sr = new GameObject(name).AddComponent<SpriteRenderer>();
        if (parent != null) sr.transform.SetParent(parent, false);
        sr.sortingOrder = order; return sr;
    }
    static void Ground(SpriteRenderer sr, float x) => sr.transform.position = new Vector3(x, WorldConfig.GroundY - sr.sprite.bounds.min.y, 0); // 피벗 무관 하단=지면

    public void SetupWorld(Camera cam, float portalX) {
        _portalF = Frames(KeyPortal); _spinF = Frames(KeySpinner);
        if (_portalF.Length > 0) { _portal = Sr("Portal", null, 3); _portal.sprite = _portalF[0]; Ground(_portal, portalX); }
        if (cam == null) return;
        if (_spinF.Length > 0) { _spinner = Sr("Spinner", cam.transform, HudOrder); _spinner.sprite = _spinF[0]; _spinner.transform.localPosition = new Vector3(cam.orthographicSize * cam.aspect - 1f, -cam.orthographicSize + 1f, 10); }
        var frame = Resources.Load<Sprite>(KeyHpFrame);
        if (frame != null) {
            _hpFrame = Sr("BossHpBar", cam.transform, HudOrder); _hpFrame.sprite = frame;
            _hpFrame.transform.localScale = new Vector3(HudScale, HudScale, 1);
            _hpFrame.transform.localPosition = new Vector3(0, cam.orthographicSize - (8 + frame.rect.height * HudScale / 2f) / 40f, 10); // 상단 중앙, 위 여백 8px
            _hpFill = Sr("Fill", _hpFrame.transform, HudOrder + 1);
            var tex = Texture2D.whiteTexture;
            _hpFill.sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0, 0.5f), 40); // 1px, 좌측 피벗
            _hpFill.color = new Color(0.78f, 0.12f, 0.12f);
            _hpFill.transform.localPosition = new Vector3((24 - frame.rect.width / 2f) / 40f, 0, 0); // 창 x 24..118, y 7..17 (프레임 129x24 실측)
            _hpFrame.gameObject.SetActive(false);
        }
    }
    // t = 애니 시각(런타임 Time.time, 캡처는 고정값). boss = 살아있는 보스(없으면 HP 바 숨김).
    public void TickWorld(float t, MonsterController boss) {
        if (_portal != null) {
            _portal.sprite = _portalF[(int)(t * 8) % _portalF.Length]; // 소용돌이 회전 = 6프레임(석조 틀이라 스프라이트 통째 회전 안 함)
            float k = 0.85f + 0.15f * Mathf.Sin(t * 4f); _portal.color = new Color(k, k, k);
        }
        if (_spinner != null) {
            var cam = _spinner.GetComponentInParent<Camera>();
            if (cam != null) _spinner.transform.localPosition = new Vector3(cam.orthographicSize * cam.aspect - 1f, -cam.orthographicSize + 1f, 10);
            _spinner.enabled = Loading;
            _spinner.sprite = _spinF[(int)(t * 10) % _spinF.Length];
            float k = 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(t * 3f)); _spinner.color = new Color(1, 1, 1, k); // 펄스: 알파 0.6~1 (최저 0.6 유지)
        }
        if (_hpFrame != null) {
            var cam = _hpFrame.GetComponentInParent<Camera>();
            if (cam != null) _hpFrame.transform.localPosition = new Vector3(0, cam.orthographicSize - (8 + _hpFrame.sprite.rect.height * HudScale / 2f) / 40f, 10);
            bool show = boss != null && !boss.IsDead;
            _hpFrame.gameObject.SetActive(show);
            if (show) _hpFill.transform.localScale = new Vector3(94f * Mathf.Clamp01((float)boss.Hp / boss.MaxHp), 10f, 1);
        }
    }
    static bool IsBoss(MonsterController m) => m != null && (m.Def.rank == "boss" || m.Def.rank == "midboss" || m.Def.rank == "hidden");

    // field/server.js GET/POST /save/:id (§9 세이브). 인증 없이 계정 id 만으로 왕복하는 기존 서버 계약 그대로 사용.
    const float SAVE_INTERVAL = 20f;

    void SetGameplayReady(bool ready) {
        _player.GameplayReady = ready;
        _player.GetComponent<Game.Rendering.FrameAnimator>().enabled = ready;
        if (Spawner == null) return;
        Spawner.enabled = ready;
        foreach (var m in Spawner.Alive) {
            if (m == null) continue;
            m.enabled = ready;
            m.GetComponent<Game.Rendering.FrameAnimator>().enabled = ready;
        }
    }

    IEnumerator SaveLoop() {
        yield return StartCoroutine(LoadSave());
        while (true) {
            yield return new WaitForSeconds(SAVE_INTERVAL);
            yield return StartCoroutine(PostSave());
        }
    }

    IEnumerator LoadSave() {
        string url = ToHttp(ServerUrl).TrimEnd('/') + "/save/" + UnityWebRequest.EscapeURL(AccountId);
        using (var req = UnityWebRequest.Get(url)) {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) {
                Debug.LogWarning($"[save] load failed: {req.error}");
                yield break;
            }
            var env = JsonUtility.FromJson<SaveEnvelope>(req.downloadHandler.text);
            if (env?.data != null) _player.ApplyPreferences(env.data);
        }
    }

    IEnumerator PostSave() {
        var net = NetworkClient.Instance;
        if (net == null || !net.Connected || !net.Joined) yield break;
        var data = new SavePreferences { potionThreshold = _player.PotionHpThresholdPercent };
        string json = JsonUtility.ToJson(data);
        string url = ToHttp(ServerUrl).TrimEnd('/') + "/save/" + UnityWebRequest.EscapeURL(AccountId);
        using (var req = new UnityWebRequest(url, "POST")) {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) Debug.LogWarning($"[save] post failed: {req.error}");
        }
    }

    static string ToHttp(string wsUrl) => wsUrl.Replace("wss://", "https://").Replace("ws://", "http://");

    void ClearGhosts() {
        foreach (var go in _ghosts.Values) RemoveGhostObject(go);
        _ghosts.Clear(); _roster.Clear();
    }

    static void RemoveGhostObject(GameObject go) { if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }

    void UpdateRoster(RosterEntry[] roster) {
        var present = new Dictionary<string, RosterEntry>();
        foreach (var entry in roster ?? Array.Empty<RosterEntry>())
            if (entry != null && !string.IsNullOrEmpty(entry.id) && entry.id != NetworkClient.Instance?.Me) present[entry.id] = entry;
        foreach (var id in _ghosts.Keys.Where(id => !present.ContainsKey(id)).ToArray()) { RemoveGhostObject(_ghosts[id]); _ghosts.Remove(id); }
        _roster.Clear();
        foreach (var pair in present) {
            _roster[pair.Key] = pair.Value;
            if (_ghosts.TryGetValue(pair.Key, out var go)) RefreshGhost(go, pair.Value);
        }
    }

    void RefreshGhost(GameObject go, RosterEntry entry) {
        var anim = go.GetComponent<Game.Rendering.FrameAnimator>();
        string body = entry.gender == "female" ? "main_f" : "main_m";
        if (anim.SourceKey != body) { anim.SetSource($"{Game.Rendering.ActorScale.PlayerRoot}/{body}"); anim.Play("idle"); }
        var weapon = entry.equip?.weapon;
        var defs = weapon?.kind == "staff" ? GameData.Staves : GameData.Swords;
        string name = weapon != null && (weapon.kind == "staff" || weapon.kind == "sword") && weapon.tier >= 0 && weapon.tier < defs.Length ? System.IO.Path.GetFileName(defs[weapon.tier].spritePath) : null;
        go.GetComponent<Game.Rendering.GearAttachment>().SetWeapon(name);
        var label = go.GetComponentInChildren<TextMesh>();
        label.text = (entry.name ?? entry.id) + " · 다른 플레이어";
        label.transform.localPosition = new Vector3(0, go.GetComponent<SpriteRenderer>().sprite.bounds.max.y + 0.2f, 0);
    }

    void UpdateGhost(string id, float x, int face) {
        if (string.IsNullOrEmpty(id) || id == NetworkClient.Instance?.Me || !_roster.TryGetValue(id, out var entry) || float.IsNaN(x) || float.IsInfinity(x)) return;
        bool existed = _ghosts.TryGetValue(id, out var go);
        if (!existed) {
            go = new GameObject($"Ghost_{id}"); go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>(); sr.sortingOrder = 4;
            var anim = go.AddComponent<Game.Rendering.FrameAnimator>(); anim.Renderer = sr;
            anim.SetSource($"{Game.Rendering.ActorScale.PlayerRoot}/{(entry.gender == "female" ? "main_f" : "main_m")}"); anim.Play("idle");
            var gear = go.AddComponent<Game.Rendering.GearAttachment>(); gear.Init(sr, anim);
            go.AddComponent<Game.Rendering.ActorVisual>().Init(sr, Game.Rendering.ActorScale.Player, false);
            var label = new GameObject("RemoteName").AddComponent<TextMesh>(); label.transform.SetParent(go.transform, false);
            label.anchor = TextAnchor.LowerCenter; label.fontSize = 32; label.characterSize = 0.3f; label.color = new Color(0.72f, 0.85f, 1);
            var font = Resources.Load<Font>("Fonts/Galmuri11");
            if (font != null) { label.font = font; label.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
            label.GetComponent<MeshRenderer>().sortingOrder = 10;
            _ghosts[id] = go; RefreshGhost(go, entry);
        }
        bool moving = existed && Mathf.Abs(x - go.transform.position.x) > 0.01f;
        go.GetComponent<Game.Rendering.FrameAnimator>().Play(moving ? "walk" : "idle");
        go.transform.position = new Vector3(Mathf.Clamp(x, WorldConfig.MapMargin, WorldConfig.MapWidth - WorldConfig.MapMargin), go.transform.position.y, 0);
        go.GetComponent<Game.Rendering.GearAttachment>().SetFace(face);
    }

    // fps 측정(기획 기준 10): 5초 평균을 로그로
    float _fpsT; int _fpsN;
    void Update() {
        _fpsT += Time.unscaledDeltaTime; _fpsN++;
        if (_fpsT >= 5f) { Debug.Log($"[fps] {_fpsN / _fpsT:0.0} avg over {_fpsT:0.0}s"); _fpsT = 0; _fpsN = 0; }
        TickWorld(Time.time, Spawner != null ? Spawner.Alive.FirstOrDefault(IsBoss) : null);
    }

}
}
