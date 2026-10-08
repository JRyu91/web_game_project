// Stage 3 모션 캡처: 배치모드 에디터에서 실제 PlayerController/MonsterController 로직을 고정 dt(1/60)로 스텝.
// 실행: -batchmode -executeMethod Game.EditorTools.Stage3Capture.Run
// 출력: ../unity_review/stage3/_frames/<name>/NNN.png (60fps, 크롭) + timing_log.txt. GIF 는 ffmpeg 후처리.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine;
using Game.Data;
using Game.Gameplay;
using Game.Rendering;
using Game.Network;

namespace Game.EditorTools {

public static class Stage3Capture {
    public static void RenderingRegression() {
        var root = new GameObject("RenderingRegression");
        try {
            var manager = root.AddComponent<GameManager>();
            var zone = ZoneController.Current != null ? ZoneController.Current.Zone.ToString() : "A";
            var roster = new[] { new RosterEntry { id = "peer", name = "원격", zone = zone, gender = "female", x = 22, face = -1 } };
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(GameManager).GetMethod("UpdateRoster", flags).Invoke(manager, new object[] { roster });
            var ghost = root.transform.Find("Ghost_peer");
            if (ghost == null || ghost.position.x != 22 || !ghost.GetComponent<SpriteRenderer>().flipX || ghost.GetComponent<SpriteRenderer>().sortingOrder != 6)
                throw new System.Exception("Roster must immediately create positioned, facing actor on gear body layer");
            var label = ghost.GetComponentInChildren<TextMesh>();
            if (label == null || !label.text.Contains("원격")) throw new System.Exception("Remote nameplate missing");
            ghost.GetComponent<FrameAnimator>().Still("idle", 0);
            ghost.GetComponent<ActorNameplate>().SendMessage("LateUpdate");
            var bbox = SpriteBBox.Get(ghost.GetComponent<SpriteRenderer>().sprite);
            if (Mathf.Abs(label.transform.localPosition.y - bbox.yMax - 0.2f) > 0.001f) throw new System.Exception("Nameplate must follow visible head");
            typeof(GameManager).GetMethod("UpdateRoster", flags).Invoke(manager, new object[] { System.Array.Empty<RosterEntry>() });
            if (root.transform.Find("Ghost_peer") != null) throw new System.Exception("Departed roster peer remains visible");
            var playerGo = new GameObject("SkillRegressionPlayer"); playerGo.transform.SetParent(root.transform);
            var player = playerGo.AddComponent<PlayerController>(); player.Init(true, "male", () => new List<MonsterController>());
            var feedback = root.AddComponent<SkillFeedback>(); feedback.Init(player, () => new MonsterController[0]);
            foreach (var asset in new[] { "obj_energybolt", "obj_firebolt", "obj_bomb", "obj_nuke" })
                if (Resources.LoadAll<Sprite>("Sprites/FX/" + asset + "/anim1").Length == 0) throw new System.Exception("Missing skill resource: " + asset);
            foreach (var skill in GameData.Skills) {
                feedback.Clear(); feedback.Cast(skill);
                if (!SkillFeedback.Supports(skill.key) || feedback.ActiveCount == 0) throw new System.Exception("Missing skill feedback: " + skill.key);
                feedback.Tick(2);
                if (feedback.ActiveCount != 0) throw new System.Exception("Skill feedback leaked: " + skill.key);
            }
            for (int i = 0; i < 40; i++) feedback.Cast(GameData.Skills[0]);
            if (feedback.ActiveCount > 32) throw new System.Exception("Skill feedback exceeded cap");
            feedback.Clear();
            Debug.Log("PASS RenderingRegression: immediate roster, facing, gear layer, nameplate, departure, ten skill effects, lifecycle cap");
        } finally { Object.DestroyImmediate(root); }
    }

    public static void MonkGroundRegression() {
        Begin(); Setup("monk_ground_B", Zone.B, "m", "sword", 60);
        var monster = Mob(12, PX + 2, 99999);
        var animator = monster.GetComponent<FrameAnimator>();
        var visual = monster.GetComponent<ActorVisual>();
        int checkedFrames = 0;
        foreach (var clip in new[] { "walk", "attack", "hurt" }) {
            for (int frame = 0; frame < animator.FrameCount(clip); frame++) {
                animator.Still(clip, frame); visual.Refresh();
                var sprite = visual.Body.sprite; var rect = sprite.textureRect; var pixels = sprite.texture.GetPixels32();
                int bottom = int.MaxValue;
                for (int y = (int)rect.yMin; y < (int)rect.yMax; y++)
                    for (int x = (int)rect.xMin; x < (int)rect.xMax; x++)
                        if (pixels[y * sprite.texture.width + x].a == 255) bottom = Mathf.Min(bottom, y);
                float localFoot = (bottom - rect.yMin - sprite.pivot.y) / sprite.pixelsPerUnit;
                float worldFoot = monster.transform.TransformPoint(new Vector3(0, localFoot, 0)).y;
                if (Mathf.Abs(worldFoot - WorldConfig.GroundY) > 0.001f)
                    throw new System.Exception($"t12 {clip}/{frame}: foot {worldFoot} ground {WorldConfig.GroundY}");
                checkedFrames++;
                if (frame == 0 || clip == "attack" && frame == 6)
                    Shot($"team_v6/f_round/code/final/monk_ground_B_{clip}_{frame}");
            }
        }
        Debug.Log($"PASS MonkGroundRegression: t12 {checkedFrames} walk/attack/hurt frames opaque feet = B GroundY {WorldConfig.GroundY}");
        ZoneController.AnimTime = 0;
    }

    public static void RenderingCapture() {
        Begin();
        foreach (var skill in GameData.Skills) {
            Setup("skill_" + skill.key, Zone.A, "m", skill.weapon);
            var monsters = new List<MonsterController> { Mob(1, PX + 2, 99999), Mob(1, PX + 4, 99999) };
            var feedback = _pl.gameObject.AddComponent<SkillFeedback>(); feedback.Init(_pl, () => monsters);
            _pl.gameObject.AddComponent<ActorNameplate>().Init("모험가 · 나", new Color(1, 0.9f, 0.6f));
            foreach (var visual in ActorVisual.All) visual.Refresh();
            feedback.Cast(skill); feedback.Tick(0.18f);
            Shot("team_v6/f_round/code/final/skill_" + skill.key);
            if (skill.key == "end_staff") { feedback.Tick(0.35f); Shot("team_v6/f_round/code/final/skill_end_staff_impact"); }
            feedback.Clear();
        }
        ZoneController.AnimTime = 0;
        Debug.Log("PASS RenderingCapture: ten skill feedback frames");
    }

