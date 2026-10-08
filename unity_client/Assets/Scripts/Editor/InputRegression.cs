using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Game.Gameplay;
using Game.Network;

namespace Game.EditorTools {
public static class InputRegression {
    static Mouse _mouse;
    static Vector2 _position;
    static int _step, _nextFrame;
    static double _deadline;
    static bool _started, _oldOptionsEnabled;
    static EnterPlayModeOptions _oldOptions;
    static InputSettings.BackgroundBehavior _oldBackground;
    static InputSettings.EditorInputBehaviorInPlayMode _oldEditorBehavior;

    // Batch: -executeMethod Game.EditorTools.InputRegression.Run (omit -quit).
    // Actual PlayMode frames handle input/EventSystem; no direct click dispatch or manual Process.
    public static void Run() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _started = false; _step = 0;
        _deadline = EditorApplication.timeSinceStartup + 30;
        _oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        _oldOptions = EditorSettings.enterPlayModeOptions;
        _oldBackground = InputSystem.settings.backgroundBehavior;
        _oldEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorApplication.playModeStateChanged += PlayState;
        EditorApplication.update += Tick;
        EditorApplication.EnterPlaymode();
    }

    public static void Systems() {
        Stats();
        CombatRegression.Run();
        CombatRegression.RunAnimation();
        ProjectileRegression.Run();
        DropRegression.Run();
        Stage3Capture.RenderingRegression();
        Stage3Capture.RenderingCapture();
        Run();
    }

    static void PlayState(PlayModeStateChange state) {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        try {
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            var cam = new GameObject("InputCamera").AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 9;
            cam.targetTexture = new RenderTexture(1280, 720, 24);
            var player = new GameObject("Player").AddComponent<PlayerController>();
            player.Init(false, "male", () => Array.Empty<MonsterController>());
            var net = new GameObject("Network").AddComponent<NetworkClient>();
            CheckDisposal(cam, player, net);
            var ui = new GameObject("GameUI").AddComponent<GameUI>();
            ui.Init(cam, player, null, net);
            ui.OnInv(new InvMsg { req = "join", ok = true, state = new InvState {
                level = 1, potions = 3, invCap = 60, equip = new InvEquip { weapon = 1 },
                inv = new[] { new InvItem { uid = 1, slot = "weapon", kind = "sword", tier = 0, name = "낡은단검", cost = new InvCost { stones = 2, gold = 400 } } }
            } });
            ui.Tick(0); // Server state must redraw successfully while all panels remain hidden.
            Debug.Log("[InputRegression] hidden inventory redraw PASS");
            CheckRequestGate(ui, net);
            CheckSummonResponses(ui);
            CheckInventory(ui);
            var es = UnityEngine.Object.FindAnyObjectByType<EventSystem>();
            var module = es.GetComponent<InputSystemUIInputModule>();
            if (module == null || es.GetComponent<StandaloneInputModule>() != null ||
                module.point == null || module.leftClick == null ||
                !module.point.action.enabled || !module.leftClick.action.enabled)
                throw new Exception("GameUI input module/actions not initialized by real lifecycle");
            _mouse = InputSystem.AddDevice<Mouse>();
            Canvas.ForceUpdateCanvases();
            var button = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
                .Single(b => b.GetComponentInChildren<Text>().text == "가방");
            var rect = (RectTransform)button.transform;
            _position = RectTransformUtility.WorldToScreenPoint(cam, rect.TransformPoint(rect.rect.center));
            _nextFrame = Time.frameCount + 2;
            _started = true;
        } catch (Exception error) { Finish(false, error.ToString()); }
    }

    static void CheckDisposal(Camera cam, PlayerController player, NetworkClient net) {
        var inv = typeof(NetworkClient).GetField("OnInv", BindingFlags.Instance | BindingFlags.NonPublic);
        if (inv == null) throw new Exception("Cannot inspect NetworkClient.OnInv subscriptions");
        var font = typeof(Font).GetField("textureRebuilt", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < 3; i++) {
            var ui = new GameObject("DisposalUI").AddComponent<GameUI>();
            ui.Init(cam, player, null, net, isAdmin: i == 2);
            bool hasAdmin = ui.GetComponentsInChildren<Text>(true).Any(t => t.text == "관리");
            if (hasAdmin != (i == 2)) throw new Exception("Admin menu role mismatch");
            if (ui.GetComponentsInChildren<Text>(true).Any(t => t.supportRichText)) throw new Exception("Untrusted chat/name rich text remains enabled");
            if (CountTarget(inv.GetValue(net) as Delegate, ui) != 1) throw new Exception("Live UI subscription count is not 1");
            UnityEngine.Object.DestroyImmediate(ui.gameObject);
            if (CountTarget(inv.GetValue(net) as Delegate, ui) != 0) throw new Exception("Destroyed UI remains subscribed to network");
            if (font != null && CountTarget(font.GetValue(null) as Delegate, ui) != 0) throw new Exception("Destroyed UI remains subscribed to font");
        }
        Debug.Log("[InputRegression] disposal PASS x3; Font delegate " + (font != null ? "checked" : "not introspectable, unverified"));
    }
    static int CountTarget(Delegate callbacks, object target) => callbacks?.GetInvocationList().Count(d => ReferenceEquals(d.Target, target)) ?? 0;

