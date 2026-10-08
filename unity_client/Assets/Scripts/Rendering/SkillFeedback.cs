using System;
using System.Collections.Generic;
using System.Linq;
using Game.Data;
using Game.Gameplay;
using UnityEngine;

namespace Game.Rendering {
// Skill damage remains in PlayerController; this component only paints the impact event.
// ponytail: procedural skill paths stand in for missing bespoke art; replace when matching skill frame assets are supplied.
public class SkillFeedback : MonoBehaviour {
    sealed class Effect {
        public GameObject go; public SpriteRenderer sprite; public Sprite[] frames;
        public float age, duration; public Vector3 start, end;
    }
    readonly List<Effect> _effects = new List<Effect>();
    readonly Dictionary<string, Sprite[]> _frames = new Dictionary<string, Sprite[]>();
    PlayerController _player; Func<IEnumerable<MonsterController>> _monsters;
    Material _lineMaterial;
    Zone _zone;
    public int ActiveCount => _effects.Count;
    public void Init(PlayerController player, Func<IEnumerable<MonsterController>> monsters) {
        _player = player; _monsters = monsters;
        _zone = ZoneController.Current != null ? ZoneController.Current.Zone : Zone.A;
        _lineMaterial = new Material(Shader.Find("Sprites/Default"));
        _player.OnSkillCast += Cast;
    }
    public static bool Supports(string key) => GameData.Skills.Any(s => s.key == key);
    public void Clear() {
        foreach (var e in _effects) { if (Application.isPlaying) Destroy(e.go); else DestroyImmediate(e.go); }
        _effects.Clear();
    }
    Effect Create(string name, float duration, Vector3 start, Vector3 end) {
        if (_effects.Count >= 32) { var old = _effects[0]; if (Application.isPlaying) Destroy(old.go); else DestroyImmediate(old.go); _effects.RemoveAt(0); }
        var go = new GameObject(name); go.transform.SetParent(transform, false); go.transform.position = start;
        var effect = new Effect { go = go, duration = duration, start = start, end = end }; _effects.Add(effect); return effect;
    }
    void SpriteEffect(string asset, Vector3 start, Vector3 end, float duration, int face, float delay = 0) {
        if (!_frames.TryGetValue(asset, out var frames)) {
            frames = Resources.LoadAll<Sprite>("Sprites/FX/" + asset + "/anim1").OrderBy(s => s.name).ToArray();
            _frames[asset] = frames;
        }
        if (frames.Length == 0) return;
        var e = Create(asset, duration, start, end); e.frames = frames;
        e.sprite = e.go.AddComponent<SpriteRenderer>(); e.sprite.sortingOrder = 9; e.sprite.sprite = frames[0]; e.sprite.flipX = face < 0; e.age = -delay; e.sprite.enabled = delay == 0;
    }
    void Path(string name, Vector3[] points, Color color, float duration = 0.45f) {
        var e = Create(name, duration, Vector3.zero, Vector3.zero);
        var line = e.go.AddComponent<LineRenderer>(); line.sharedMaterial = _lineMaterial;
        line.useWorldSpace = true; line.positionCount = points.Length; line.SetPositions(points);
        line.startWidth = line.endWidth = 2f / 40f; line.startColor = line.endColor = color; line.sortingOrder = 9;
    }
    void Arc(Vector3 center, float radius, float from, float to, Color color) {
        var points = new Vector3[17];
        for (int i = 0; i < points.Length; i++) { float angle = Mathf.Lerp(from, to, i / 16f) * Mathf.Deg2Rad; points[i] = Snap(center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius); }
        Path("SkillArc", points, color);
    }
    static Vector3 Snap(Vector3 p) => new Vector3(Mathf.Round(p.x * 40) / 40, Mathf.Round(p.y * 40) / 40, 0);
    public void Cast(SkillDef skill) {
        if (_player == null || !Supports(skill.key)) return;
        float x = _player.transform.position.x; int face = _player.Face;
        var center = new Vector3(x, WorldConfig.GroundY + 0.8f, 0);
        var targets = (_monsters?.Invoke() ?? Enumerable.Empty<MonsterController>()).Where(m => m != null && !m.IsDead)
            .Where(m => skill.kind == "map" || skill.kind == "facing" && (m.transform.position.x - x) * face >= 0 || skill.kind == "area" && ActorVisual.Gap(m.transform, _player.transform) <= 3.2f || skill.kind == "line" && (m.transform.position.x - x) * face >= 0 && ActorVisual.Gap(m.transform, _player.transform) <= _player.Range * 3)
            .OrderBy(m => Mathf.Abs(m.transform.position.x - x)).Take(12).Select(m => new Vector3(m.transform.position.x, WorldConfig.GroundY + 0.6f, 0)).ToArray();
        var ahead = center + Vector3.right * face * 2;
        var cyan = new Color(0.45f, 0.9f, 1); var gold = new Color(1, 0.85f, 0.3f);
        switch (skill.key) {
            case "qi_staff": SpriteEffect("obj_energybolt", center, targets.Length > 0 ? targets[0] : ahead, 0.35f, face); break;
            case "rain_staff": SpriteEffect("obj_firebolt", center, targets.Length > 0 ? targets[0] : ahead, 0.45f, face); break;
            case "qi_sword": case "rain_sword": Arc(center, skill.key == "qi_sword" ? 1.3f : 2.3f, face > 0 ? -80 : 100, face > 0 ? 80 : 260, cyan); break;
            case "volc_staff":
                var previous = center;
                foreach (var target in targets) { Path("ChainLightning", new[] { Snap(previous), Snap((previous + target) / 2 + Vector3.up * 0.45f), Snap(target) }, cyan); previous = target; }
                if (targets.Length == 0) Path("ChainLightning", new[] { center, ahead + Vector3.up }, cyan);
                break;
            case "volc_sword": case "end_sword":
                float width = skill.key == "end_sword" ? 7 : 3;
                var crack = new Vector3[17];
                for (int i = 0; i < crack.Length; i++) crack[i] = Snap(new Vector3(x - width + width * 2 * i / 16f, WorldConfig.GroundY + (i % 2) * 0.15f, 0));
                Path("GroundCrack", crack, gold, 0.65f);
                if (skill.key == "end_sword") Arc(center, 3, 0, 360, gold);
                break;
            case "king_sword": Arc(center, 1.5f, 0, 360, gold); Arc(center, 2.2f, 0, 360, gold); break;
            case "king_staff":
                foreach (var target in targets.Length > 0 ? targets : new[] { ahead }) {
                    Arc(target, 0.6f, 0, 360, cyan);
                    for (int i = 0; i < 5; i++) {
                        var flake = Snap(target + new Vector3((i - 2) * 0.35f, 0.8f + (i % 2) * 0.45f));
                        Path("FrostFlake", new[] { flake - Vector3.up * 0.12f, flake + Vector3.up * 0.12f }, cyan);
                        Path("FrostFlake", new[] { flake - Vector3.right * 0.12f, flake + Vector3.right * 0.12f }, cyan);
                    }
                }
                break;
            case "end_staff":
                foreach (var target in targets.Length > 0 ? targets : new[] { ahead }) { SpriteEffect("obj_bomb", target + new Vector3(-1, 3), target, 0.4f, face); SpriteEffect("obj_nuke", target, target, 0.8f, face, 0.4f); }
                break;
        }
    }
    public void Tick(float dt) {
        for (int i = _effects.Count - 1; i >= 0; i--) {
            var e = _effects[i]; e.age += dt;
            if (e.age >= e.duration) { if (Application.isPlaying) Destroy(e.go); else DestroyImmediate(e.go); _effects.RemoveAt(i); continue; }
            if (e.sprite == null) continue;
            if (e.age < 0) continue;
            e.sprite.enabled = true;
            float progress = e.age / e.duration;
            e.go.transform.position = Snap(Vector3.Lerp(e.start, e.end, progress));
            e.sprite.sprite = e.frames[Mathf.Min(e.frames.Length - 1, (int)(progress * e.frames.Length))];
        }
    }
    void Update() {
        var zone = ZoneController.Current != null ? ZoneController.Current.Zone : Zone.A;
        if (_player == null || _player.IsDead || !_player.GameplayReady || zone != _zone) { Clear(); _zone = zone; return; }
        Tick(Time.deltaTime);
    }
    void OnDestroy() { if (_player != null) _player.OnSkillCast -= Cast; Clear(); if (_lineMaterial != null) { if (Application.isPlaying) Destroy(_lineMaterial); else DestroyImmediate(_lineMaterial); } }
}
}