    const string OUT = "../unity_review/stage3";
    const float DT = 1f / 60f;
    const float PX = 24.5f; // 화면 x≈180 빈 공간(나무·가로등 줄기와 안 겹침)
    static Camera _cam;
    static ZoneController _zc;
    static RenderTexture _rt;
    static Texture2D _tex;
    static PlayerController _pl;
    static readonly List<MonsterController> _mobs = new List<MonsterController>();
    static readonly StringBuilder _log = new StringBuilder();
    static int _f; static string _scene;

    public static void Run() { Begin(); RunAll(); }

    // E안 시스 런타임 검증: -executeMethod Game.EditorTools.Stage3Capture.Sheath → e_round/code/sheath_<g>_<무기>.png
    public static void Sheath() {
        Begin();
        foreach (var g in new[] { "m", "f" })
            foreach (var w in new[] { "낡은단검", "뇌신검", "드래곤스태프", "공허지팡이", "화염검" }) {
                bool staff = !GameData.Swords.Any(d => d.spritePath.Contains(w));
                int idx = System.Array.FindIndex(staff ? GameData.Staves : GameData.Swords, d => d.spritePath.Contains(w));
                Setup($"sheath_{g}", Zone.A, g, staff ? "staff" : "sword", 1, idx);
                for (int i = 0; i < 30; i++) Step(_scene, stepMobs: false);
                Shot($"team_v6/e_round/code/sheath_{g}_{w}");
            }
        // fx 루프: 화염검 idle, AnimTime 으로 프레임 0/중간/마지막
        Setup("sheath_fx", Zone.A, "m", "sword", 1, System.Array.FindIndex(GameData.Swords, d => d.spritePath.Contains("화염검")));
        for (int i = 0; i < 30; i++) Step(_scene, stepMobs: false);
        var gear = _pl.GetComponent<GearAttachment>();
        foreach (var (j, tag) in new[] { (0, "f0"), (4, "fmid"), (7, "flast") }) { ZoneController.AnimTime = j / 10f + 0.01f; gear.Apply(); Shot($"team_v6/e_round/code/sheath_fx_{tag}"); } // weapon_fx.json: 화염검 8프레임 10fps
        ZoneController.AnimTime = 0;
        // 보스 오라: 염제(t20) 좌/우
        foreach (var flip in new[] { false, true }) {
            Setup("aura", Zone.C, "m", "sword");
            var m = Mob(20, PX + 5, 99999);
            m.GetComponent<SpriteRenderer>().flipX = flip;
            foreach (var a in ActorVisual.All) a.Refresh();
            m.RefreshAura();
            Shot($"team_v6/e_round/code/aura_{(flip ? "L" : "R")}");
            Debug.Log($"[aura] flip={flip} bodyX={m.GetComponent<ActorVisual>().BodyX * 40:0} auraX={m.transform.Find("BossAura")?.position.x * 40:0} ground={WorldConfig.GroundY * 40:0}");
        }
        Debug.Log("[Stage3Capture] sheath done");
    }

