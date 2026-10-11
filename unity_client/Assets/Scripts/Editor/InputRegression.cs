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
            CheckEffects();
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

    static void CheckEffects() {
        foreach (var effect in new[] { (key: "obj_firepillar", frames: 9, fps: 12f, scale: 1f, order: 10), (key: "fx_boss_warning", frames: 8, fps: 10f, scale: 2f, order: 4) }) {
            var frames = Game.Rendering.HitFx.Frames("Sprites/FX/" + effect.key + (effect.key == "obj_firepillar" ? "/anim1" : ""));
            if (frames.Length != effect.frames) throw new Exception("FX frame count: " + effect.key);
            var pos = new Vector3(40, Game.Data.WorldConfig.GroundY + (effect.key == "obj_firepillar" ? 1.6f : .6f), 0);
            var go = Game.Rendering.HitFx.Play(effect.key, pos, effect.fps, effect.scale, false, effect.order);
            if (go == null) throw new Exception("FX Play failed: " + effect.key);
            var sr = go.GetComponent<SpriteRenderer>();
            if (go.transform.position != pos || go.transform.localScale.x != effect.scale || sr.sortingOrder != effect.order)
                throw new Exception("FX anchor/layer changed: " + effect.key);
            for (int i = 0; i < frames.Length; i++) {
                if (i > 0) go.GetComponent<Game.Rendering.HitFx>().Tick(1f / effect.fps + .00001f);
                if (sr.sprite != frames[i] || sr.sprite.rect.width != 160 || sr.sprite.rect.height != 160 || sr.sprite.pixelsPerUnit != 40)
                    throw new Exception("FX sequence/canvas/PPU: " + effect.key + " frame " + i);
            }
            go.GetComponent<Game.Rendering.HitFx>().Tick(1f / effect.fps + .00001f);
        }
        Debug.Log("[EffectsRegression] actual Play/Tick frames, anchors, scale, order, PPU PASS");
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
        var player = (PlayerController)typeof(GameUI).GetField("_pl", flags).GetValue(ui);
        var helmet = player.GetComponentsInChildren<SpriteRenderer>(true).First(sr => sr.name == "Helmet");
        if (!helmet.enabled || helmet.sprite == null || helmet.sprite.texture == null) throw new Exception("Wearable sprite cache survived Play Mode with destroyed objects");
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
                case 0:
                    if (GameObject.Find("Fx_obj_firepillar") != null || GameObject.Find("Fx_fx_boss_warning") != null)
                        throw new Exception("FX did not disappear after its last frame");
                    Debug.Log("[EffectsRegression] lifetime/destruction PASS");
                    Queue(false); break;
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

    public static void EnhancementLayout() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root = new GameObject("EnhancementLayoutRegression");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var targets = new[] { new Vector2Int(1600, 1000), new Vector2Int(402, 874), new Vector2Int(852, 393), new Vector2Int(720, 393), new Vector2Int(402, 874), new Vector2Int(1600, 1000) };
        var cam = root.AddComponent<Camera>(); cam.orthographic = true; cam.orthographicSize = 9;
        var actor = new GameObject("Player"); actor.transform.SetParent(root.transform);
        var pl = actor.AddComponent<PlayerController>(); pl.Init(false, "male", () => Array.Empty<MonsterController>());
        var net = root.AddComponent<NetworkClient>(); net.enabled = false;
        var ui = new GameObject("GameUI").AddComponent<GameUI>(); ui.transform.SetParent(root.transform);
        RectTransform FieldRect(string name) {
            var value = typeof(GameUI).GetField(name, flags).GetValue(ui);
            return value is RectTransform rect ? rect : (RectTransform)((Component)value).transform;
        }
        Rect Bounds(RectTransform child, RectTransform parent) {
            var corners = new Vector3[4]; child.GetWorldCorners(corners);
            var points = corners.Select(c => parent.InverseTransformPoint(c)).ToArray();
            return Rect.MinMaxRect(points.Min(c => c.x), points.Min(c => c.y), points.Max(c => c.x), points.Max(c => c.y));
        }
        void Inside(RectTransform child, RectTransform parent) {
            var b = Bounds(child, parent); var p = parent.rect;
            if (b.xMin < p.xMin - .1f || b.xMax > p.xMax + .1f || b.yMin < p.yMin - .1f || b.yMax > p.yMax + .1f)
                throw new Exception($"{child.name} exceeds {parent.name}: {b} / {p}");
        }
        RenderTexture rt = null;
        try {
            rt = new RenderTexture(targets[0].x, targets[0].y, 24); cam.targetTexture = rt; ui.Init(cam, pl, null, net);
            foreach (var size in targets) {
                cam.targetTexture = null; rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                rt = new RenderTexture(size.x, size.y, 24); cam.targetTexture = rt;
                typeof(GameUI).GetMethod("ResponsiveLayout", flags).Invoke(ui, null);
                var preview = FieldRect("_enhPreview"); var button = FieldRect("_bEnhTry"); var result = FieldRect("_enhResult");
                ui.Show("enh"); Canvas.ForceUpdateCanvases();
                var parent = (RectTransform)preview.parent;
                if (Bounds(preview, parent).Overlaps(Bounds(button, parent)) || Bounds(preview, parent).Overlaps(Bounds(result, parent)))
                    throw new Exception("Enhancement preview overlaps action/result");
                Inside(preview, parent); Inside(button, parent); Inside(result, parent);
                foreach (var kind in new[] { "sword", "staff" }) {
                    int count = kind == "staff" ? Game.Data.GameData.Staves.Length : Game.Data.GameData.Swords.Length;
                    for (int tier = 0; tier < count; tier++) {
                        var item = new InvItem { uid = 1, slot = "weapon", kind = kind, tier = tier, enh = 14, cost = new InvCost { stones = 1, gold = 1 } };
                        ui.OnInv(new InvMsg { req = "join", ok = true, state = new InvState { level = 100, equip = new InvEquip { weapon = 1 }, inv = new[] { item } } });
                        typeof(GameUI).GetField("_sel", flags).SetValue(ui, 1);
                        typeof(GameUI).GetMethod("Redraw", flags).Invoke(ui, null);
                        Canvas.ForceUpdateCanvases(); Inside(FieldRect("_icon"), preview);
                        var icon = FieldRect("_icon").GetComponent<Image>();
                        if (icon.sprite == null || icon.rectTransform.sizeDelta != icon.sprite.rect.size) throw new Exception("Enhancement icon lost native size");
                    }
                }
                foreach (var outcome in new[] { "success", "down", "destroy" }) {
                    typeof(GameUI).GetMethod("ShowEnh", flags).Invoke(ui, new object[] { new InvEnh { result = outcome, from = 14, to = 15 } });
                    int visibleFrames = 0;
                    for (int frame = 0; frame < 24; frame++) { ui.SetFxTime(frame / 10f); Canvas.ForceUpdateCanvases(); if (FieldRect("_fx").GetComponent<Image>().enabled) { visibleFrames++; Inside(FieldRect("_fx"), preview); } }
                    if (visibleFrames == 0) throw new Exception($"Enhancement {outcome} FX missing");
                }
                ui.Show("inv"); Canvas.ForceUpdateCanvases();
                var groups = (System.Collections.Generic.List<RectTransform>)typeof(GameUI).GetField("_autoGroups", flags).GetValue(ui);
                if (groups.Count != 2 || groups[0].parent != groups[1].parent || Bounds(groups[0], (RectTransform)groups[0].parent).Overlaps(Bounds(groups[1], (RectTransform)groups[1].parent))) throw new Exception("Automatic processing groups overlap/split");
                foreach (var group in groups) foreach (var child in group.GetComponentsInChildren<RectTransform>(true).Where(r => r.parent == group)) Inside(child, group);
                Debug.Log($"[EnhancementLayout] PASS {size}: all weapon icons, result FX, grouped automatic controls, resize transitions");
            }
        } finally { cam.targetTexture = null; UnityEngine.Object.DestroyImmediate(root); if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); } }
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
                var fixtureNet = root.AddComponent<NetworkClient>(); fixtureNet.enabled = false;
                typeof(NetworkClient).GetProperty("Connected").SetValue(fixtureNet, true);
                typeof(NetworkClient).GetProperty("Joined").SetValue(fixtureNet, true);
                var ui = new GameObject("GameUI").AddComponent<GameUI>(); ui.transform.SetParent(root.transform); ui.Init(cam, pl, null, fixtureNet);
                var state = new InvState { level = 20, exp = 3000, gold = 45210, stones = 37, potions = 4,
                    str = 30, dex = 10, luk = 10, statPoints = 45,
                    equip = new InvEquip { weapon = 1, helmet = 2 }, skills = new[] { Game.Data.GameData.Skills[0].key },
                    inv = new[] { new InvItem { uid = 1, slot = "weapon", kind = "sword", tier = 4, enh = 7 },
                        new InvItem { uid = 2, slot = "helmet", tier = 3, enh = 2 } } };
                ui.OnInv(new InvMsg { req = "join", ok = true, state = state }); ui.Show("stats"); ui.Tick(.3f);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var values = (System.Collections.Generic.Dictionary<string, Text>)typeof(GameUI).GetField("_statValues", flags).GetValue(ui);
                if (values["class"].text != "전사" || values["gold"].text != "45,210 G" || values["exp"].text != "3,000 / 5,624 (53.3%)") throw new Exception("Stats authoritative state display mismatch");
                int expectedDef = 10 + Game.Data.CombatMath.GearDefWithEnhance(Game.Data.GameData.Helmets[3], 2);
                if (values["defense"].text != expectedDef.ToString("N0")) throw new Exception("Stats enhanced defense mismatch");
                var hud = (System.Collections.Generic.Dictionary<string, Text>)typeof(GameUI).GetField("_cooldownHud", flags).GetValue(ui);
                var skillKey = Game.Data.GameData.Skills[0].key;
                if (!hud.ContainsKey(skillKey) || !hud[skillKey].text.Contains("준비")) throw new Exception("Skill cooldown HUD missing ready state");
                var timers = (System.Collections.Generic.Dictionary<string, float>)typeof(PlayerController).GetField("_skillCd", flags).GetValue(pl);
                timers[skillKey] = 7.5f; ui.Tick(.3f);
                if (!hud[skillKey].text.Contains("8초")) throw new Exception("Skill HUD does not reflect actual cooldown");
                Canvas.ForceUpdateCanvases();
                var hudCorners = new Vector3[4]; ((RectTransform)hud[skillKey].transform.parent).GetWorldCorners(hudCorners);
                foreach (var corner in hudCorners) {
                    var point = RectTransformUtility.WorldToScreenPoint(cam, corner);
                    if (point.x < 0 || point.x > size.x || point.y < 0 || point.y > size.y) throw new Exception("Skill HUD exceeds viewport");
                }
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
                var growthDot = (Text)typeof(GameUI).GetField("_growthDot", flags).GetValue(ui);
                if (!growthDot.gameObject.activeSelf) throw new Exception("Unspent points growth badge missing");
                var allocationButtons = (System.Collections.Generic.List<Button>)typeof(GameUI).GetField("_allocationButtons", flags).GetValue(ui);
                if (!allocationButtons.All(b => b.interactable) || allocationButtons.Count != 8 || allocationButtons[1].GetComponentInChildren<Text>().text != "+10") throw new Exception("Ten point allocation button missing");
                state.statPoints = 0; ui.OnInv(new InvMsg { req = "allocate_stat", ok = true, state = state });
                if (growthDot.gameObject.activeSelf || allocationButtons.Any(b => b.interactable)) throw new Exception("Empty points badge/button state incorrect");
                state.statPoints = 9; ui.OnInv(new InvMsg { req = "join", ok = true, state = state });
                if (!allocationButtons.Where((b, i) => i % 2 == 0).All(b => b.interactable) || allocationButtons.Where((b, i) => i % 2 == 1).Any(b => b.interactable)) throw new Exception("Ten point button enabled below ten points");

                state.inv[0].kind = "staff"; state.level = Game.Data.GameData.LEVEL_MAX;
                ui.OnInv(new InvMsg { req = "join", ok = true, state = state }); ui.Tick(.3f);
                if (values["class"].text != "마법사" || !pl.Penetrating || values["exp"].text != "MAX") throw new Exception("Stats staff/max-level display mismatch");
                if (hud[skillKey].transform.parent.gameObject.activeSelf) throw new Exception("Sword HUD remains after staff switch");
                state.skills = Game.Data.GameData.Skills.Select(s => s.key).ToArray();
                ui.OnInv(new InvMsg { req = "join", ok = true, state = state }); ui.Show(""); ui.Tick(.3f);
                if (hud.Values.Count(t => t.transform.parent.gameObject.activeSelf) != 5) throw new Exception("Skill HUD does not show five current weapon skills");
                foreach (var text in hud.Values.Where(t => t.transform.parent.gameObject.activeSelf)) {
                    ((RectTransform)text.transform.parent).GetWorldCorners(hudCorners);
                    foreach (var corner in hudCorners) {
                        var point = RectTransformUtility.WorldToScreenPoint(cam, corner);
                        if (point.x < 0 || point.x > size.x || point.y < 0 || point.y > size.y) throw new Exception("Five skill HUD exceeds viewport");
                    }
                }
                Canvas.ForceUpdateCanvases(); cam.Render(); cam.Render(); RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); image.Apply(); RenderTexture.active = null;
                System.IO.File.WriteAllBytes($"Logs/skill-hud-{size.x}x{size.y}.png", image.EncodeToPNG());
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
