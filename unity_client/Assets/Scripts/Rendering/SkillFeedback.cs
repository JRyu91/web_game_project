using System;
using System.Collections.Generic;
using System.Linq;
using Game.Data;
using Game.Gameplay;
using UnityEngine;

namespace Game.Rendering {
// Native clip anticipation follows the attack; the authoritative damage event starts its impact frame.
public class SkillFeedback : MonoBehaviour {
    [Serializable] public sealed class Clip { public string key, asset; public float fps = 16; public float originX, originY; public int impactFrame; }
    [Serializable] sealed class ClipFile { public Clip[] clips; }
    sealed class Effect {
        public GameObject go; public SpriteRenderer sprite; public Sprite[] frames;
        public SkillDef skill; public bool windup, detail;
        public float age, duration; public int first, last; public Vector3 position, start;
    }
    const int MaxEffects = 48;
    readonly List<Effect> _effects = new List<Effect>();
    readonly Dictionary<string, Clip> _clips = new Dictionary<string, Clip>();
    PlayerController _player; Func<IEnumerable<MonsterController>> _monsters;
    Zone _zone;
    public int ActiveCount => _effects.Count;
    public void Init(PlayerController player, Func<IEnumerable<MonsterController>> monsters) {
        _player = player; _monsters = monsters;
        _zone = ZoneController.Current != null ? ZoneController.Current.Zone : Zone.A;
        var file = Resources.Load<TextAsset>("Config/skill_fx");
        if (file != null) foreach (var clip in JsonUtility.FromJson<ClipFile>(file.text).clips) _clips[clip.key] = clip;
        _player.OnSkillWindup += Windup;
        _player.OnSkillImpact += Impact;
    }
    public static bool Supports(string key) => GameData.Skills.Any(s => s.key == key);
    static void Remove(Effect effect) { if (Application.isPlaying) Destroy(effect.go); else DestroyImmediate(effect.go); }
    public void Clear() { foreach (var effect in _effects) Remove(effect); _effects.Clear(); }
    void ClearWindup(string key) {
        for (int i = _effects.Count - 1; i >= 0; i--) if (_effects[i].windup && _effects[i].skill.key == key) { Remove(_effects[i]); _effects.RemoveAt(i); }
    }
    Vector3[] Targets(SkillDef skill) => (_monsters?.Invoke() ?? Enumerable.Empty<MonsterController>())
        .Where(m => _player.SkillHits(skill, m)).Select(m => {
            var visual = m.GetComponent<ActorVisual>();
            return new Vector3(visual != null ? visual.BodyX : m.transform.position.x, WorldConfig.GroundY, 0);
        }).ToArray();
    public void Cast(SkillDef skill) => Impact(skill, Targets(skill)); // Existing preview/capture callers.
    public void Windup(SkillDef skill, float seconds) {
        if (seconds > 0) Paint(skill, Targets(skill), true, seconds);
    }
    public void Impact(SkillDef skill, Vector3[] targets) {
        if (!isActiveAndEnabled || _player == null || _player.IsDead || !_player.GameplayReady) return;
        ClearWindup(skill.key);
        Paint(skill, targets, false, 0);
        if (skill.weapon == "sword") {
            var frames = HitFx.Frames("Sprites/FX/obj_hitspark/anim1");
            foreach (var target in targets) Add(skill, frames, 0, frames.Length - 1, frames.Length / 24f, false, true, target + Vector3.up * .65f, target + Vector3.up * .65f);
        }
    }
    void Paint(SkillDef skill, Vector3[] targets, bool windup, float seconds) {
        if (!isActiveAndEnabled || _player == null || _player.IsDead || !_player.GameplayReady || !_clips.TryGetValue(skill.key, out var clip)) return;
        var frames = HitFx.Frames("Sprites/FX/" + clip.asset + "/anim1");
        if (frames.Length == 0) return;
        int impact = Mathf.Clamp(clip.impactFrame, 0, frames.Length - 1);
        bool projectile = windup && (skill.key == "qi_staff" || skill.key == "rain_staff");
        bool meteor = windup && skill.key == "end_staff";
        if (meteor && _clips.TryGetValue("meteor_projectile", out var meteorClip)) { clip = meteorClip; frames = HitFx.Frames("Sprites/FX/" + clip.asset + "/anim1"); }
        if (windup && impact == 0 && !projectile && !meteor) return;
        int first = windup ? 0 : impact, last = windup && !meteor ? impact - 1 : frames.Length - 1;
        float duration = windup ? seconds : (last - first + 1) / Mathf.Max(1, clip.fps);
        int face = _player.Face;
        var ahead = new Vector3(_player.Visual.BodyX + face * 1.5f, WorldConfig.GroundY, 0);
        // Sword silhouettes define the sweep once; spells strike each actual target, including imminent kills.
        var positions = skill.weapon == "sword" ? new[] { skill.kind == "area" || skill.kind == "map" ? new Vector3(_player.Visual.BodyX, WorldConfig.GroundY, 0) : ahead }
            : targets.Length == 0 ? new[] { ahead } : targets;
        if (projectile) {
            var ordered = targets.Where(target => (target.x - _player.Visual.BodyX) * face >= 0).OrderBy(target => Mathf.Abs(target.x - _player.Visual.BodyX)).ToArray();
            positions = new[] { ordered.Length > 0 ? ordered[skill.key == "qi_staff" ? ordered.Length - 1 : 0] : ahead };
            frames = HitFx.Frames("Sprites/FX/" + (skill.key == "qi_staff" ? "obj_energybolt" : "obj_firebolt") + "/anim1");
            first = 0; last = frames.Length - 1;
        }
        foreach (var target in positions) {
            var frame = frames[first];
            var offset = projectile ? Vector3.up * .65f : new Vector3((frame.texture.width * .5f - clip.originX) * face, clip.originY - frame.texture.height * .5f, 0) / frame.pixelsPerUnit;
            float height = skill.key == "rain_sword" ? 1.3f : skill.key == "qi_sword" || skill.key == "qi_staff" || skill.key == "rain_staff" ? .8f : skill.key == "king_sword" ? .6f : 0;
            if (projectile || meteor) height = 0;
            var position = Snap(target + offset + Vector3.up * height);
            var start = projectile ? Snap(new Vector3(_player.Visual.BodyX + face * .3f, WorldConfig.GroundY + .8f, 0)) : meteor ? position + new Vector3(face * 3f, 5f, 0) : position;
            Add(skill, frames, first, last, duration, windup, false, start, position);
        }
    }
    void Add(SkillDef skill, Sprite[] frames, int first, int last, float duration, bool windup, bool detail, Vector3 start, Vector3 position) {
        if (frames.Length == 0) return;
        if (_effects.Count >= MaxEffects) {
            int oldest = _effects.FindIndex(e => e.windup || e.detail); if (oldest < 0) oldest = 0;
            Remove(_effects[oldest]); _effects.RemoveAt(oldest);
        }
        var go = new GameObject("Skill_" + skill.key + (windup ? "_windup" : detail ? "_hit" : "_impact"));
        go.transform.SetParent(transform, false); go.transform.position = start;
        var sprite = go.AddComponent<SpriteRenderer>(); sprite.sortingOrder = windup ? 8 : detail ? 11 : 10;
        sprite.sprite = frames[first]; sprite.flipX = _player.Face < 0;
        _effects.Add(new Effect { go = go, sprite = sprite, frames = frames, skill = skill, windup = windup, detail = detail, duration = duration, first = first, last = last, position = position, start = start });
    }

