using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Game.Data;
using Game.Gameplay;
using Game.Network;
using Game.Rendering;

namespace Game.EditorTools {
// Fixed progression, actual Unity combat/animations at 60Hz; economy totals exclude drops, sales and bosses.
public static class BalancePlaytest {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run() {
        CombatMath.EnsureBalance();
        var rows = new StringBuilder("profile,level,zone,weapon,move_px,duration_s,kills,deaths,potions,damage_taken,base_gold,base_xp,skill_casts,max_actors,max_projectiles\n");
        float oldSpeed = PlayerController.MovePx, oldGround = WorldConfig.GroundY;
        try {
            foreach (int level in new[] { 20, 35, 70, 100 })
                foreach (string weapon in new[] { "sword", "staff" })
                    rows.Append(Trial(level, weapon, oldSpeed));
            foreach (string weapon in new[] { "sword", "staff" }) rows.Append(Trial(35, weapon, 75f));
            foreach (int level in new[] { 20, 35, 40, 60, 70, 80, 100 })
                foreach (string weapon in new[] { "sword", "staff" }) rows.Append(Trial(level, weapon, oldSpeed, false));
            foreach (int level in new[] { 35, 70 })
                foreach (string weapon in new[] { "sword", "staff" }) rows.Append(Trial(level, weapon, oldSpeed, false, true));
            File.WriteAllText("Logs/balance-playtest.csv", rows.ToString());
            Debug.Log("[BalancePlaytest] PASS: twenty-eight 30-minute actual-controller simulations, fixed progression, seeds and economy exclusions\n" + rows);
        } finally { PlayerController.MovePx = oldSpeed; WorldConfig.GroundY = oldGround; }
    }
    static string Trial(int level, string weapon, float speed, bool allocated = true, bool undergeared = false) {
        var root = new GameObject("BalancePlaytest");
        try {
            PlayerController.MovePx = speed;
            var zone = root.AddComponent<ZoneController>();
            var map = level < 35 ? Zone.A : level < 70 ? Zone.B : Zone.C;
            zone.SetZone(map, Tod.Day);
            var spawner = root.AddComponent<MonsterSpawner>(); spawner.SetPopulation(1);
            typeof(MonsterSpawner).GetField("_rng", Private).SetValue(spawner, new System.Random(173));
            var playerGo = new GameObject("Player"); playerGo.transform.SetParent(root.transform);
            playerGo.transform.position = new Vector3(40, WorldConfig.GroundY, 0);
            var player = playerGo.AddComponent<PlayerController>(); player.Level = level;
            player.Init(true, "male", () => spawner.Alive);
            var weapons = weapon == "sword" ? GameData.Swords : GameData.Staves;
            int wi = Array.FindLastIndex(weapons, w => w.level <= level - (undergeared ? 15 : 0));
            string cls = weapon == "sword" ? "warrior" : "mage";
            int hi = Array.FindLastIndex(GameData.Helmets, g => g.level <= level - (undergeared ? 20 : 0) && (g.cls == cls || g.cls == "common"));
            int ai = Array.FindLastIndex(GameData.Armors, g => g.level <= level - (undergeared ? 20 : 0) && (g.cls == cls || g.cls == "common"));
            int points = allocated ? 5 * (level - 1) : 0, main = points * 7 / 10;
            var state = new InvState { level = level, potions = 9999, dex = points - main,
                str = weapon == "sword" ? main : 0, intelligence = weapon == "staff" ? main : 0,
                skills = GameData.Skills.Where(s => s.weapon == weapon && (allocated ? s.lv <= level : s.lv < level)).Select(s => s.key).ToArray(),
                equip = new InvEquip { weapon = 1, helmet = hi >= 0 ? 2 : 0, armor = ai >= 0 ? 3 : 0 },
                inv = new[] { new InvItem { uid = 1, slot = "weapon", kind = weapon, tier = wi },
                    new InvItem { uid = 2, slot = "helmet", tier = hi }, new InvItem { uid = 3, slot = "armor", tier = ai } } };
            player.ApplyState(state);
            typeof(PlayerController).GetField("_rng", Private).SetValue(player, new System.Random(211));
            int kills = 0, deaths = 0, casts = 0, maxActors = 0, maxBolts = 0, loss = 0;
            long gold = 0, xp = 0; int previousHp = player.Hp;
            player.OnHpChanged += (hp, max) => { if (hp < previousHp) loss += previousHp - Math.Max(0, hp); previousHp = hp; };
            player.OnSkillCast += _ => casts++;
            var actors = new List<MonsterController>();
            spawner.OnMonsterSpawned += m => { actors.Add(m); typeof(MonsterController).GetField("_rng", Private).SetValue(m, new System.Random(331 + m.Def.tier)); };
            spawner.OnMonsterDied += m => { kills++; gold += m.Def.gold; xp += m.Def.exp; player.ConfirmKillReward(); };
            var spawn = typeof(MonsterSpawner).GetMethod("SpawnOne", Private);
            var bolts = typeof(PlayerController).GetField("_projectiles", Private);
            var pa = player.GetComponent<FrameAnimator>(); var pv = player.GetComponent<ActorVisual>(); var gear = player.GetComponent<GearAttachment>();
            var playerState = typeof(PlayerController).GetField("_state", Private);
            const float dt = 1f / 60; float nextSpawn = 0; bool wasDead = false;
            for (int tick = 0; tick < 1800 * 60; tick++) {
                nextSpawn -= dt;
                if (nextSpawn <= 0 && spawner.Alive.Count < spawner.MaxAlive) { spawn.Invoke(spawner, null); nextSpawn = spawner.SpawnInterval; }
                float beforeX = player.transform.position.x;
                player.Step(dt);
                if ((PlayerState)playerState.GetValue(player) == PlayerState.Move) {
                    float expectedX = Mathf.Clamp(beforeX + player.Face * speed * dt / 40, WorldConfig.MapMargin, WorldConfig.MapWidth - WorldConfig.MapMargin);
                    if (Mathf.Abs(player.transform.position.x - expectedX) > 0.0001f) throw new Exception("Configured movement speed mismatch");
                }
                foreach (var m in actors.ToArray()) if (m != null) m.Step(dt);
                pa.Tick(dt);
                foreach (var m in actors.ToArray()) if (m != null) { m.GetComponent<FrameAnimator>().Tick(dt); m.GetComponent<ActorVisual>().Refresh(); }
                pv.Refresh(); gear.Apply(); actors.RemoveAll(m => m == null);
                if (player.IsDead && !wasDead) deaths++; wasDead = player.IsDead;
                maxActors = Math.Max(maxActors, actors.Count);
                maxBolts = Math.Max(maxBolts, ((System.Collections.ICollection)bolts.GetValue(player)).Count);
                if (float.IsNaN(player.transform.position.x) || spawner.Alive.Count > 5 || actors.Count > 20 || maxBolts > 16)
                    throw new Exception("Simulation bounds/lifecycle failure");
            }
            if (state.skills.Length > 0 && casts == 0) throw new Exception("Simulation did not exercise owned skills");
            string row = $"{(undergeared ? "entry_undergeared" : allocated ? "allocated_all_books" : "unallocated_prior_books")},{level},{map},{weapon},{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)},1800,{kills},{deaths},{9999 - player.PotionCount},{loss},{gold},{xp},{casts},{maxActors},{maxBolts}\n";
            Debug.Log("[BalancePlaytest] " + row.Trim()); return row;
        } finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
}