    // E안 2단계 런타임 검증: -executeMethod Game.EditorTools.Stage3Capture.Stage2 → e_round/code/s2/*.png
    const string S2 = "team_v6/e_round/code/s2";
    static int WIdx(string w, out bool staff) {
        staff = !GameData.Swords.Any(d => d.spritePath.Contains(w));
        return System.Array.FindIndex(staff ? GameData.Staves : GameData.Swords, d => d.spritePath.Contains(w));
    }
    // 반전 진단: 캡처 직전 flipX·셰이더 + 실제 렌더(화면 bbox 좌/우 불투명 비중)
    static void LogFlip(string who, SpriteRenderer sr) {
        var b = _cam.WorldToScreenPoint(sr.bounds.min); var e = _cam.WorldToScreenPoint(sr.bounds.max);
        int x0 = Mathf.Clamp((int)b.x, 0, 1279), x1 = Mathf.Clamp((int)e.x, 0, 1279), y0 = Mathf.Clamp((int)b.y, 0, 719), y1 = Mathf.Clamp((int)e.y, 0, 719), mid = (x0 + x1) / 2;
        float l = 0, r = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { var c = _tex.GetPixel(x, y); float v = c.r + c.g + c.b; if (x < mid) l += v; else r += v; }
        Debug.Log($"[flip] {who} flipX={sr.flipX} enabled={sr.enabled} shader={sr.sharedMaterial?.shader?.name} lum L/R={l / Mathf.Max(1, r):0.000}");
    }
    // 지팡이 attack2 재캡처: -executeMethod Game.EditorTools.Stage3Capture.StaffAtk
    public static void StaffAtk() { Begin(); AtkAll(new[] { "공허지팡이", "적룡스태프", "나무지팡이", "드래곤스태프" }, "staff_atk"); ZoneController.AnimTime = 0; Debug.Log("[Stage3Capture] staff done"); }
    static void AtkAll(string[] ws, string dir) {
        foreach (var w in ws)
            foreach (var g in new[] { "m", "f" }) {
                int idx = WIdx(w, out bool st); string clip = st ? "attack2" : "attack";
                Setup("atkfx", Zone.A, g, st ? "staff" : "sword", 1, idx);
                Mob(1, PX + 2.2f, 99999);
                // 원본 전 프레임(클립 테이블에 없는 준비 프레임 포함)을 Still 로 하나씩 → GearAttachment 가 손 테이블/시스 판단
                for (int i = 0; i < 30; i++) Step(_scene, stepPlayer: false, stepMobs: false);
                var pa = _pl.GetComponent<FrameAnimator>(); var gear = _pl.GetComponent<GearAttachment>();
                for (int k = 0, n = pa.FrameCount(clip); k < n; k++) {
                    ZoneController.AnimTime = k * 0.1f;
                    pa.Still(clip, k); foreach (var a in ActorVisual.All) a.Refresh(); gear.Apply();
                    Shot($"{S2}/{dir}/{w}_{g}_{k:D2}");
                    Debug.Log($"[atk] {w} {g} #{k} " + string.Join(" ", _pl.GetComponentsInChildren<SpriteRenderer>().Select(r => $"{r.name}:{(r.enabled ? r.sortingOrder.ToString() : "-")}")));
                }
            }
    }
    // F안 D 검증: 보스 스폰 가속 시뮬 → f_round/code/boss_spawn_sim.log
    // -executeMethod Game.EditorTools.Stage3Capture.BossSpawnSim
    public static void BossSpawnSim() {
        Begin();
        CombatMath.EnsureBalance();
        var bl = new StringBuilder("tier\thp\tatk\tdef\texp\tgold\n");
        foreach (var m in GameData.Monsters) bl.Append($"{m.tier}\t{m.hp}\t{m.atk}\t{m.def}\t{m.exp}\t{m.gold}\n");
        bl.Append($"killHeal={CombatMath.KillHeal} regenDelay={CombatMath.RegenDelay} regenRate={CombatMath.RegenRate} spawnCap={CombatMath.SpawnCap}\n");
        File.WriteAllText("../unity_review/stage3/team_v6/f_round/code/balance_runtime.tsv", bl.ToString());
        var sp = new GameObject("Spawner").AddComponent<MonsterSpawner>();
        var kill = typeof(MonsterSpawner).GetMethod("CountKill", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var log = new StringBuilder();
        void KillBoss() { var b = sp.Boss; kill.Invoke(sp, new object[] { b }); Object.DestroyImmediate(b.gameObject); }
        // 1) 존별 자연 스폰 빈도: 보스 등장 즉시 처치 → 다음 굴림. 1000시간 시뮬
        foreach (var z in new[] { Zone.A, Zone.B, Zone.C }) {
            _zc.SetZone(z, Tod.Day);
            var cnt = new Dictionary<int, int> { { 19, 0 }, { 20, 0 }, { 21, 0 } };
            const int H = 1000;
            for (int sec = 0; sec < H * 3600; sec++) { int t = sp.TickBoss(1f); if (t > 0) { cnt[t]++; KillBoss(); } }
            log.Append($"zone {z} {H}h: t19={cnt[19]} (기대 {(z != Zone.A ? H : 0)}) t20={cnt[20]} (기대 {(z == Zone.C ? H / 2 : 0)}) t21={cnt[21]} (기대 {H / 24.0:0.0})\n");
        }
        // 2) 1마리 유지: 보스 살려두고 10만 초 굴림 → 추가 스폰 0, 일반 스폰 풀에 보스 없음
        _zc.SetZone(Zone.C, Tod.Day);
        int extra = 0;
        while (sp.Boss == null) sp.TickBoss(1f);
        var first = sp.Boss;
        for (int i = 0; i < 100000; i++) if (sp.TickBoss(1f) > 0) extra++;
        log.Append($"boss alive {first.Def.tier}: 추가 스폰 {extra} (기대 0), 소환 가능={sp.CanSummon(20)} (기대 False)\n");
        if (sp.Boss != null) KillBoss();
        log.Append($"C 보스 없음: can19={sp.CanSummon(19)} can20={sp.CanSummon(20)} can21={sp.CanSummon(21)} (기대 True/True/False, 카운트는 서버)\n");
        _zc.SetZone(Zone.A, Tod.Day); log.Append($"A 존 can19={sp.CanSummon(19)} (기대 False)\n");
        // 4) t21 10분 퇴장
        var spf = typeof(MonsterSpawner).GetMethod("SpawnBoss", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        spf.Invoke(sp, new object[] { 21 }); int s21 = 0;
        while (sp.Boss != null && s21 < 2000) { sp.TickBoss(1f); s21++; }
        log.Append($"t21 퇴장까지 {s21}s (기대 600)\n");
        File.WriteAllText("../unity_review/stage3/team_v6/f_round/code/boss_spawn_sim.log", log.ToString());
        Debug.Log("[BossSpawnSim]\n" + log);
    }

    // F안 C 통합 검증: 로컬 서버(node field/server.js, 인메모리)에 실제 NetworkClient 로 접속 → 처치→드랍→장착→강화→구매→재접속.
    // -executeMethod Game.EditorTools.Stage3Capture.NetITest → f_round/code/itest.log + f_round/code/ui/*.png
    const string F = "team_v6/f_round/code/ui";
    static NetworkClient NewNet() {
        var net = new GameObject("Net").AddComponent<NetworkClient>();
        typeof(NetworkClient).GetProperty("Instance").SetValue(null, net); // 에디터 모드 AddComponent 는 Awake 미호출
        return net;
    }
    static readonly System.Reflection.MethodInfo NetUpdate = typeof(NetworkClient).GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    static bool Pump(NetworkClient net, System.Func<bool> until, float sec) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < sec) { NetUpdate.Invoke(net, null); if (until()) return true; System.Threading.Thread.Sleep(15); }
        return false;
    }
    static string Brief(InvState st) => st == null ? "null" : $"Lv{st.level} exp{st.exp} gold{st.gold} stones{st.stones} potions{st.potions} inv{st.inv?.Length} equip(w{st.equip.weapon},h{st.equip.helmet},a{st.equip.armor}) k19={st.killCountT19} k20={st.killCountT20}";
    static void UiShot(GameUI ui, string name) { ui.Tick(0); Canvas.ForceUpdateCanvases(); Shot($"{F}/{name}"); }

