using System;
using System.Reflection;
using System.Net.Http;
using System.Text;
using System.Threading;
using UnityEngine;
using Game.Data;
using Game.Gameplay;
using Game.Network;
using Game.Rendering;

namespace Game.EditorTools {

// -executeMethod Game.EditorTools.SaveRegression.Run: 편집 모드의 권위 join·설정 적용 순서 검증.
public static class SaveRegression {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private).Invoke(obj, args);
    static void Check(bool ok, string message) { if (!ok) throw new Exception("[SaveRegression] " + message); }

    [Serializable] class Envelope { public SaveData data; }

    // 실제 로컬 소켓·HTTP 검사. 편집 모드에서 Update만 펌프하며 파일은 만들지 않는다.
    public static void RunNetwork() {
        CheckPendingDropJson();
        string url = Environment.GetEnvironmentVariable("CODEX_QA_WS_URL");
        Check(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == "ws", "격리한 로컬 서버 URL 필요");
        var root = new GameObject("NetworkSaveRegression");
        var previousNet = NetworkClient.Instance;
        var context = SynchronizationContext.Current;
        try {
            SynchronizationContext.SetSynchronizationContext(null); // 배치 메서드가 메인 스레드를 점유하므로 async I/O 연속은 스레드풀에서 처리.
            var manager = root.AddComponent<GameManager>();
            manager.Spawner = root.AddComponent<MonsterSpawner>();
            var go = new GameObject("Player"); go.transform.SetParent(root.transform);
            var player = go.AddComponent<PlayerController>();
            player.Init(false, "male", () => manager.Spawner.Alive);
            typeof(GameManager).GetField("_player", Private).SetValue(manager, player);
            var net = root.AddComponent<NetworkClient>();
            typeof(NetworkClient).GetProperty("Instance").SetValue(null, net);
            Call(manager, "BindNetwork", net);
            InvMsg last = null;
            int joins = 0;
            net.OnInv += m => { last = m; if (m.state != null) player.ApplyState(m.state); if (m.req == "join" && m.ok) joins++; };
            string account = "codex_" + Guid.NewGuid().ToString("N");
            net.Connect(url, account, "검증", 1, "male", player.GetSnapshot);
            Check(!player.GameplayReady && !net.Joined, "접속 시작 전에 게임 틱을 멈추지 않음");
            Pump(net, () => net.Joined && joins == 1);
            Check(player.GameplayReady && !manager.Loading, $"실제 join 뒤 게임 틱 재개 실패: ready={player.GameplayReady}, loading={manager.Loading}, pendingUid={last?.state?.pendingDrop?.uid}, pending={last?.state?.HasPendingDrop}");
            Check(player.Gold == 0 && player.Exp == 0, "신규 검증 계정에 기존 성장 상태가 있음");
            SpawnMsg spawn = null;
            net.OnSpawn += message => spawn = message;
            net.Request(new InvReq { type = "spawn", tier = 1, monsterId = 1001 });
            Pump(net, () => spawn != null && spawn.monsterId == 1001);
            Check(spawn.ok && !string.IsNullOrEmpty(spawn.receipt), "real spawn registration failed");
            Thread.Sleep(310);
            int seq = player.GainKillReward(1, spawn.receipt);
            Pump(net, () => last != null && last.req == "kill" && last.seq == seq);
            Check(last.ok && last.drop != null && player.Gold > 0 && player.Gold == last.drop.gold && player.Exp == last.drop.exp, $"실제 처치 보상 실패: code={last?.code}, ok={last?.ok}, drop={last?.drop != null}, gold={player.Gold}, exp={player.Exp}");
            string expected = JsonUtility.ToJson(last.state);
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) }) {
                var endpoint = new UriBuilder(uri) { Scheme = "http" }.Uri.ToString().TrimEnd('/') + "/save/" + account;
                using (var body = new StringContent("{\"potionThreshold\":50}", Encoding.UTF8, "application/json"))
                using (var response = http.PostAsync(endpoint, body).GetAwaiter().GetResult())
                    Check((int)response.StatusCode == 204, "실제 HTTP 설정 저장 실패");
                var saved = JsonUtility.FromJson<Envelope>(http.GetStringAsync(endpoint).GetAwaiter().GetResult()).data;
                Check(saved.potionThreshold == 50 && saved.gold == player.Gold && saved.exp == player.Exp, "설정 저장이 성장 상태를 변경함");
                player.ApplyPreferences(new SaveData { potionThreshold = 50, level = 1, gold = 0, weaponTier = 0 });
                Check(player.Gold == saved.gold && player.Exp == saved.exp && player.PotionHpThresholdPercent == 50, "늦은 HTTP 설정이 성장 상태를 덮음");
            }
            // 같은 객체의 연속 Connect: 이전 소켓의 종료·수신 메시지가 새 접속을 끊으면 안 된다.
            net.Connect(url, account, "검증", player.Level, "male", player.GetSnapshot);
            Check(!player.GameplayReady && !net.Joined, "재접속 대기 중 게임 틱 진행");
            net.Connect(url, account, "검증", player.Level, "male", player.GetSnapshot);
            Pump(net, () => net.Joined && joins == 2);
            Check(JsonUtility.ToJson(last.state) == expected, "재접속 권위 상태 불일치");
            var until = DateTime.UtcNow.AddMilliseconds(300);
            while (DateTime.UtcNow < until) { Call(net, "Update"); Thread.Sleep(10); }
            Check(net.Joined && player.GameplayReady && joins == 2, "옛 소켓이 새 연결을 끊음");
            Debug.Log("[SaveRegression] NETWORK PASS: real local WS join/kill, HTTP preferences, same-instance rapid reconnect; editor Update pump, in-memory server");
        } finally {
            typeof(NetworkClient).GetProperty("Instance").SetValue(null, previousNet);
            UnityEngine.Object.DestroyImmediate(root);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static void CheckPendingDropJson() {
        // JsonUtility may instantiate a nested serializable class even when the server sends null.
        var empty = JsonUtility.FromJson<InvState>("{\"pendingDrop\":null}");
        var absent = JsonUtility.FromJson<InvState>("{}");
        var zero = JsonUtility.FromJson<InvState>("{\"pendingDrop\":{\"uid\":0}}");
        var item = JsonUtility.FromJson<InvState>("{\"pendingDrop\":{\"uid\":123}}");
        Check(!empty.HasPendingDrop && !absent.HasPendingDrop && !zero.HasPendingDrop && item.HasPendingDrop, "server JSON pending-drop presence mismatch");
        Debug.Log($"[SaveRegression] pending JSON PASS: nullClassCreated={empty.pendingDrop != null}, empty / zero resumes, positive uid pauses");
    }

    static void Pump(NetworkClient net, Func<bool> done) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) {
            Call(net, "Update");
            if (done()) return;
            Thread.Sleep(10);
        }
        throw new Exception("[SaveRegression] network step timed out");
    }

    public static void Run() {
        CheckPendingDropJson();
        var root = new GameObject("SaveRegression");
        var previousNet = NetworkClient.Instance;
        try {
            var manager = root.AddComponent<GameManager>();
            manager.Spawner = root.AddComponent<MonsterSpawner>();
            var go = new GameObject("Player"); go.transform.SetParent(root.transform);
            var player = go.AddComponent<PlayerController>();
            player.Init(false, "male", () => manager.Spawner.Alive);
            typeof(GameManager).GetField("_player", Private).SetValue(manager, player);
            var net = root.AddComponent<NetworkClient>();
            typeof(NetworkClient).GetProperty("Instance").SetValue(null, net);
            Call(manager, "BindNetwork", net);
            Call(manager, "SetGameplayReady", false);
            var def = GameData.Monsters[0]; def.hp = 1000000;
            var mob = (MonsterController)Call(manager.Spawner, "Spawn", def, 0.1f);
            Call(manager, "SetGameplayReady", false);
            Check(!manager.Spawner.enabled && !mob.enabled && !mob.GetComponent<FrameAnimator>().enabled && !player.GameplayReady, "초기 게임 틱이 멈추지 않음");
            Check(net.Request(new InvReq { type = "kill", tier = 1 }) == 0 && net.SellMany(new[] { 1 }) == 0, "join 전 변경 요청 허용");
            var old = new SaveData { level = 1, gold = 0, weaponKind = "sword", weaponTier = 0, potionCount = 0, potionThreshold = 30 };
            player.ApplyPreferences(old);
            typeof(NetworkClient).GetProperty("Connected").SetValue(net, true);
            Call(net, "Dispatch", "{\"type\":\"welcome\",\"id\":\"test\",\"channel\":\"1\",\"cap\":10}");
            Check(!net.Joined && manager.Loading && !player.GameplayReady, "welcome만으로 플레이 시작");
            var state = new InvState {
                level = 35, exp = 123, gold = 9999, potions = 7, invCap = 60,
                equip = new InvEquip { weapon = 1 },
                inv = new[] { new InvItem { uid = 1, slot = "weapon", kind = "staff", tier = 7, enh = 3, level = 35 } },
            };
            bool joinedInCallback = false;
            net.OnInv += _ => joinedInCallback = net.Joined;
            Call(net, "Dispatch", JsonUtility.ToJson(new InvMsg { type = "inv", req = "join", ok = true, state = state }));
            Check(net.Joined && joinedInCallback && !manager.Loading && player.GameplayReady && manager.Spawner.enabled && mob.enabled && mob.GetComponent<FrameAnimator>().enabled, "join 권위 상태 적용 후 재개 실패");
            player.ApplyPreferences(old);
            Check(player.Level == 35 && player.Exp == 123 && player.Gold == 9999 && player.WeaponKind == "staff" && player.WeaponTierIdx == 7 && player.WeaponEnhance == 3 && player.PotionCount == 7 && player.PotionHpThresholdPercent == 30, "오래된 HTTP save가 권위 상태를 덮어씀");
            player.ApplyPreferences(new SaveData { potionThreshold = 25 });
            Check(player.PotionHpThresholdPercent == 30, "잘못된 설정 적용");
            Check(JsonUtility.ToJson(new SavePreferences { potionThreshold = 30 }) == "{\"potionThreshold\":30}", "HTTP 설정에 성장 데이터 포함");
            Call(net, "Dispatch", "{\"type\":\"closed\"}");
            int hp = player.Hp; Vector3 pos = player.transform.position;
            player.Step(1f); player.TakeDamage(999999);
            Check(player.GainKillReward(mob.Def.tier, "disconnected-must-not-send") == 0, "disconnected reward bypassed request gate");
            Check(!net.Joined && !net.Connected && !player.GameplayReady && !manager.Spawner.enabled && !mob.enabled && !player.GetComponent<FrameAnimator>().enabled && player.Hp == hp && player.transform.position == pos, "연결 끊긴 뒤 전투가 멈추지 않음");
            Check(net.Request(new InvReq { type = "kill", tier = 1 }) == 0, "연결 끊긴 뒤 처치 요청 전송");
            Call(net, "Dispatch", JsonUtility.ToJson(new InvMsg { type = "inv", req = "join", ok = true, state = state }));
            Check(!net.Joined && !player.GameplayReady, "연결 끊긴 뒤 오래된 join 메시지로 재개");
            typeof(NetworkClient).GetProperty("Connected").SetValue(net, true);
            Call(net, "Dispatch", "{\"type\":\"welcome\",\"id\":\"test\",\"channel\":\"1\",\"cap\":10}");
            Call(net, "Dispatch", "{\"type\":\"inv\",\"req\":\"join\",\"ok\":false,\"code\":\"save_failed\"}");
            Call(net, "Update");
            Check(!net.Connected && !net.Joined && !manager.Loading && !player.GameplayReady, "join 실패를 영구 접속 중으로 표시");
            Debug.Log("[SaveRegression] PASS: join authority, old HTTP preferences, request gating, game tick pause, disconnect");
        } finally {
            typeof(NetworkClient).GetProperty("Instance").SetValue(null, previousNet);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
}