    static Vector3 Snap(Vector3 position) => new Vector3(Mathf.Round(position.x * 40) / 40, Mathf.Round(position.y * 40) / 40, 0);
    public void Tick(float dt) {
        if (_player == null || _player.IsDead || !_player.GameplayReady) { Clear(); return; }
        for (int i = _effects.Count - 1; i >= 0; i--) {
            var effect = _effects[i]; effect.age += Mathf.Max(0, dt);
            if (!effect.windup && effect.age >= effect.duration || effect.windup && !_player.PreparingSkill(effect.skill.key)) { Remove(effect); _effects.RemoveAt(i); continue; }
            float progress = Mathf.Clamp01(effect.age / effect.duration);
            effect.go.transform.position = Snap(Vector3.Lerp(effect.start, effect.position, progress)); // World anchors survive owner movement.
            effect.sprite.sprite = effect.frames[Mathf.Min(effect.last, effect.first + (int)(progress * (effect.last - effect.first + 1)))];
            if (!effect.windup) effect.sprite.color = new Color(1, 1, 1, Mathf.Clamp01((1 - progress) * 4));
        }
    }
    void Update() {
        var zone = ZoneController.Current != null ? ZoneController.Current.Zone : Zone.A;
        if (zone != _zone) { Clear(); _zone = zone; return; }
        Tick(Time.deltaTime);
    }
    void OnDisable() => Clear();
    void OnDestroy() {
        if (_player != null) { _player.OnSkillWindup -= Windup; _player.OnSkillImpact -= Impact; }
        Clear();
    }
}
}