    // UI presentation only: readiness flags are simulated; server/Dispatch handshake has separate checks.
    static void CheckRequestGate(GameUI ui, NetworkClient net) {
        var buy = ui.GetComponentsInChildren<Button>(true).Single(b => b.GetComponentInChildren<Text>(true).text == "포션 1개");
        var connected = typeof(NetworkClient).GetProperty("Connected");
        var joined = typeof(NetworkClient).GetProperty("Joined");
        net.enabled = false; // Simulated connectivity must not attempt real socket writes.
        try {
            ui.Tick(0);
            if (buy.interactable) throw new Exception("Offline purchase enabled");
            connected.SetValue(net, true); joined.SetValue(net, false); ui.Tick(0);
            if (buy.interactable) throw new Exception("Purchase enabled before authoritative join");
            joined.SetValue(net, true); ui.Tick(0);
            if (!buy.interactable) throw new Exception("Purchase stayed disabled after join");
            connected.SetValue(net, false); joined.SetValue(net, false); ui.Tick(0);
            if (buy.interactable) throw new Exception("Purchase stayed enabled after disconnect");
            Debug.Log("[InputRegression] purchase gate PASS: offline/pre-join disabled, joined enabled, disconnected disabled (simulated readiness flags)");
        } finally {
            connected.SetValue(net, false); joined.SetValue(net, false); net.enabled = true;
        }
    }

