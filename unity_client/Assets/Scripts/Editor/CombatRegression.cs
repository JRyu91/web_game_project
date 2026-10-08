using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Game.Data;
using Game.Gameplay;
using Game.Rendering;
using Game.Network;

namespace Game.EditorTools {

// 배치 호출: -executeMethod Game.EditorTools.CombatRegression.Run (편집 모드의 실제 전투 메서드 검증).
public static class CombatRegression {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static FieldInfo Field(object obj, string name) => obj.GetType().GetField(name, Private);
    static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private).Invoke(obj, args);
    static void Check(bool condition, string message) { if (!condition) throw new Exception("[CombatRegression] " + message); }

    public static void Run() {
        CheckGrowth();
        CheckAdaptiveCamera();
        CheckSkyGroundCoverage();
        CheckGhostRoster();
        CheckPatrolAndPopulation();
        var root = new GameObject("CombatRegression");
        try {
            var spawner = root.AddComponent<MonsterSpawner>();
            var playerGo = new GameObject("RegressionPlayer");
            playerGo.transform.SetParent(root.transform);
            var player = playerGo.AddComponent<PlayerController>();
            player.WeaponKind = "staff"; player.Level = 100;
            player.Init(false, "male", () => spawner.Alive);
            var transportGo = new GameObject("TransportRegressionPlayer");
            transportGo.transform.SetParent(root.transform);
            var transportPlayer = transportGo.AddComponent<PlayerController>();
            transportPlayer.Init(false, "male", () => Array.Empty<MonsterController>());
            try { CheckTransport(transportPlayer); }
            finally { UnityEngine.Object.DestroyImmediate(transportGo); }
            var skills = GameData.Skills.Where(s => s.weapon == "staff" && s.lv <= player.Level).Take(2).ToArray();
            Check(skills.Length == 2, "두 스킬 검증 데이터 부족");
            var pending = (List<SkillDef>)Field(player, "_pendingSkills").GetValue(player);
            Call(player, "UpdateSkills", 0f);
            Check(pending.Count == 0, "미습득 스킬이 자동 시전 대기열에 들어감");
            GrantSkills(player, skills.Select(skill => skill.key).ToArray());
            Check(skills.All(skill => player.CanCast(skill)), "습득·레벨·무기 충족 스킬이 잠김");
            Check(!player.CanCast(GameData.Skills.First(skill => skill.weapon == "sword")), "다른 무기 스킬을 시전 가능");
            var def = GameData.Monsters[0]; def.hp = 1000000; def.def = 100;
            var target = (MonsterController)Call(spawner, "Spawn", def, 0.1f);
            const int seed = 123;
            Field(player, "_rng").SetValue(player, new System.Random(seed));
            var rng = new System.Random(seed);
            int basic = CombatMath.Mitigate(CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng), def.def);
            int expected = basic;
            foreach (var skill in skills) {
                int atk = CombatMath.PlayerFixedAtk(player.Level) + CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng);
                expected += CombatMath.Mitigate(Mathf.Max(1, Mathf.RoundToInt(atk * skill.mult)), def.def);
            }
            pending.AddRange(skills);
            Call(player, "StartAttack", target);
            Call(player, "Impact");
            Check(target.Hp == def.hp - expected + basic, "지팡이 기본 공격이 비행 전에 즉시 피해를 넣음");
            Call(player, "TickProjectiles", 0.2f);
            Check(target.Hp == def.hp - expected, "투사체 기본 공격 + 두 스킬의 개별 방어력 적용 불일치");
            Check(pending.Count == 0, "시전 후 대기 스킬이 남음");
            var cds = (Dictionary<string, float>)Field(player, "_skillCd").GetValue(player);
            Check(skills.All(s => cds[s.key] == s.cd), "스킬 쿨다운 미적용");
            Check(!target.TakeDamage(100), "피격 쿨다운의 중복 방지가 사라짐");
            Field(target, "_hitCooldownTimer").SetValue(target, 0f);
            int hp = target.Hp;
            Call(player, "Impact");
            Check(target.Hp == hp, "동일 공격의 두 번째 Impact가 피해를 넣음");
            Check(target.TakeDamage(2000000) && target.IsDead, "피격 쿨다운 종료 뒤 공격이 적용되지 않음");
            UnityEngine.Object.DestroyImmediate(target.gameObject);

