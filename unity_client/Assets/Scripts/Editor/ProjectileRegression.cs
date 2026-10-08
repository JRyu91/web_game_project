using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor.SceneManagement;
using Game.Data;
using Game.Gameplay;
using Game.Rendering;

namespace Game.EditorTools {
public static class ProjectileRegression {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string message) { if (!ok) throw new Exception("[ProjectileRegression] " + message); }
    static object Call(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, Private).Invoke(obj, args);
    static MonsterController Spawn(MonsterSpawner spawner, float x) {
        var def = GameData.Monsters[0]; def.hp = 100000; def.def = 0;
        return (MonsterController)Call(spawner, "Spawn", def, x);
    }
    public static void Run() {
        var root = new GameObject("ProjectileRegression");
        StaffProjectile bolt = null;
        try {
            var spawner = root.AddComponent<MonsterSpawner>();
            var first = Spawn(spawner, 1.2f); var last = Spawn(spawner, 2.7f); var behind = Spawn(spawner, -1f); var outside = Spawn(spawner, 4f);
            var pool = new[] { first, last, behind, outside };
            int hp = first.Hp, damage = 100;
            // 스킬의 피격 CD가 남아 있어도 독립 투사체의 명중은 사라지지 않는다.
            first.TakeDamage(damage);
            bolt = new StaffProjectile(new Vector3(1, 0, 0), 0, 1, 9, damage, 0.08f);
            Check(bolt.Effect == "obj_energybolt" && bolt.Visual.GetComponent<SpriteRenderer>().sprite != null, "energy asset missing / tier9 mismatch");
            Check(last.Hp == hp, "damage before flight");
            bolt.Step(0.01f, pool);
            Check(first.Hp == hp - 2 * damage && last.Hp == hp, "projectile hit lost to skill cooldown / distant instant damage");
            bolt.Step(0.5f, pool);
            Check(bolt.Finished && Math.Abs(bolt.Visual.transform.position.x - (1 + StaffProjectile.Range)) < 0.0001f, "range clamp / final sweep wrong");
            Check(first.Hp == hp - 2 * damage && last.Hp == hp - damage && behind.Hp == hp && outside.Hp == hp, "piercing once / behind / range violation");
            bolt.Dispose();
            var left = Spawn(spawner, -2.7f);
            bolt = new StaffProjectile(new Vector3(-1, 0, 0), 0, -1, 10, damage, 0.08f);
            Check(bolt.Effect == "obj_firebolt" && bolt.Visual.GetComponent<SpriteRenderer>().flipX && bolt.Visual.GetComponent<SpriteRenderer>().sprite != null, "fire tier10 / left direction mismatch");
            bolt.Step(1f, new[] { left }); Check(left.Hp == hp - damage, "left swept hit failed"); bolt.Dispose();
            var moving = Spawn(spawner, 2.7f);
            bolt = new StaffProjectile(new Vector3(1, 0, 0), 0, 1, 0, damage, 0.08f);
            moving.transform.position = new Vector3(5, 0, 0); bolt.Step(1f, new[] { moving });
            Check(moving.Hp == hp, "hit used launch-time target position"); bolt.Dispose(); bolt = null;
            var player = new GameObject("Owner").AddComponent<PlayerController>(); player.transform.SetParent(root.transform);
            player.WeaponKind = "staff"; player.Init(false, "male", () => pool);
            Call(player, "StartAttack", first); Call(player, "Impact");
            var flights = (List<StaffProjectile>)typeof(PlayerController).GetField("_projectiles", Private).GetValue(player);
            Check(flights.Count == 1, "staff impact failed to launch");
            var muzzle = player.GetComponent<GearAttachment>().Muzzle;
            Check(Vector3.Distance(flights[0].Visual.transform.position, muzzle) < 0.0001f, "launch was not weapon metadata muzzle");
            player.GameplayReady = false; player.Step(0.01f); Check(flights.Count == 0, "disconnect / pause left damaging flight");
            player.GameplayReady = true; Call(player, "StartAttack", first); Call(player, "Impact"); Call(player, "ClearProjectilesOnZone", Zone.B);
            Check(flights.Count == 0, "map transition left flight");
            Call(player, "StartAttack", first); Call(player, "Impact"); Call(player, "Die"); Check(flights.Count == 0, "death left flight");
            Debug.Log("[ProjectileRegression] PASS travel, swept piercing once, skill cooldown, moving/behind/outside, tier9/10, muzzle, pause/map/death");
        } finally {
            bolt?.Dispose();
            foreach (var m in root.GetComponent<MonsterSpawner>().Alive.ToArray()) if (m != null) UnityEngine.Object.DestroyImmediate(m.gameObject);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
    public static void Capture() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cam = new GameObject("ProjectileCamera").AddComponent<Camera>(); cam.orthographic = true;
        var zone = new GameObject("ProjectileZone").AddComponent<ZoneController>(); zone.Cam = cam;
        ZoneController.AnimTime = 0; zone.SetZone(Zone.A, Tod.Day);
        var rt = new RenderTexture(1280, 720, 24); var tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
        cam.targetTexture = rt; cam.aspect = 1280f / 720f; cam.transform.position = new Vector3(36, cam.transform.position.y, -10);
        try {
            foreach (int tier in new[] { 9, 10 }) foreach (int face in new[] { 1, -1 }) {
                var p = new GameObject("StaffCapture").AddComponent<PlayerController>(); p.WeaponKind = "staff"; p.WeaponTierIdx = tier;
                p.transform.position = new Vector3(36, WorldConfig.GroundY, 0); p.Init(false, "male", () => Array.Empty<MonsterController>());
                var gear = p.GetComponent<GearAttachment>(); var anim = p.GetComponent<FrameAnimator>();
                anim.Play("attack2", false); anim.Tick(0.19f); gear.SetFace(face); gear.Apply(); p.GetComponent<ActorVisual>().Refresh(); gear.Apply();
                using (var shot = new StaffProjectile(gear.Muzzle, 36, face, tier, 100, 0.08f)) {
                    shot.Step(0.07f, Array.Empty<MonsterController>()); cam.Render(); cam.Render();
                    RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply(); RenderTexture.active = null;
                    File.WriteAllBytes($"Logs/projectile-{shot.Effect}-{(face > 0 ? "right" : "left")}.png", tex.EncodeToPNG());
                }
                UnityEngine.Object.DestroyImmediate(p.gameObject);
            }
            Debug.Log("[ProjectileRegression] four tier / direction captures complete; visual review required");
        } finally { cam.targetTexture = null; rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex); ZoneController.AnimTime = -1; }
    }
}
}