    // 서버 응답을 흉내 낸 로컬 표시(서버 코드 무관): 강화 4결과 + 인벤·상점·소환 → f_round/code/ui_r1/*.png
    // -executeMethod Game.EditorTools.Stage3Capture.UiFake
    public static void UiFake() {
        Begin(); Setup("uifake", Zone.A, "m", "sword", 20);
        var ui = new GameObject("GameUI").AddComponent<GameUI>(); ui.Init(_cam, _pl, null, null);
        InvItem It(int uid, string slot, string kind, int tier, int enh, string name, int lv) => new InvItem { uid = uid, slot = slot, kind = kind, tier = tier, enh = enh, name = name, level = lv, sell = 10 * (lv + 5) * (1 + enh), cost = new InvCost { stones = 6, gold = 1600 } };
        var items = new[] { It(1, "weapon", "sword", 4, 7, "기사검", 20), It(2, "helmet", "", 3, 2, "철투구", 15), It(3, "armor", "", 3, 0, "사슬갑옷", 15), It(4, "weapon", "staff", 3, 0, "무쇠지팡이", 15), It(5, "weapon", "sword", 2, 1, "청동검", 10), It(6, "helmet", "", 1, 0, "가죽모자", 5) };
        InvState St() => new InvState { level = 20, exp = 3000, gold = 45210, stones = 37, potions = 4, potionPrice = 627, invCap = 60, killCountT19 = 512, killCountT20 = 230, equip = new InvEquip { weapon = 1, helmet = 2, armor = 3 }, inv = items };
        ui.OnInv(new InvMsg { req = "join", ok = true, state = St() });
        ui.SelectUid(1); ui.Show("inv"); UiShot(ui, "../ui_r3/01_inventory");
        ui.Show("enh");
        foreach (var (res, to) in new[] { ("success", 8), ("keep", 7), ("down", 6), ("destroy", -1) }) {
            ui.SelectUid(1);
            ui.OnInv(new InvMsg { req = "enhance", ok = true, enh = new InvEnh { uid = 1, from = 7, to = to, result = res, cost = new InvCost { stones = 6, gold = 1600 } }, state = St() });
            for (int f = 0; f < 3; f++) { ui.SetFxTime(f * 0.25f); UiShot(ui, $"../ui_r3/02_enh_{res}_f{f}"); }
        }
        ui.Show("shop"); UiShot(ui, "../ui_r3/03_shop");
        ui.Show("sum"); UiShot(ui, "../ui_r3/04_summon");
        ui.OnInv(new InvMsg { req = "enhance", ok = false, code = "no_stones", state = St() }); ui.Show("enh"); UiShot(ui, "../ui_r3/05_error_toast");
        Debug.Log("[uifake] done");
    }

    // F안 실제 클릭 검증: 화면 좌표 → EventSystem.RaycastAll(GraphicRaycaster) → IPointerClickHandler 실행. 버튼 함수 직접 호출 없음.
    // -executeMethod Game.EditorTools.Stage3Capture.ClickTest (로컬 서버 필요) → f_round/code/click_test.log, f_round/code/final/*.png
    static Vector2 ScreenOf(string label, out GameObject btn) {
        btn = null;
        foreach (var t in Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsSortMode.None))
            if (t.text == label && t.isActiveAndEnabled && t.GetComponentInParent<UnityEngine.UI.Button>() != null) { btn = t.GetComponentInParent<UnityEngine.UI.Button>().gameObject; break; }
        if (btn == null) return new Vector2(-1, -1);
        return RectTransformUtility.WorldToScreenPoint(_cam, ((RectTransform)btn.transform).TransformPoint(((RectTransform)btn.transform).rect.center));
    }
    static string Click(UnityEngine.EventSystems.EventSystem es, Vector2 p) {
        Canvas.ForceUpdateCanvases(); _cam.Render(); // 새로 켜진 그래픽은 한 번 그려져야 depth 가 생김(실게임은 매 프레임 렌더)
        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = p, button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
        // 에디터 모드에선 BaseRaycaster.OnEnable 이 안 돌아 RaycasterManager 가 비어 있음 → 캔버스의 GraphicRaycaster 를 직접 사용(같은 히트 테스트)
        foreach (var gr in Object.FindObjectsByType<UnityEngine.UI.GraphicRaycaster>(FindObjectsSortMode.None)) gr.Raycast(ped, hits);
        hits.Sort((x, y) => y.depth.CompareTo(x.depth));
        if (hits.Count == 0) return "miss";
        var go = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[0].gameObject);
        if (go == null) return $"no handler ({hits[0].gameObject.name})";
        ped.pointerPress = go; ped.pointerCurrentRaycast = hits[0];
        UnityEngine.EventSystems.ExecuteEvents.Execute(go, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        return $"hit {go.name}";
    }
    static int _fixtureMonsterId;
    static void ReportFixtureKill(NetworkClient net, int tier) {
        SpawnMsg spawn = null;
        int id = ++_fixtureMonsterId;
        void Registered(SpawnMsg message) { if (message.monsterId == id) spawn = message; }
        net.OnSpawn += Registered;
        try {
            // These legacy capture fixtures synthesize one monster at a time, respecting the spawn budget.
            System.Threading.Thread.Sleep(1010);
            net.Request(new InvReq { type = "spawn", tier = tier, monsterId = id });
            if (!Pump(net, () => spawn != null, 5) || !spawn.ok || string.IsNullOrEmpty(spawn.receipt))
                throw new System.Exception("Capture spawn registration failed: " + spawn?.code);
            System.Threading.Thread.Sleep(310);
            _pl.GainKillReward(tier, spawn.receipt);
        } finally { net.OnSpawn -= Registered; }
    }

