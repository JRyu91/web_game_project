using System;
using System.Collections.Generic;
using UnityEngine;
using Game.Rendering;

namespace Game.Gameplay {
// 정본 staff_bolt_tier: Lv50부터 fire, 지팡이 끝에서 73px / 400px/s.
public sealed class StaffProjectile : IDisposable {
    public const float Range = 73f / 40f, Speed = 400f / 40f;
    public readonly GameObject Visual;
    public readonly string Effect;
    readonly HashSet<MonsterController> _hit = new HashSet<MonsterController>();
    readonly int _face, _damage;
    readonly float _ownerX, _hitStop;
    float _remaining = Range, _previousX;
    public bool Finished => _remaining <= 0f;

    public StaffProjectile(Vector3 muzzle, float ownerX, int face, int tier, int damage, float hitStop) {
        _face = face < 0 ? -1 : 1; _damage = damage; _ownerX = ownerX; _hitStop = hitStop;
        _previousX = ownerX; // 몸에 닿은 근접 대상도 첫 비행 구간에 포함한다.
        Effect = tier >= 10 ? "obj_firebolt" : "obj_energybolt";
        Visual = new GameObject("StaffProjectile-" + Effect);
        Visual.transform.position = muzzle;
        var sr = Visual.AddComponent<SpriteRenderer>(); sr.sortingOrder = 9; sr.flipX = _face < 0;
        var anim = Visual.AddComponent<FrameAnimator>(); anim.Renderer = sr; anim.Fps = 1f / 0.06f;
        anim.SetSource("Sprites/FX/" + Effect); anim.Play("anim1");
    }
    public void Step(float dt, IEnumerable<MonsterController> monsters) {
        if (Finished || Visual == null || dt <= 0f) return;
        float distance = Mathf.Min(_remaining, Speed * dt);
        var p = Visual.transform.position; p.x += _face * distance; Visual.transform.position = p;
        float lo = Mathf.Min(_previousX, p.x), hi = Mathf.Max(_previousX, p.x);
        // 기존 전투는 발라인의 수평 거리 모델이다. sweep으로 저FPS에서도 관통 누락을 막는다.
        foreach (var monster in monsters) {
            if (Finished || Visual == null) break;
            if (monster == null || monster.IsDead || _hit.Contains(monster)) continue;
            var body = monster.GetComponent<ActorVisual>();
            float x = body != null ? body.BodyX : monster.transform.position.x;
            float radius = body != null ? body.HalfWidth : 0f;
            if ((x - _ownerX) * _face < 0f || x + radius < lo || x - radius > hi) continue;
            _hit.Add(monster);
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
