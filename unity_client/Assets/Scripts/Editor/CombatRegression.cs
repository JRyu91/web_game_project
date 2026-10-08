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
        CheckAdaptiveCamera();
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
            int expected = CombatMath.Mitigate(CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng), def.def);
            foreach (var skill in skills) {
                int atk = CombatMath.PlayerFixedAtk(player.Level) + CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng);
                expected += CombatMath.Mitigate(Mathf.Max(1, Mathf.RoundToInt(atk * skill.mult)), def.def);
            }
            pending.AddRange(skills);
            Call(player, "StartAttack", target);
            Call(player, "Impact");
            Check(target.Hp == def.hp - expected, "기본 공격 + 두 스킬의 개별 방어력 적용 불일치");
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

    static void CheckAdaptiveCamera() {
        var root = new GameObject("AdaptiveCameraRegression");
        float oldGround = WorldConfig.GroundY;
        try {
            var cameraGo = new GameObject("Camera"); cameraGo.transform.SetParent(root.transform);
            var cam = cameraGo.AddComponent<Camera>(); cam.orthographic = true; cam.orthographicSize = 9;
            var pixel = cameraGo.AddComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();
            pixel.assetsPPU = 40; pixel.refResolutionX = 1280; pixel.refResolutionY = 720;
            var zone = root.AddComponent<ZoneController>(); zone.Cam = cam;
            foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(393, 720), new Vector2Int(852, 329) }) {
                var texture = new RenderTexture(size.x, size.y, 24);
                cam.targetTexture = texture; cam.aspect = (float)size.x / size.y;
                foreach (float ground in new[] { -6.475f, -5.925f, -5.875f }) {
                    WorldConfig.GroundY = ground; zone.Layout(10);
                    var feet = cam.WorldToViewportPoint(new Vector3(cam.transform.position.x, ground, 0));
                    float expected = 0.5f + ground / 18f;
                    Check(Mathf.Abs(feet.y - expected) < 0.003f && feet.y > 0.1f, $"adaptive footline clipped: {size}, y={feet.y}");
                    Check(ground + 3.7f < cam.transform.position.y + cam.orthographicSize, "148px player body clipped above viewport");
                }
                cam.targetTexture = null; UnityEngine.Object.DestroyImmediate(texture);
            }
            Debug.Log("[CombatRegression] adaptive camera PASS: PC / portrait / short landscape footline and body bounds across A/B/C");
        } finally { WorldConfig.GroundY = oldGround; UnityEngine.Object.DestroyImmediate(root); }
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
            int impacts = 0, casts = 0;
            animator.OnImpact += () => impacts++;
            player.OnSkillCast += _ => casts++;
            const int seed = 456;
            Field(player, "_rng").SetValue(player, new System.Random(seed));
            var rng = new System.Random(seed);
            var def = GameData.Monsters[0]; def.hp = 1000000; def.def = 100;
            var target = (MonsterController)Call(spawner, "Spawn", def, 0.1f);
            int expected = CombatMath.Mitigate(CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng), def.def);
            foreach (var skill in GameData.Skills.Where(s => s.weapon == "staff" && s.lv <= player.Level)) {
                int atk = CombatMath.PlayerFixedAtk(player.Level) + CombatMath.RollWeaponDamage(player.CurrentWeapon, 0, rng);
                expected += CombatMath.Mitigate(Mathf.Max(1, Mathf.RoundToInt(atk * skill.mult)), def.def);
            }
            const float dt = 1f / 60f;
            // Player.Step이 자동 공격을 시작하고 Tick이 실제 impact 프레임 이벤트를 발생시킨다.
            for (int i = 0; i < 120 && impacts == 0; i++) { player.Step(dt); animator.Tick(dt); }
            Check(impacts == 1 && casts == 2 && target.Hp == def.hp - expected, "애니메이션 impact 기본 공격 + 자동 스킬 피해 불일치");
            Field(target, "_hitCooldownTimer").SetValue(target, 0f);
            Check(target.TakeDamage(2000000) && target.IsDead, "애니메이션 검증 대상 정리 실패");
            UnityEngine.Object.DestroyImmediate(target.gameObject);

            def.hp = 1; def.def = 0;
            var mobs = Enumerable.Range(0, 3).Select(i => (MonsterController)Call(spawner, "Spawn", def, player.transform.position.x + 0.1f + i * 0.1f)).ToArray();
            int deaths = 0;
            spawner.OnMonsterDied += _ => deaths++;
            for (int i = 0; i < 180 && deaths < 3; i++) { player.Step(dt); animator.Tick(dt); }
            Check(impacts == 2 && mobs.All(m => m.IsDead) && deaths == 3 && spawner.Alive.Count == 0, "실제 impact 이벤트 광역 동시 처치 실패");
            Debug.Log("[CombatRegression] ANIMATION PASS: fixed-step editor Player.Step → FrameAnimator.OnImpact, basic+2skills, multi-kill");
        } finally {
            typeof(PlayerController).GetProperty("Local").SetValue(null, previousLocal);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
}