    static void CheckInventory(GameUI ui) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var state = new InvState { level = 1, gold = 1000, invCap = 60,
            equip = new InvEquip { weapon = 2, helmet = 3 },
            inv = new[] {
                new InvItem { uid = 1, slot = "weapon", kind = "sword", tier = 0, name = "미장착" },
                new InvItem { uid = 2, slot = "weapon", kind = "sword", tier = 0, name = "장착검" },
                new InvItem { uid = 3, slot = "helmet", tier = 0, name = "장착투구" },
                new InvItem { uid = 4, slot = "armor", tier = 0, name = "미장착갑옷" }
            } };
        ui.OnInv(new InvMsg { req = "join", ok = true, state = state }); ui.SelectUid(4);
        var rows = (System.Collections.Generic.List<Button>)typeof(GameUI).GetField("_rows", flags).GetValue(ui);
        if (!rows[0].GetComponentInChildren<Text>(true).text.Contains("장착검") || !rows[1].GetComponentInChildren<Text>(true).text.Contains("장착투구")) throw new Exception("Equipped items are not first");
        if (rows[0].image.color.g <= rows[0].image.color.r) throw new Exception("Equipped item has no green highlight");
        typeof(GameUI).GetMethod("Check", flags).Invoke(ui, new object[] { 0 });
        var selected = (System.Collections.Generic.HashSet<int>)typeof(GameUI).GetField("_checked", flags).GetValue(ui);
        if (selected.Count != 0 || state.inv[0].uid != 1) throw new Exception("Equipped selection allowed or authoritative inventory reordered");
        ui.OnInv(new InvMsg { req = "kill", seq = 1000, ok = true, state = state, drop = new InvDrop { gold = 3, disassembled = 2 } });
        var toast = (Text)typeof(GameUI).GetField("_toast", flags).GetValue(ui);
        if (!toast.text.Contains("자동 분해") || !toast.text.Contains("+2")) throw new Exception("Auto disassemble feedback absent");
        ui.OnInv(new InvMsg { req = "kill", seq = 1001, ok = true, state = state, drop = new InvDrop { gold = 3, sold = 50 } });
        if (!toast.text.Contains("자동 판매 +50G")) throw new Exception("Auto sell feedback absent");
        if (ui.GetComponentsInChildren<Text>(true).Any(t => t.text == "관리")) throw new Exception("Regular user has admin menu");
        Debug.Log("[InputRegression] equipped sorting/color/protection, policy notifications, ordinary admin UI hidden PASS");
    }

    static void CheckSummonResponses(GameUI ui) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo F(string name) => typeof(GameUI).GetField(name, flags);
        F("_pendingSummon").SetValue(ui, 19); F("_summonSeq").SetValue(ui, 100);
        ui.OnInv(new InvMsg { req = "summon", seq = 99, ok = false, code = "not_enough" });
        if ((int)F("_pendingSummon").GetValue(ui) != 19) throw new Exception("Late summon response changed current reservation");
        F("_summonAck").SetValue(ui, true); F("_summonAckSeq").SetValue(ui, 101);
        ui.OnInv(new InvMsg { req = "summon_ack", seq = 101, ok = false, code = "save_failed" });
        if (!(bool)F("_summonAck").GetValue(ui) || (float)F("_summonRetry").GetValue(ui) <= 0) throw new Exception("Save failure lost retry reservation");
        ui.OnInv(new InvMsg { req = "summon_ack", seq = 100, ok = true });
        if (!(bool)F("_summonAck").GetValue(ui)) throw new Exception("Late ACK released current reservation");
        ui.OnInv(new InvMsg { req = "summon_ack", seq = 101, ok = false, code = "no_pending" });
        if ((bool)F("_summonAck").GetValue(ui) || (int)F("_pendingSummon").GetValue(ui) != 0) throw new Exception("Terminal ACK failure permanently locked summon button");
        F("_pendingSummon").SetValue(ui, 19); F("_summonMonsterId").SetValue(ui, 44);
        var spawnHandler = typeof(GameUI).GetMethod("OnSummonSpawn", flags);
        spawnHandler.Invoke(ui, new object[] { new SpawnMsg { monsterId = 43, tier = 19, ok = true, receipt = "old" } });
        if ((bool)F("_summonAck").GetValue(ui)) throw new Exception("Unrelated spawn acknowledged current summon");
        spawnHandler.Invoke(ui, new object[] { new SpawnMsg { monsterId = 44, tier = 19, ok = false, code = "spawn_full" } });
        if (!(bool)F("_summonAck").GetValue(ui) || (bool)F("_summonSpawned").GetValue(ui)) throw new Exception("Rejected registration consumed summon as a successful spawn");
        F("_pendingSummon").SetValue(ui, 0); F("_summonAck").SetValue(ui, false);
        Debug.Log("[InputRegression] summon sequence / save retry / terminal unlock / spawn rejection PASS");
    }

    static void Tick() {
        if (EditorApplication.timeSinceStartup > _deadline) { Finish(false, "PlayMode input check timed out after 30s"); return; }
        if (!_started || !EditorApplication.isPlaying || Time.frameCount < _nextFrame) return;
        try {
            switch (_step++) {
                case 0: Queue(false); break;
                case 1: Queue(true); break;
                case 2: Queue(false); break;
                case 3:
                    if (GameObject.Find("Panel_inv") == null) throw new Exception("Mouse press/release did not open inventory");
                    Queue(true); break;
                case 4: Queue(false); break;
                case 5:
                    if (GameObject.Find("Panel_inv") != null) throw new Exception("Second mouse click did not close inventory");
                    Finish(true, "queued MouseState -> real PlayMode input actions/EventSystem -> inventory open/close; no manual Process/ExecuteEvents, no OS mouse");
                    break;
            }
            _nextFrame = Time.frameCount + 2;
        } catch (Exception error) { Finish(false, error.ToString()); }
    }

    // Presentation fixtures use authoritative state shape; no network or economy writes.
    public static void Stats() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(1280, 656), new Vector2Int(393, 852), new Vector2Int(852, 393) }) {
            var root = new GameObject("StatsRegression");
            var rt = new RenderTexture(size.x, size.y, 24);
            var image = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            try {
                var cam = root.AddComponent<Camera>(); cam.orthographic = true; cam.orthographicSize = 9;
                cam.targetTexture = rt; cam.backgroundColor = new Color(0.12f, 0.2f, 0.16f);
                var actor = new GameObject("Player"); actor.transform.SetParent(root.transform);
                var pl = actor.AddComponent<PlayerController>(); pl.Init(false, "male", () => Array.Empty<MonsterController>());
                var ui = new GameObject("GameUI").AddComponent<GameUI>(); ui.transform.SetParent(root.transform); ui.Init(cam, pl, null, null);
                var state = new InvState { level = 20, exp = 3000, gold = 45210, stones = 37, potions = 4,
                    str = 30, dex = 10, luk = 10, statPoints = 45,
                    equip = new InvEquip { weapon = 1, helmet = 2 }, skills = new[] { Game.Data.GameData.Skills[0].key },
                    inv = new[] { new InvItem { uid = 1, slot = "weapon", kind = "sword", tier = 4, enh = 7 },
                        new InvItem { uid = 2, slot = "helmet", tier = 3, enh = 2 } } };
                ui.OnInv(new InvMsg { req = "join", ok = true, state = state }); ui.Show("stats"); ui.Tick(.3f);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var values = (System.Collections.Generic.Dictionary<string, Text>)typeof(GameUI).GetField("_statValues", flags).GetValue(ui);
                if (values["class"].text != "전사" || values["gold"].text != "45,210 G" || values["exp"].text != "3,000 / 12,000 (25.0%)") throw new Exception("Stats authoritative state display mismatch");
                int expectedDef = 10 + Game.Data.CombatMath.GearDefWithEnhance(Game.Data.GameData.Helmets[3], 2);
                if (values["defense"].text != expectedDef.ToString("N0")) throw new Exception("Stats enhanced defense mismatch");
                pl.TakeDamage(20); ui.Tick(.3f);
                if (values["hp"].text != $"{pl.Hp:N0} / {pl.MaxHp:N0}") throw new Exception("Stats live HP did not update");
                Canvas.ForceUpdateCanvases();
                var panel = ui.GetComponentsInChildren<RectTransform>(true).Single(r => r.name == "Panel_stats");
                var corners = new Vector3[4]; panel.GetWorldCorners(corners);
                foreach (var corner in corners) {
                    var point = RectTransformUtility.WorldToScreenPoint(cam, corner);
                    if (point.x < -1 || point.x > size.x + 1 || point.y < -1 || point.y > size.y + 1) throw new Exception("Stats panel exceeds viewport");
                }
                cam.Render(); cam.Render(); RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); image.Apply(); RenderTexture.active = null;
                System.IO.File.WriteAllBytes($"Logs/stats-{size.x}x{size.y}.png", image.EncodeToPNG());
                if (size.x < 1000) {
                    var scroll = panel.GetComponentInChildren<ScrollRect>();
                    scroll.verticalNormalizedPosition = 0; Canvas.ForceUpdateCanvases();
                    var lastSkill = values["skill_" + Game.Data.GameData.Skills.Last().key].rectTransform;
                    var viewportCorners = new Vector3[4]; scroll.viewport.GetWorldCorners(viewportCorners); lastSkill.GetWorldCorners(corners);
                    if (corners[0].y < viewportCorners[0].y - .01f || corners[1].y > viewportCorners[1].y + .01f) throw new Exception("Last skill cannot be reached by scrolling");
                }
                ui.Show("allocate"); Canvas.ForceUpdateCanvases(); cam.Render(); cam.Render();
                RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); image.Apply(); RenderTexture.active = null;
                System.IO.File.WriteAllBytes($"Logs/allocate-{size.x}x{size.y}.png", image.EncodeToPNG());
                var allocationText = (Text)typeof(GameUI).GetField("_allocationInfo", flags).GetValue(ui);
                if (!allocationText.text.Contains("45")) throw new Exception("Allocation points do not reflect server state");
                state.inv[0].kind = "staff"; state.level = Game.Data.GameData.LEVEL_MAX;
                ui.OnInv(new InvMsg { req = "join", ok = true, state = state }); ui.Tick(.3f);
                if (values["class"].text != "마법사" || !pl.Penetrating || values["exp"].text != "MAX") throw new Exception("Stats staff/max-level display mismatch");
                if (state.gold != 45210 || state.stones != 37 || state.potions != 4) throw new Exception("Stats mutated economy");
                Debug.Log($"[StatsRegression] PASS {size}: state, enhanced defense, live HP, staff/MAX, panel bounds, economy unchanged");
                cam.targetTexture = null;
            } finally {
                RenderTexture.active = null; UnityEngine.Object.DestroyImmediate(root); rt.Release();
                UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }

    static void Queue(bool down) => InputSystem.QueueStateEvent(_mouse, new MouseState { position = _position, buttons = (ushort)(down ? 1 : 0) });

    static void Finish(bool passed, string message) {
        _started = false;
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= PlayState;
        if (_mouse != null) { InputSystem.RemoveDevice(_mouse); _mouse = null; }
        InputSystem.settings.backgroundBehavior = _oldBackground;
        InputSystem.settings.editorInputBehaviorInPlayMode = _oldEditorBehavior;
        EditorSettings.enterPlayModeOptions = _oldOptions;
        EditorSettings.enterPlayModeOptionsEnabled = _oldOptionsEnabled;
        if (passed) Debug.Log("[InputRegression] PASS: " + message);
        else Debug.LogError("[InputRegression] FAIL: " + message);
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
}