    public static void ClickTest() {
        Begin();
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        var log = new StringBuilder(); void L(string x) { log.Append(x).Append('\n'); Debug.Log("[click] " + x); }
        Setup("click", Zone.A, "m", "sword");
        var net = NewNet();
        var ui = new GameObject("GameUI").AddComponent<GameUI>(); ui.Init(_cam, _pl, null, net);
        var es = Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>();
        InvMsg last = null; net.OnInv += m => last = m;
        string acc = "click_" + System.DateTime.Now.ToString("HHmmss");
        net.Connect("ws://localhost:8080", acc, "테스터", 1, "male", () => _pl.GetSnapshot());
        if (!Pump(net, () => last != null && last.req == "join", 10)) { L("FAIL join"); File.WriteAllText($"{OUT}/team_v6/f_round/code/click_test.log", log.ToString()); return; }
        var mob = Mob(1, PX + 3, 99999);
        for (int i = 0; i < 200 && _pl.Inv.inv.Length < 6; i++) { last = null; ReportFixtureKill(net, mob.Def.tier); Pump(net, () => last != null && last.req == "kill", 5); System.Threading.Thread.Sleep(340); }
        L($"준비: {Brief(_pl.Inv)}");
        UiShot(ui, "../final/00_hud");
        bool Act(string label, string expectReq) {
            var p = ScreenOf(label, out var b);
            if (b == null) { L($"[{label}] 버튼 없음/비활성"); return false; }
            bool inter = b.GetComponent<UnityEngine.UI.Button>().interactable;
            last = null; var r = Click(es, p);
            bool got = expectReq == null || Pump(net, () => last != null && last.req == expectReq, 5);
            L($"[{label}] screen=({p.x:0},{p.y:0}) {r} interactable={inter}" + (expectReq != null ? $" → {(got ? $"{last.req} ok={last.ok} code={last.code}" : "응답 없음")}" : ""));
            NetUpdate.Invoke(net, null); ui.Tick(0);
            return got;
        }
        GameObject Panel(string id) => GameObject.Find("Panel_" + id);
        Act("가방", null); L($"  가방 패널 열림={Panel("inv")?.activeSelf}");
        // 행 클릭: 미장착 장비(행 텍스트로 찾기)
        var st = _pl.Inv; var cand = st.inv.First(x => x.uid != st.equip.weapon && x.level <= st.level);
        string rowText = $"{cand.name} +{cand.enh}  Lv{cand.level}";
        Act(rowText, null); L($"  행 선택 = {cand.name} uid {cand.uid}");
        Act("장착", "equip"); L($"  equip 결과 {(_pl.Inv.equip.weapon == cand.uid || _pl.Inv.equip.helmet == cand.uid || _pl.Inv.equip.armor == cand.uid)}");
        // 다른 미장착 하나 선택 → 판매
        st = _pl.Inv; var c2 = st.inv.First(x => x.uid != st.equip.weapon && x.uid != st.equip.helmet && x.uid != st.equip.armor);
        Act($"{c2.name} +{c2.enh}  Lv{c2.level}", null); int n0 = _pl.Inv.inv.Length;
        Act("판매", "sell"); L($"  인벤 {n0}→{_pl.Inv.inv.Length}");
        // 체크 2개 → 선택 판매
        st = _pl.Inv; var free = st.inv.Where(x => x.uid != st.equip.weapon && x.uid != st.equip.helmet && x.uid != st.equip.armor).Take(2).ToArray();
        var checks = Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None).Where(b => b.gameObject.activeInHierarchy && ((RectTransform)b.transform).sizeDelta.x == 30).ToList();
        // 행 순서 = 인벤 순서 → 체크 버튼은 같은 y 의 행 옆
        foreach (var it in free) {
            ScreenOf($"{(st.equip.weapon == it.uid ? "[E] " : "")}{it.name} +{it.enh}  Lv{it.level}", out var row);
            var chk = checks.OrderBy(b => Mathf.Abs(b.transform.position.y - row.transform.position.y)).First();
            var p = RectTransformUtility.WorldToScreenPoint(_cam, chk.transform.position + (Vector3)((RectTransform)chk.transform).rect.center * 0);
            L($"[체크 {it.name} uid {it.uid}] {Click(es, RectTransformUtility.WorldToScreenPoint(_cam, ((RectTransform)chk.transform).TransformPoint(((RectTransform)chk.transform).rect.center)))}");
            ui.Tick(0);
        }
        UiShot(ui, "../final/01_inventory_multiselect");
        n0 = _pl.Inv.inv.Length; long g0 = _pl.Inv.gold;
        Act("선택 판매 (2)", "sell"); L($"  인벤 {n0}→{_pl.Inv.inv.Length} gold {g0}→{_pl.Inv.gold} sold=[{string.Join(",", last?.sell?.sold ?? new int[0])}]");
        Act("X", null); L($"  X 후 가방 패널 열림={Panel("inv")?.activeSelf == true}");
        Act("가방", null); var w = _pl.Inv.inv.First(x => x.uid == _pl.Inv.equip.weapon);
        Act($"[E] {w.name} +{w.enh}  Lv{w.level}", null); Act("강화하기", null); L($"  강화 패널 열림={Panel("enh")?.activeSelf == true}");
        Act("강화 시도", "enhance"); UiShot(ui, "../final/02_enhance");
        Act("상점", null); int p0 = _pl.Inv.potions;
        Act("포션 1개", "buy"); L($"  포션 {p0}→{_pl.Inv.potions}"); UiShot(ui, "../final/03_shop");
        Act("소환", null); Act("드래곤 소환", "summon"); L($"  (카운트 {_pl.Inv.killCountT19}/500 → 비활성 버튼은 클릭 무시가 정상)"); UiShot(ui, "../final/04_summon");
        Act("X", null); L($"  X 후 소환 패널 열림={Panel("sum")?.activeSelf == true}");
        UiShot(ui, "../final/05_full");
        typeof(NetworkClient).GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(net, null);
        File.WriteAllText($"{OUT}/team_v6/f_round/code/click_test.log", log.ToString());
        Debug.Log("[click] done");
    }

    public static void NetITest() {
        Begin();
        System.Threading.SynchronizationContext.SetSynchronizationContext(null); // async 연속을 스레드풀로(에디터 메서드가 메인 스레드를 잡고 있음)
        var log = new StringBuilder(); void L(string x) { log.Append(x).Append('\n'); Debug.Log("[itest] " + x); }
        Setup("itest", Zone.A, "m", "sword");
        var net = NewNet();
        var ui = new GameObject("GameUI").AddComponent<GameUI>(); ui.Init(_cam, _pl, null, net);
        InvMsg last = null; net.OnInv += m => last = m;
        string acc = "itest_" + System.DateTime.Now.ToString("HHmmss");
        net.Connect("ws://localhost:8080", acc, "테스터", 1, "male", () => _pl.GetSnapshot());
        if (!Pump(net, () => last != null && last.req == "join", 10)) { L("FAIL join 응답 없음 (서버 켜져 있나?)"); File.WriteAllText($"{OUT}/team_v6/f_round/code/itest.log", log.ToString()); return; }
        L($"join: {Brief(last.state)}");
        UiShot(ui, "00_join_hud");
        // 처치 N회 (서버 속도 제한 3/s → 0.36s 간격)
        var mob = Mob(1, PX + 3, 99999); int drops = 0, stonesGot = 0, rate = 0; const int N = 150;
        int i = 0;
        for (; i < N || (_pl.Inv.stones < 6 && i < 900); i++) { // 강화 3회분(강화석 6) 모일 때까지 연장
            last = null; ReportFixtureKill(net, mob.Def.tier);
            Pump(net, () => last != null && last.req == "kill", 5);
            if (last == null) { L($"kill {i}: 응답 없음"); continue; }
            if (!last.ok) { if (last.code == "rate") rate++; else L($"kill {i}: {last.code}"); }
            else { if (last.drop.item != null && last.drop.item.uid > 0) { drops++; L($"kill {i}: 드랍 {last.drop.item.name}(uid {last.drop.item.uid}, {last.drop.item.slot}, Lv{last.drop.item.level}) sold={last.drop.sold}"); } stonesGot += last.drop.stones; if (last.drop.levelUp > 0) L($"kill {i}: 레벨업 → Lv{last.state.level}"); }
            System.Threading.Thread.Sleep(340);
        }
        L($"처치 {i}: 드랍 {drops}, 강화석 +{stonesGot}, rate 거절 {rate} → {Brief(_pl.Inv)} / 클라 Lv{_pl.Level} gold{_pl.Gold}");
        // 장착: 레벨 맞는 미장착 장비 하나
        var st = _pl.Inv; var cand = st.inv.FirstOrDefault(x => x.uid != st.equip.weapon && x.uid != st.equip.helmet && x.uid != st.equip.armor && x.level <= st.level);
        if (cand != null) {
            last = null; net.Request(new InvReq { type = "equip", slot = cand.slot, uid = cand.uid }); Pump(net, () => last != null && last.req == "equip", 5);
            L($"equip {cand.name}({cand.slot}) ok={last?.ok} code={last?.code} → equip(w{_pl.Inv.equip.weapon},h{_pl.Inv.equip.helmet},a{_pl.Inv.equip.armor}) 클라 무기={_pl.CurrentWeapon.name} 투구idx={_pl.HelmetTierIdx} 갑옷idx={_pl.ArmorTierIdx}");
            ui.SelectUid(cand.uid);
        } else L("equip: 후보 없음(드랍 없음)");
        ui.Show("inv"); UiShot(ui, "01_inventory");
        // 강화: 장착 무기
        int wuid = _pl.Inv.equip.weapon; ui.SelectUid(wuid); ui.Show("enh"); UiShot(ui, "02_enhance_before");
        var results = new Dictionary<string, int>();
        for (int k = 0; k < 6; k++) {
            last = null; net.Request(new InvReq { type = "enhance", uid = wuid }); Pump(net, () => last != null && last.req == "enhance", 5);
            if (last == null) { L("enhance 응답 없음"); break; }
            if (!last.ok) { L($"enhance #{k}: {last.code} ({Brief(last.state)})"); break; }
            ui.OnInv(last); // GameUI 도 OnInv 구독 중이지만 결과 FX 캡처 위해 명시 재적용 없이 상태만 확인
            results[last.enh.result] = results.TryGetValue(last.enh.result, out var c) ? c + 1 : 1;
            L($"enhance #{k}: +{last.enh.from}→{last.enh.to} {last.enh.result} cost(stones {last.enh.cost.stones}, gold {last.enh.cost.gold})");
            for (int f = 0; f < 3; f++) { ui.SetFxTime(f * 0.3f); UiShot(ui, $"03_enhance_{k}_{last.enh.result}_f{f}"); }
            if (last.enh.result == "destroy") wuid = _pl.Inv.equip.weapon;
        }
        L("강화 결과 분포: " + string.Join(", ", results.Select(kv => $"{kv.Key}={kv.Value}")));
        // 상점
        long g0 = _pl.Inv.gold; int p0 = _pl.Inv.potions;
        last = null; net.Request(new InvReq { type = "buy", item = "potion", qty = 1 }); Pump(net, () => last != null && last.req == "buy", 5);
        L($"buy potion x1 ok={last?.ok} code={last?.code} potions {p0}→{_pl.Inv.potions} gold {g0}→{_pl.Inv.gold} (가격 {_pl.Inv.potionPrice})");
        ui.Show("shop"); UiShot(ui, "04_shop");
        // 포션 사용
        last = null; _pl.TakeDamage(9999 * 0 + _pl.MaxHp / 2); int hp0 = _pl.Hp; net.Request(new InvReq { type = "potion" }); Pump(net, () => last != null && last.req == "potion", 5);
        L($"potion use ok={last?.ok} hp {hp0}→{_pl.Hp}/{_pl.MaxHp} potions→{_pl.Inv.potions}");
        // 소환 (카운트 부족 → not_enough 기대)
        last = null; net.Request(new InvReq { type = "summon", tier = 19 }); Pump(net, () => last != null && last.req == "summon", 5);
        L($"summon 19 ok={last?.ok} code={last?.code} (기대 not_enough, k19={_pl.Inv.killCountT19})");
        ui.Show("sum"); UiShot(ui, "05_summon");
        // 재접속 복원
        string before = Brief(_pl.Inv);
        typeof(NetworkClient).GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(net, null);
        Object.DestroyImmediate(net.gameObject); System.Threading.Thread.Sleep(500);
        var net2 = NewNet(); last = null; net2.OnInv += m => last = m;
        net2.Connect("ws://localhost:8080", acc, "테스터", 1, "male", () => _pl.GetSnapshot());
        Pump(net2, () => last != null && last.req == "join", 10);
        string after = Brief(last?.state);
        L($"재접속 전: {before}\n재접속 후: {after}\n복원 일치={before == after}");
        typeof(NetworkClient).GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(net2, null);
        File.WriteAllText($"{OUT}/team_v6/f_round/code/itest.log", log.ToString());
        Debug.Log("[itest] done");
    }

    public static void Stage2() {
        Begin();
        // 1) 좌우 반전: 몬스터 비대칭 확인용 t20·t2, 플레이어 face
        foreach (int t in new[] { 20, 2 })
            foreach (var flip in new[] { false, true }) {
                Setup("flip", t == 20 ? Zone.C : Zone.A, "m", "sword");
                if (!flip) Shot($"{S2}/flip_t{t}_bg"); // 배경만(반전 판정 마스크용)
                var m = Mob(t, PX + 5, 99999); m.GetComponent<SpriteRenderer>().flipX = flip;
                foreach (var a in ActorVisual.All) a.Refresh(); m.RefreshAura();
                Shot($"{S2}/flip_t{t}_{(flip ? "L" : "R")}"); LogFlip($"t{t}", m.GetComponent<SpriteRenderer>());
            }
        foreach (var face in new[] { 1, -1 }) {
            Setup("face", Zone.A, "m", "sword", 1, WIdx("화염검", out _)); // fx 무기로 무기·fx 반전까지
            if (face > 0) { _pl.gameObject.SetActive(false); Shot($"{S2}/face_player_bg"); _pl.gameObject.SetActive(true); } _pl.GetComponent<GearAttachment>().SetFace(face);
            for (int i = 0; i < 10; i++) Step(_scene, stepMobs: false);
            Shot($"{S2}/face_player_{(face > 0 ? "R" : "L")}"); LogFlip("player", _pl.GetComponent<SpriteRenderer>()); foreach (var w in _pl.GetComponentsInChildren<SpriteRenderer>()) if (w.name.StartsWith("Weapon")) LogFlip(w.name, w);
        }
        // 2) 6종 대기 프레임 전(walk0)/후(walk 중간)
        foreach (int t in new[] { 7, 8, 9, 13, 14, 17 }) {
            var zn = t <= 7 ? Zone.A : t <= 14 ? Zone.B : Zone.C;
            Setup("idle", zn, "m", "sword");
            var m0 = Mob(t, PX + 4, 99999); var m1 = Mob(t, PX + 9, 99999);
            var a0 = m0.GetComponent<FrameAnimator>(); var a1 = m1.GetComponent<FrameAnimator>();
            a0.Still("walk", 0); a1.Still("walk", a1.FrameCount("walk") / 2); // MonsterController.IdleFrame 과 같은 식
            foreach (var a in ActorVisual.All) a.Refresh();
            Shot($"{S2}/idle_t{t}_before_after");
        }
        // 3) 포털·스피너·보스 HP 바 (GameManager 배선 그대로)
        Setup("world", Zone.A, "m", "sword");
        var gm = new GameObject("GM").AddComponent<GameManager>();
        gm.SetupWorld(_cam, PX + 9);
        for (int i = 0; i < 4; i++) { gm.TickWorld(i * 0.13f, null); Shot($"{S2}/portal_spinner_{i}"); }
        Object.DestroyImmediate(gm.gameObject); foreach (var n in new[] { "Portal", "Spinner", "BossHpBar" }) { var o = GameObject.Find(n); if (o) Object.DestroyImmediate(o); }
        Setup("bosshp", Zone.C, "m", "sword");
        gm = new GameObject("GM").AddComponent<GameManager>(); gm.SetupWorld(_cam, -100); gm.Loading = false;
        var boss = Mob(20, PX + 6, 99999); boss.Hp = boss.MaxHp * 6 / 10;
        foreach (var a in ActorVisual.All) a.Refresh(); boss.RefreshAura();
        gm.TickWorld(0, boss); Shot($"{S2}/boss_hp_full");
        Object.DestroyImmediate(gm.gameObject); foreach (var n in new[] { "Portal", "Spinner", "BossHpBar" }) { var o = GameObject.Find(n); if (o) Object.DestroyImmediate(o); }
        // 4) 지팡이 시스 재캡처
        foreach (var g in new[] { "m", "f" })
            foreach (var w in new[] { "드래곤스태프", "공허지팡이" }) {
                int idx = WIdx(w, out bool st); Setup("sheath", Zone.A, g, st ? "staff" : "sword", 1, idx);
                for (int i = 0; i < 30; i++) Step(_scene, stepMobs: false);
                Shot($"{S2}/sheath_{g}_{w}");
            }
        // 5) 공격 fx: 검=attack, 지팡이=attack2 전 프레임(첫 공격 1회)
        AtkAll(new[] { "화염검", "뇌명검", "명왕검", "최후의검", "공허지팡이", "적룡스태프" }, "atk");
        ZoneController.AnimTime = 0;
        Debug.Log("[Stage3Capture] stage2 done");
    }

    static void Begin() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Directory.CreateDirectory(OUT);
        _cam = new GameObject("Cam").AddComponent<Camera>();
        _cam.orthographic = true; _cam.orthographicSize = 9f;
        _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = Color.black;
        _rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        _cam.targetTexture = _rt;
        _tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
        _zc = new GameObject("Zone").AddComponent<ZoneController>();
        _zc.Cam = _cam;
        ZoneController.AnimTime = 0;
        PlayerController.CombatLog = s => _log.Append($"{_scene}\tf{_f:D3}\t{_f * DT * 1000:0}ms\t{s}\n");
    }

    static void RunAll() {

        // 무기 단계: idle 들기 방식 3안 비교(걷기는 walk 재생성 대기라 제외)
        foreach (var g in new[] { "m", "f" }) {
            foreach (var cs in new[] { CarryStyle.Down, CarryStyle.Sheath }) {
                Setup($"carry_{cs.ToString().ToLower()}_{g}", Zone.A, g, "sword"); _pl.GetComponent<GearAttachment>().Carry = cs;
                for (int i = 0; i < 90; i++) Step(_scene, stepMobs: false);
            }
            Attack($"atk_sword_{g}", g, "sword");
            Attack($"atk_staff_{g}", g, "staff");
            Attack($"atk_sword_{g}_left", g, "sword", true);
            Attack($"atk_staff_{g}_left", g, "staff", true);
        }
        foreach (var z in new[] { Zone.A, Zone.B, Zone.C }) ZoneShot(z);
        BossLineup();
        MobCycle("mob_A_t2", Zone.A, 2);
        MobCycle("mob_B_t8", Zone.B, 8);
        MobCycle("mob_C_t13", Zone.C, 13);
        File.WriteAllText($"{OUT}/timing_log.txt", _log.ToString());
        PlayerController.CombatLog = null;
        Debug.Log("[Stage3Capture] done");
    }

    static void Setup(string scene, Zone z, string g, string weapon, int level = 1, int tier = 0) {
        Clear();
        _scene = scene; _f = 0;
        _zc.SetZone(z, Tod.Day); _zc.CameraXOverride = 36; _zc.Layout(null); _cam.Render(); _cam.Render();
        var go = new GameObject("Player");
        go.transform.position = new Vector3(PX, 0, 0);
        _pl = go.AddComponent<PlayerController>();
        _pl.WeaponKind = weapon; _pl.WeaponTierIdx = tier; _pl.Level = level;
        _pl.Init(true, g == "f" ? "female" : "male", () => _mobs);
    }

    static MonsterController Mob(int tier, float x, int hp) {
        var def = GameData.Monsters.First(m => m.tier == tier);
        var m = new GameObject($"t{tier}").AddComponent<MonsterController>();
        m.Init(def, new Vector3(x, 0, 0), x - 1, x + 1);
        m.Hp = m.MaxHp = hp;
        _mobs.Add(m);
        return m;
    }

    // 몬스터 1마리 상대 3회 공격(+대기).
    static void Attack(string name, string g, string weapon, bool left = false) {
        Setup(name, Zone.A, g, weapon);
        if (left) _pl.transform.position = new Vector3(PX + 4f, 0, 0); // 왼쪽 보기: 몬스터를 왼쪽에
        var m = Mob(1, left ? PX + 1.8f : PX + 2.2f, 99999);
        int frames = Mathf.CeilToInt((3 * (1f / 1.5f) + 0.2f) / DT);
        for (int i = 0; i < frames; i++) Step(name); // 몬스터도 실제 로직(피격 쿨다운 포함)
    }

    static void Walk(string name, string g) {
        Setup(name, Zone.A, g, "sword");
        var m = Mob(1, 60f, 99999); // 멀리 → 계속 걷기
        for (int i = 0; i < 90; i++) Step(name, stepMobs: false); // 1.5s ≈ 2사이클(720ms)
        Clear(); Setup(name, Zone.A, g, "sword");                 // 이어서 idle 2사이클
        _f = 90; _scene = name;
        for (int i = 0; i < 90; i++) Step(name, stepMobs: false);
    }

    // 몬스터 공격(플레이어 플래시) → 플레이어가 2타로 처치(hurt, dead, 페이드)
    static void MobCycle(string name, Zone z, int tier) {
        Setup(name, z, "m", "sword", z == Zone.A ? 10 : z == Zone.B ? 60 : 95);
        var m = Mob(tier, PX + 1.6f, 1);
        for (int i = 0; i < 60; i++) Step(name, stepPlayer: false);    // 1s: 몬스터 공격 1회
        m.Hp = 2 * 99999; int hp0 = m.Hp;
        bool first = true;
        for (int i = 0; i < 200 && (_mobs.Count > 0 || i < 30); i++) {
            Step(name);
            if (first && m != null && m.Hp < hp0) { first = false; m.Hp = 1; } // 첫 타는 hurt, 다음 타에 사망
            _mobs.RemoveAll(x => x == null);
        }
    }

    // 존 C 라인업: 플레이어, t18, t19, t20, t21 (walk 0, 발라인 일렬) — 발/HUD 확인
    static void BossLineup() {
        Setup("boss_lineup_C", Zone.C, "m", "sword");
        float x = PX * 40 + 80;
        foreach (int t in new[] { 18, 19, 20, 21 }) {
            var m = Mob(t, 0, 99999);
            float hw = m.GetComponent<ActorVisual>().HalfWidth * 40;
            x += hw + 16; m.transform.position = new Vector3(Mathf.Round(x) / 40f, 0, 0); x += hw + 16;
            m.GetComponent<SpriteRenderer>().flipX = true;
            PlayerController.CombatLog?.Invoke($"lineup t{t} scale={ActorScale.Tier(t)} root={ActorScale.MonsterRoot(t)} h={m.GetComponent<SpriteRenderer>().sprite.rect.height * ActorScale.Tier(t)}");
        }
        foreach (var a in ActorVisual.All) a.Refresh();
        Shot("boss_lineup_C");
        foreach (var m in _mobs) Object.DestroyImmediate(m.gameObject);
        _mobs.Clear();
    }

    // 존별 대표 컷(x800 카메라, 플레이어 idle + 무기, 몬스터 3마리)
    static void ZoneShot(Zone z) {
        Setup($"{z}_x800_weapon", z, "m", "sword", 30);
        int[] tiers = z == Zone.A ? new[] { 1, 4, 6 } : z == Zone.B ? new[] { 7, 10, 12 } : new[] { 13, 16, 18 };
        float[] xs = { 37f, 42.5f, 48.25f };
        for (int i = 0; i < 3; i++) { var m = Mob(tiers[i], xs[i], 99999); m.GetComponent<SpriteRenderer>().flipX = true; }
        _pl.GetComponent<GearAttachment>().Apply();
        foreach (var a in ActorVisual.All) a.Refresh();
        Shot($"{z}_x800_weapon");
    }

    static void Step(string name, bool stepPlayer = true, bool stepMobs = true) {
        _mobs.RemoveAll(x => x == null);
        if (stepPlayer) _pl.Step(DT);
        foreach (var m in _mobs.ToArray()) if (m != null && stepMobs) m.Step(DT);
        foreach (var a in Object.FindObjectsByType<FrameAnimator>(FindObjectsSortMode.None)) {
            if (!stepPlayer && a.gameObject == _pl.gameObject) continue;
            a.Tick(DT);
        }
        _mobs.RemoveAll(x => x == null);
        ActorVisual.All.RemoveAll(a => a == null);
        foreach (var a in ActorVisual.All) { a.TickFlash(DT); a.Refresh(); }
        foreach (var gr in Object.FindObjectsByType<GearAttachment>(FindObjectsSortMode.None)) gr.Apply();
        var pa = _pl.GetComponent<FrameAnimator>();
        _log.Append($"{_scene}\tf{_f:D3}\t{_f * DT * 1000:0}ms\tplayer {pa.Clip}[{pa.SourceIndex}]{(pa.Frozen ? " FROZEN" : "")}\n");
        Shot($"_frames/{name}/{_f:D3}");
        _f++;
    }

    static void Shot(string name) {
        _cam.Render();
        RenderTexture.active = _rt;
        _tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); _tex.Apply();
        RenderTexture.active = null;
        string path = $"{OUT}/{name}.png";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, _tex.EncodeToPNG());
    }

    static void Clear() {
        foreach (var m in _mobs) if (m != null) Object.DestroyImmediate(m.gameObject);
        _mobs.Clear();
        if (_pl != null) Object.DestroyImmediate(_pl.gameObject);
        ActorVisual.All.RemoveAll(a => a == null);
    }
}
}
