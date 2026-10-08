using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Game.Gameplay;
using Game.Rendering;

namespace Game.EditorTools {
// PM runs: -executeMethod Game.EditorTools.NightQA.Run. No art generation or network.
public static class NightQA {
    static void Check(bool ok, string message) { if (!ok) throw new Exception("[NightQA] " + message); }
    public static void Run() {
        var root = new GameObject("NightQA");
        try {
            var zone = root.AddComponent<ZoneController>();
            typeof(ZoneController).GetField("<Zone>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(zone, Zone.B);
            var spawner = root.AddComponent<MonsterSpawner>();
            var alive = (List<MonsterController>)typeof(MonsterSpawner).GetField("_alive", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(spawner);
            for (int i=0;i<spawner.MaxAlive;i++) {
                var mob = new GameObject("CapacityFixture"); mob.transform.SetParent(root.transform);
                alive.Add(mob.AddComponent<MonsterController>());
            }
            Check(!spawner.CanSummon(19), "full field permits a summoned boss above MaxAlive");
            Check(!spawner.SpawnSummoned(19), "full field accepts spawn and consumes summon");
            int before=alive.Count;
            spawner.TickBoss(100000f);
            Check(alive.Count==before && spawner.Boss==null, "natural boss exceeds total monster cap");
            Check(!spawner.CanSummon(999), "unknown boss tier accepted");
            Debug.Log("[NightQA] PASS: full-field summoned/natural boss cap and unknown tier rejection");
        } finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
}
