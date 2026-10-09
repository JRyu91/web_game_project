using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Game.Rendering;

namespace Game.Gameplay {
// 정본 staff_bolt_tier: Lv50부터 fire, 지팡이 끝에서 219px / 400px/s (261008 사거리 ×3).
public sealed class StaffProjectile : IDisposable {
    public const float Range = 219f / 40f, Speed = 400f / 40f;
    public readonly GameObject Visual;
    public readonly string Effect;
    public bool Critical; // 발사 시점 치명타 → 명중 시 몬스터 치명타 연출
    readonly HashSet<MonsterController> _hit = new HashSet<MonsterController>();
    readonly int _face, _damage;
    readonly float _ownerX, _hitStop;
    float _remaining = Range, _previousX, _elapsed;
    readonly Vector3 _start;
    bool _aimed;
    float _aimY, _aimDistance;
    readonly SpriteRenderer _renderer;
    readonly Sprite[] _frames;
    static readonly Dictionary<string, Sprite[]> Frames = new Dictionary<string, Sprite[]>();
    // Core anchors in the original canvases: energy 48px (36,24), fire 72px (54,36); tails remain behind the travel point.
    public Vector2 CoreCanvas => Effect == "obj_firebolt" ? new Vector2(54, 36) : new Vector2(36, 24);
    static Sprite[] CoreFrames(string effect, Vector2 core) {
        if (Frames.TryGetValue(effect, out var frames)) return frames;
        frames = Resources.LoadAll<Sprite>("Sprites/FX/" + effect + "/anim1").OrderBy(sprite => sprite.name)
            .Select(sprite => Sprite.Create(sprite.texture, sprite.rect, new Vector2((core.x - sprite.rect.x) / sprite.rect.width, (core.y - sprite.rect.y) / sprite.rect.height), sprite.pixelsPerUnit)).ToArray();
        Frames[effect] = frames; return frames;
    }
    public bool Finished => _remaining <= 0f;

    public StaffProjectile(Vector3 muzzle, float ownerX, int face, int tier, int damage, float hitStop) {
        _face = face < 0 ? -1 : 1; _damage = damage; _ownerX = ownerX; _hitStop = hitStop;
        _start = muzzle; _aimY = muzzle.y;
        _previousX = ownerX; // 몸에 닿은 근접 대상도 첫 비행 구간에 포함한다.
        Effect = tier >= 10 ? "obj_firebolt" : "obj_energybolt";
        Visual = new GameObject("StaffProjectile-" + Effect);
        Visual.transform.position = muzzle;
        _renderer = Visual.AddComponent<SpriteRenderer>(); _renderer.sortingOrder = 9; _renderer.flipX = _face < 0;
        _frames = CoreFrames(Effect, CoreCanvas);
        if (_frames.Length > 0) _renderer.sprite = _frames[0];
    }
    public void Step(float dt, IEnumerable<MonsterController> monsters) {
        if (Finished || Visual == null || dt <= 0f) return;
        var pool = monsters?.ToArray() ?? Array.Empty<MonsterController>();
        if (!_aimed) {
            _aimed = true;
            float low = Mathf.Min(_ownerX, _start.x + _face * Range), high = Mathf.Max(_ownerX, _start.x + _face * Range);
            float nearest = Range; bool found = false;
            foreach (var monster in pool) {
                if (monster == null || monster.IsDead) continue;
                var body = monster.GetComponent<ActorVisual>();
                if (body == null || body.Body == null || body.Body.sprite == null) continue;
                float x = body.BodyX, radius = body.HalfWidth;
                if ((x - _ownerX) * _face < 0 || x + radius < low || x - radius > high) continue;
                float centerY = body.Body.transform.TransformPoint(new Vector3(0, SpriteBBox.Get(body.Body.sprite).center.y, 0)).y;
                _aimY = found ? Mathf.Min(_aimY, centerY) : centerY; found = true;
                nearest = Mathf.Min(nearest, (x - _face * radius - _start.x) * _face);
            }
            _aimDistance = Mathf.Clamp(nearest, 0, Range);
        }
        _elapsed += dt;
        if (_frames.Length > 0) _renderer.sprite = _frames[(int)(_elapsed / 0.06f) % _frames.Length];
        float distance = Mathf.Min(_remaining, Speed * dt);
        var p = Visual.transform.position; p.x += _face * distance;
        // Visual aiming only; existing horizontal travel, sweep and damage stay unchanged.
        p.y = Mathf.Lerp(_start.y, _aimY, _aimDistance > 0 ? Mathf.Clamp01((Range - _remaining + distance) / _aimDistance) : 1);
        Visual.transform.position = p;
        float lo = Mathf.Min(_previousX, p.x), hi = Mathf.Max(_previousX, p.x);
        // 기존 전투는 발라인의 수평 거리 모델이다. sweep으로 저FPS에서도 관통 누락을 막는다.
        foreach (var monster in pool) {
            if (Finished || Visual == null) break;
            if (monster == null || monster.IsDead || _hit.Contains(monster)) continue;
            var body = monster.GetComponent<ActorVisual>();
            float x = body != null ? body.BodyX : monster.transform.position.x;
            float radius = body != null ? body.HalfWidth : 0f;
            if ((x - _ownerX) * _face < 0f || x + radius < lo || x - radius > hi) continue;
            _hit.Add(monster);
            if (Critical) monster.MarkCritical();
            if (monster.TakeProjectileDamage(_damage)) monster.HitStop(_hitStop, _face);
        }
        _previousX = p.x; _remaining -= distance;
    }
    public void Dispose() {
        _remaining = 0f;
        if (Visual == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(Visual); else UnityEngine.Object.DestroyImmediate(Visual);
    }
}
}