            def.hp = 1; def.def = 0;
            var mobs = Enumerable.Range(0, 3).Select(i => (MonsterController)Call(spawner, "Spawn", def, 0.1f + i * 0.1f)).ToArray();
            int deaths = 0;
            spawner.OnMonsterDied += _ => deaths++;
            // 첫 처치의 콜백이 다른 대상도 처치해도 남은 대상 적용과 목록 순회가 안전해야 한다.
            mobs[0].OnDied += _ => mobs[1].TakeDamage(999);
            pending.AddRange(skills);
            Call(player, "StartAttack", mobs[0]);
            Call(player, "Impact");
            Check(mobs.All(m => m.IsDead) && deaths == 3 && spawner.Alive.Count == 0, "광역 동시 처치·콜백 처치 후 목록 제거 실패");

            pending.AddRange(skills);
            Call(player, "StartAttack", (object)null);
            Call(player, "Impact");
            Check(pending.Count == 0, "대상이 없는 Impact 처리 실패");
            Debug.Log("[CombatRegression] PASS: basic+2skills, per-hit mitigation, cooldown, duplicate impact, multi-kill, callback kill, empty targets");
        } finally {
            UnityEngine.Object.DestroyImmediate(root);
        }
        RunAnimation();
    }

    static void CheckSkyGroundCoverage() {
        var root = new GameObject("CoverageRegression");
        float oldGround = WorldConfig.GroundY;
        var texture = new RenderTexture(1280, 1240, 24);
        try {
            var cameraGo = new GameObject("Camera"); cameraGo.transform.SetParent(root.transform);
            var cam = cameraGo.AddComponent<Camera>(); cam.orthographic = true; cam.orthographicSize = 15.5f; cam.aspect = 1280f / 1240;
            cam.targetTexture = texture;
            var zoneGo = new GameObject("Zone"); zoneGo.transform.SetParent(root.transform);
            var zone = zoneGo.AddComponent<ZoneController>(); zone.Cam = cam;
            foreach (Zone map in Enum.GetValues(typeof(Zone))) {
                zone.SetZone(map, Tod.Day); zone.Layout(10);
                float top = cam.transform.position.y + cam.orthographicSize, bottom = cam.transform.position.y - cam.orthographicSize;
                var background = zone.GetComponentsInChildren<SpriteRenderer>().Where(sr => map == Zone.C ? sr.name.StartsWith("back_wall_") : sr.name == "sky").ToArray();
                Check(background.Length > 0 && background.All(sr => sr.bounds.min.y <= bottom + 0.001f && sr.bounds.max.y >= top - 0.001f), $"{map} tall background does not cover viewport");
                var fills = root.GetComponentsInChildren<SpriteRenderer>().Where(sr => sr.name == "GroundExtension").ToArray();
                Check(fills.Length == 2 && fills.All(sr => sr.enabled && sr.bounds.min.y <= bottom + 0.001f), $"{map} tall ground has a bottom gap");
                Check(fills.All(sr => sr.drawMode == SpriteDrawMode.Tiled && sr.transform.localScale == Vector3.one && sr.sprite.rect.height == 32), "ground extension stretches a pixel row instead of tiling the original texture");
                var ground = root.GetComponentsInChildren<SpriteRenderer>().Where(sr => sr.name.StartsWith("ground_")).ToArray();
                Check(ground.All(sr => sr.transform.localScale == Vector3.one), "original ground art was stretched");
            }
            Debug.Log("[CombatRegression] coverage PASS: A/B/C 1280x1240 sky and bottom-ground coverage, original ground scale unchanged");
        } finally { WorldConfig.GroundY = oldGround; UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(texture); }
    }

    static void CheckGhostRoster() {
        var root = new GameObject("GhostRegression");
        try {
            var manager = root.AddComponent<GameManager>(); manager.Spawner = root.AddComponent<MonsterSpawner>();
            var entry = new RosterEntry { id = "remote_qa", x = 10, face = 1, name = "원격검사", gender = "female", zone = "A", equip = new EquipMsg { weapon = new EquipWeaponMsg { kind = "staff", tier = 0 } } };
            Call(manager, "UpdateRoster", (object)new[] { entry, entry, new RosterEntry { id = "" }, new RosterEntry { id = "other_map", zone = "B" }, new RosterEntry { id = "bot", zone = "A", bot = true } });
            Check(manager.Spawner.MapMonsterCap == 5, "different-map player / bot / duplicate inflated monster capacity");
            Call(manager, "UpdateGhost", entry.id, 10f, 1);
            var ghosts = (Dictionary<string, GameObject>)Field(manager, "_ghosts").GetValue(manager);
            Check(ghosts.Count == 1, "duplicate / empty roster ids created extra ghosts");
            var ghost = ghosts[entry.id]; var anim = ghost.GetComponent<FrameAnimator>();
            Check(anim.SourceKey == "main_f" && anim.Clip == "idle", "remote gender / first-position idle mismatch");
            Check(ghost.GetComponentInChildren<TextMesh>().text.Contains(entry.name), "remote identity label missing");
            Check(ghost.GetComponentsInChildren<SpriteRenderer>().Any(sr => sr.name == "Weapon" && sr.sprite != null), "roster equipment not attached");
            Call(manager, "UpdateGhost", entry.id, 11f, -1);
            Check(anim.Clip == "walk" && ghost.GetComponent<SpriteRenderer>().flipX, "position movement / facing not reflected");
            Call(manager, "UpdateGhost", entry.id, 11f, -1);
            Check(anim.Clip == "idle", "stationary remote keeps walking");
            Call(manager, "UpdateRoster", (object)Array.Empty<RosterEntry>());
            Check(ghosts.Count == 0 && ghost == null, "departed remote object remains in scene");
            Call(manager, "UpdateGhost", entry.id, 12f, 1);
            Check(ghosts.Count == 0, "late position resurrected a departed remote");
            Call(manager, "UpdateRoster", (object)new[] { entry }); Call(manager, "UpdateGhost", entry.id, 10f, 1);
            Call(manager, "ClearGhosts");
            Check(ghosts.Count == 0 && ((Dictionary<string, RosterEntry>)Field(manager, "_roster").GetValue(manager)).Count == 0 && manager.Spawner.MaxAlive == 0, "channel reset retains remote metadata / monster capacity");
            Debug.Log("[CombatRegression] ghost PASS: roster gender / gear / name, movement-only walk, departed cleanup, late pos rejection and channel reset");
        } finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    static void CheckPatrolAndPopulation() {
        var root = new GameObject("PatrolPopulationRegression");
        var previousLocal = PlayerController.Local;
        try {
            var playerGo = new GameObject("Player"); playerGo.transform.SetParent(root.transform);
            var player = playerGo.AddComponent<PlayerController>(); player.Init(true, "male", () => Array.Empty<MonsterController>());
            var mobGo = new GameObject("PatrolMob"); mobGo.transform.SetParent(root.transform);
            var mob = mobGo.AddComponent<MonsterController>();
            var def = GameData.Monsters[0]; def.hp = 1000000;
            mob.Init(def, new Vector3(12, 0), 10, 12); player.transform.position = new Vector3(50, 0);
            mob.Step(0.1f);
            Check(mob.transform.position.x < 11.95f, "far player overrides endpoint patrol direction / stationary walking");
            for (int i = 0; i < 10; i++) mob.Step(0.1f);
            Check(mob.transform.position.x < 11.5f, "patrol did not continue moving away from endpoint");
            mob.transform.position = new Vector3(15, mob.transform.position.y); player.transform.position = new Vector3(21, 0);
            mob.Step(0.1f); float chased = mob.transform.position.x;
            Check(chased > 15, "fixture did not chase outside patrol range");
            player.transform.position = new Vector3(50, 0); mob.Step(0.1f);
            Check(mob.transform.position.x < chased && chased - mob.transform.position.x < 0.07f && mob.transform.position.x > 12, "chase-to-patrol teleported instead of returning smoothly");
            for (int i = 0; i < 80; i++) mob.Step(0.1f);
            Check(mob.transform.position.x >= 10 && mob.transform.position.x <= 12, "monster failed to return to its patrol range");
            var spawner = root.AddComponent<MonsterSpawner>();
            Call(spawner, "SpawnBoss", 19);
            for (int i = 0; i < 7; i++) Call(spawner, "Spawn", def, 10f + i);
            spawner.SetPopulation(2);
            Check(spawner.MapMonsterCap == 10 && spawner.MaxAlive == 5 && spawner.Alive.Count == 5 && spawner.Boss != null, "per-player five slots / total population cap / boss counting failed");
            spawner.SetPopulation(1);
            Check(spawner.MapMonsterCap == 5 && spawner.Alive.Count == 5, "departed player's slots inflated map limit");
            spawner.SetPopulation(0);
            Check(spawner.MaxAlive == 0 && spawner.MapMonsterCap == 0 && spawner.Alive.Count == 0 && spawner.Boss == null, "disconnect retained encounters or boss");
            Check(spawner.TickBoss(1000000) == 0 && spawner.Alive.Count == 0, "zero population spawned a boss");
            Debug.Log("[CombatRegression] patrol/population PASS: far-player edge, chase return, five slots/player, boss included, departure and zero population");
        } finally { typeof(PlayerController).GetProperty("Local").SetValue(null, previousLocal); UnityEngine.Object.DestroyImmediate(root); }
    }

    static void CheckAdaptiveCamera() {
        var root = new GameObject("AdaptiveCameraRegression");
        float oldGround = WorldConfig.GroundY;
        try {
            var cameraGo = new GameObject("Camera"); cameraGo.transform.SetParent(root.transform);
            var cam = cameraGo.AddComponent<Camera>(); cam.orthographic = true; cam.orthographicSize = 9;
            var pixel = cameraGo.AddComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();
            pixel.assetsPPU = 40; pixel.refResolutionX = 1280; pixel.refResolutionY = 720;
            var zone = root.AddComponent<ZoneController>(); zone.Cam = cam;
            var playerGo = new GameObject("LayoutPlayer"); playerGo.transform.SetParent(root.transform);
            var player = playerGo.AddComponent<PlayerController>(); player.Init(false, "male", () => Array.Empty<MonsterController>());
            var uiGo = new GameObject("LayoutUI"); uiGo.transform.SetParent(root.transform);
            var ui = uiGo.AddComponent<GameUI>(); ui.Init(cam, player, null, null);
            foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(1280, 638), new Vector2Int(1920, 1018), new Vector2Int(393, 788), new Vector2Int(852, 329), new Vector2Int(2560, 1440) }) {
                var texture = new RenderTexture(size.x, size.y, 24);
                cam.targetTexture = texture; cam.aspect = (float)size.x / size.y;
                foreach (float ground in new[] { -6.475f, -5.925f, -5.875f }) {
                    WorldConfig.GroundY = ground; zone.Layout(10);
                    var feet = cam.WorldToViewportPoint(new Vector3(cam.transform.position.x, ground, 0));
                    float expected = 0.5f + ground / 18f;
                    Check(Mathf.Abs(feet.y - expected) < 0.003f && feet.y > 0.1f, $"adaptive footline clipped: {size}, y={feet.y}");
                    Check(ground + 3.7f < cam.transform.position.y + cam.orthographicSize, "148px player body clipped above viewport");
                }
                ui.Tick(0);
                float uiZoom = ui.GetComponent<UnityEngine.UI.CanvasScaler>().scaleFactor;
                float worldZoom = size.y / (2f * cam.orthographicSize * pixel.assetsPPU);
                Check(Mathf.Abs(uiZoom - worldZoom) < 0.001f, $"HUD / world scale mismatch: {size}, UI={uiZoom}, world={worldZoom}");
                bool compact = size.x / uiZoom < 1000 || size.y / uiZoom < 640;
                var scrolls = ui.GetComponentsInChildren<UnityEngine.UI.ScrollRect>(true);
                Check(scrolls.Any(scroll => scroll.gameObject.activeSelf) == compact, $"compact scroll mismatch: {size}");
                if (compact) {
                    var menus = (List<UnityEngine.UI.Button>)Field(ui, "_menus").GetValue(ui);
                    Check(menus.All(button => ((RectTransform)button.transform).sizeDelta.y * uiZoom >= 44), "compact menu touch target below 44 pixels");
                    var panels = (Dictionary<string, GameObject>)Field(ui, "_panels").GetValue(ui);
                    Check(panels.Values.All(panel => ((RectTransform)panel.transform).sizeDelta.y * uiZoom <= size.y - 19), "compact panel exceeds short viewport");
                }
                cam.targetTexture = null; UnityEngine.Object.DestroyImmediate(texture);
            }
            Debug.Log("[CombatRegression] adaptive camera PASS: six viewport sizes, A/B/C footline/body bounds, HUD/world integer zoom, compact scroll and 44px touch");
        } finally { WorldConfig.GroundY = oldGround; UnityEngine.Object.DestroyImmediate(root); }
    }

    static void CheckGrowth() {
        Check(CombatMath.StatDamageMultiplier(0) == 1f && CombatMath.StatDamageMultiplier(100) == 1.5f, "STR/INT scaling");
        Check(Mathf.Abs(CombatMath.CritChance(100) - 0.1f) < 0.00001f && CombatMath.CritChance(495) == 0.3f, "LUK critical chance/cap");
        var rng = new System.Random(10); var baseline = new System.Random(10);
        Check(CombatMath.ApplyGrowthDamage(100, 1f, 0f, 1.5f, rng) == 100 && rng.Next() == baseline.Next(), "zero growth alters base damage/RNG");
        Check(CombatMath.ApplyGrowthDamage(100, 1.5f, 1f, 1.5f, rng) == 225, "growth plus critical damage");
        var go = new GameObject("GrowthRegression");
        try {
            var player = go.AddComponent<PlayerController>(); player.Init(false, "male", () => Array.Empty<MonsterController>());
            player.ApplyState(new InvState { level = 100, str = 100, intelligence = 40, dex = 30, luk = 325, combat = new CombatTuning(), equip = new InvEquip {weapon = 1}, inv = new[] {new InvItem {uid = 1, slot = "weapon", kind = "sword", tier = 0}} });
            Check(player.AttackMultiplier == 1.5f && player.Defense == 30 && player.CriticalChance == 0.3f && Mathf.Abs(player.Range - 26f / 40f) < 0.00001f, "sword authoritative stats/range");
            player.WeaponKind = "staff";
            Check(Mathf.Abs(player.AttackMultiplier - 1.2f) < 0.00001f && Mathf.Abs(player.Range - 72.8f / 40f) < 0.00001f, "staff INT/range");
        } finally { UnityEngine.Object.DestroyImmediate(go); }
        Debug.Log("[CombatRegression] growth PASS: STR/INT, DEX, LUK cap, critical damage, zero-growth RNG and authoritative weapon stats");
    }

    static void CheckTransport(PlayerController player) {
        var skill = GameData.Skills.First();
        player.RestoreTransport(new Snapshot { x = float.NaN, face = -1, hp = 1, dead = true,
            respawnRemaining = 2, potionCd = 4, attackCd = 0.4f, hitDelay = 1,
            regenAcc = 0.5f, skillTimers = new[] { new SkillTimer { key = skill.key, remaining = skill.cd + 999 } } });
        var snapshot = player.GetSnapshot();
        Check(!float.IsNaN(snapshot.x) && snapshot.x == WorldConfig.MapMargin && player.IsDead && snapshot.face == -1, "invalid position / death transport restore failed");
        Check(snapshot.respawnRemaining == 2 && snapshot.potionCd == 4 && snapshot.attackCd == 0.4f && snapshot.regenAcc == 0.5f, "transport lost combat timers");
        Check(player.SkillCooldown(skill.key) == skill.cd, "transport skill cooldown exceeded configured maximum");
        player.RestoreTransport(new Snapshot { x = 10, hp = player.MaxHp });
        Debug.Log("[CombatRegression] transport PASS: invalid x, death, respawn, potion / attack / skill cooldowns");
    }

    static void GrantSkills(PlayerController player, string[] skills) {
        player.ApplyState(new InvState { level = player.Level, skills = skills, equip = new InvEquip { weapon = 1 }, inv = new[] { new InvItem { uid = 1, slot = "weapon", kind = player.WeaponKind, tier = player.WeaponTierIdx } } });
    }

    public static void RunAnimation() {
        var root = new GameObject("CombatAnimationRegression");
        var previousLocal = PlayerController.Local;
        try {
            var spawner = root.AddComponent<MonsterSpawner>();
            var playerGo = new GameObject("RegressionPlayer");
            playerGo.transform.SetParent(root.transform);
            var player = playerGo.AddComponent<PlayerController>();
            player.WeaponKind = "staff"; player.Level = 40;
            player.Init(true, "male", () => spawner.Alive);
            GrantSkills(player, GameData.Skills.Where(skill => skill.weapon == "staff" && skill.lv <= player.Level).Select(skill => skill.key).ToArray());
            var animator = player.GetComponent<FrameAnimator>();
            int impacts = 0, casts = 0; bool impactDuringReaction = false;
            animator.OnImpact += () => { impacts++; impactDuringReaction |= animator.Reacting; };
            player.OnSkillCast += _ => casts++;
            const int seed = 456;
            Field(player, "_rng").SetValue(player, new System.Random(seed));
            var rng = new System.Random(seed);
            var def = GameData.Monsters[0]; def.hp = 1000000; def.def = 100;
            var target = (MonsterController)Call(spawner, "Spawn", def, 0.1f);
            int basic = CombatMath.Mitigate(CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng), def.def);
            int expected = basic;
            foreach (var skill in GameData.Skills.Where(s => s.weapon == "staff" && s.lv <= player.Level)) {
                int atk = CombatMath.PlayerFixedAtk(player.Level) + CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng);
                expected += CombatMath.Mitigate(Mathf.Max(1, Mathf.RoundToInt(atk * skill.mult)), def.def);
            }
            const float dt = 1f / 60f;
            // Real incoming damage overlays hurt frames without replacing the attack or its pending skills.
            player.Step(dt);
            string attackClip = animator.Clip;
            float attackCd = player.GetSnapshot().attackCd;
            int pendingCount = ((List<SkillDef>)Field(player, "_pendingSkills").GetValue(player)).Count;
            player.TakeDamage(1);
            Check(animator.Reacting && animator.Clip == attackClip && player.GetSnapshot().attackCd == attackCd, "hurt interrupted attack timeline / cooldown");
            Check(((List<SkillDef>)Field(player, "_pendingSkills").GetValue(player)).Count == pendingCount, "hurt lost pending skills");
            Check(player.GetComponent<SpriteRenderer>().sprite == Resources.LoadAll<Sprite>($"{ActorScale.PlayerRoot}/main_m/hurt").OrderBy(frame => frame.name).First(), "hurt frame was not displayed");
            // Player.Step이 자동 공격을 시작하고 Tick이 실제 impact 프레임 이벤트를 발생시킨다.
            for (int i = 0; i < 120 && impacts == 0; i++) { player.Step(dt); animator.Tick(dt); }
            Check(impacts == 1 && casts == 2 && target.Hp == def.hp - expected + basic, "애니메이션 impact 스킬 피해 / 투사체 지연 불일치");
            Call(player, "TickProjectiles", 0.2f);
            Check(target.Hp == def.hp - expected, "hurt 중 발사한 투사체 기본 공격 피해 불일치");
            Check(impactDuringReaction, "attack impact did not survive active hurt reaction");
            animator.Tick(0.24f); Check(!animator.Reacting, "hurt overlay did not return to normal animation");
            Field(target, "_hitCooldownTimer").SetValue(target, 0f);
            Check(target.TakeDamage(2000000) && target.IsDead, "애니메이션 검증 대상 정리 실패");
            UnityEngine.Object.DestroyImmediate(target.gameObject);

            def.hp = 1; def.def = 0;
            var mobs = Enumerable.Range(0, 3).Select(i => (MonsterController)Call(spawner, "Spawn", def, player.transform.position.x + 0.1f + i * 0.1f)).ToArray();
            int deaths = 0;
            spawner.OnMonsterDied += _ => deaths++;
            for (int i = 0; i < 180 && deaths < 3; i++) { player.Step(dt); animator.Tick(dt); }
            Check(impacts == 2 && mobs.All(m => m.IsDead) && deaths == 3 && spawner.Alive.Count == 0, "실제 impact 이벤트 광역 동시 처치 실패");
            Debug.Log("[CombatRegression] ANIMATION PASS: real incoming hurt overlay preserves attack impact, basic+2skills, cooldown, recovery and multi-kill");
        } finally {
            typeof(PlayerController).GetProperty("Local").SetValue(null, previousLocal);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
}
