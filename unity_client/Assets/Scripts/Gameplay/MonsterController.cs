using System;
using System.Collections.Generic;
using UnityEngine;
using Game.Data;
using Game.Rendering;

namespace Game.Gameplay {

public enum MobState { Walk, Attack, Hurt, Dead }

public class MonsterController : MonoBehaviour {
    public int RegistrationId;
    public MonsterDef Def;
    public int Hp;
    public int MaxHp;
    public float PatrolMinX, PatrolMaxX;
    public event Action<MonsterController> OnDied;

    FrameAnimator _anim;
    ActorVisual _vis;
    SpriteRenderer _sr;
    MobState _state = MobState.Walk;
    int _face = 1;
    float _attackCd;
    float _hitCooldownTimer;
    System.Random _rng = new System.Random();

    // PlayerController 와 동일한 px->unit 환산 (1 unit = 40 game px) — 안 맞추면 몬스터가 화면을 순간이동하듯 가로지른다.
    const float PX_TO_UNIT = 1f / 40f;
    const float ATTACK_RANGE = 46f * PX_TO_UNIT;
    const float CHASE_RANGE = 260f * PX_TO_UNIT;
    // "아주 살짝만 느리게" 요청 — 원래 속도의 85%.
    const float SPEED_SCALE = 0.85f;
    const float CHASE_SPEED = 70f * PX_TO_UNIT * SPEED_SCALE;
    const float PATROL_SPEED = 30f * PX_TO_UNIT * SPEED_SCALE;
    const float ATTACK_INTERVAL = 1.2f;
    const float HIT_COOLDOWN = 0.15f; // 같은 순간 중복 타격 방지용(기획 결정 A, 공격 간격 0.3s까지 헛방 없음)

    public bool IsDead => _state == MobState.Dead;

    public void Init(MonsterDef def, Vector3 pos, float patrolMin, float patrolMax) {
        CombatMath.EnsureBalance();
        Def = def;
        MaxHp = def.hp; Hp = def.hp;
        PatrolMinX = patrolMin; PatrolMaxX = patrolMax;
        transform.position = pos;

        _sr = gameObject.AddComponent<SpriteRenderer>();
        _sr.sortingOrder = 5;
        _anim = gameObject.AddComponent<FrameAnimator>();
        _anim.Renderer = _sr;
        _anim.Fps = 8f;
        _anim.SetSource(ActorScale.MonsterRoot(def.tier));
        _anim.Play("walk");
        // 크기 = actor_scale.json 정수 배율(기본 1x 원본 px). 콜라이더/반폭/발 정렬은 ActorVisual(불투명 bbox).
        _vis = gameObject.AddComponent<ActorVisual>();
        _vis.Init(_sr, ActorScale.Tier(def.tier), true);
        _anim.OnImpact += Impact;
        // FINAL_c: 보스 오라 = 보스 뒤(order-1), Bottom Center, BodyX 기준. 정수 1x(몸 배율 상쇄). 키는 에셋 MANIFEST 확정 전 가정값.
        if (def.rank == "boss" || def.rank == "midboss" || def.rank == "hidden") {
            var s = Resources.Load<Sprite>(AuraKey);
            if (s != null) {
                _aura = new GameObject("BossAura").AddComponent<SpriteRenderer>();
                _aura.transform.SetParent(transform, false);
                _aura.sprite = s; _aura.sortingOrder = _sr.sortingOrder - 1;
            }
        }
    }
    // audit_c1 §2: t7·t8·t9·t13·t14·t17 은 walk0 이 정면 → 대기 프레임을 walk 중간으로(나머지는 walk0 유지)
    static readonly int[] MidIdleTiers = { 7, 8, 9, 13, 14, 17 };
    int IdleFrame => System.Array.IndexOf(MidIdleTiers, Def.tier) >= 0 ? _anim.FrameCount("walk") / 2 : 0;
    const string AuraKey = "Sprites/FX/fx_boss_aura";
    SpriteRenderer _aura;

    // 매 프레임: 몸 bbox 중심 x(BodyX)·지면에 맞춤, 좌우만 보스 flipX 따라감(상하 고정).
    void LateUpdate() => RefreshAura();
    public void RefreshAura() { // 캡처 툴용 공개
        if (_aura == null) return;
        var ls = transform.localScale;
        _aura.transform.localScale = new Vector3(1 / ls.x, 1 / ls.y, 1);
        _aura.flipX = _sr.flipX;
        _aura.color = _sr.color; // 시체 페이드 따라감
        _aura.transform.position = new Vector3(_vis.BodyX, WorldConfig.GroundY - (_aura.sprite.bounds.min.y), 0);
    }

    // Stage 3 타이밍: attack 12fps 균등 + 60% 지점 impact 에서만 데미지, hurt ≤250ms, dead 1회 후 0.6s 유지 + 0.4s 페이드.
    const float ATTACK_FPS = 12f, ATTACK_IMPACT = 0.6f, HURT_MAX = 0.25f, DEAD_FPS = 12f, CORPSE_HOLD = 0.6f, CORPSE_FADE = 0.4f;
    float _stop;      // 히트스톱 남은 시간(이 몬스터만)
    float _deadT;

    void Update() => Step(Time.deltaTime);

    public void Step(float dt) {
        if (_hitCooldownTimer > 0f) _hitCooldownTimer -= dt;
        if (_state == MobState.Dead) { UpdateCorpse(dt); return; }
        if (_stop > 0f) { _stop -= dt; return; }
        if (_state == MobState.Hurt) return;                           // hurt 끝나면 onDone 이 Walk 로
        if (_state == MobState.Attack && !_anim.Done && !_anim.IsStill) return; // 공격 모션은 끝까지
        var player = PlayerController.Local;
        if (player == null || player.IsDead) { Patrol(dt); return; }

        float dx = player.transform.position.x - transform.position.x;
        float dist = ActorVisual.Gap(player.transform, transform); // 몸 반폭 합 제외(spec 2-4)
        if (dist <= ATTACK_RANGE) {
            _face = dx >= 0 ? 1 : -1; _sr.flipX = _face < 0;
            _attackCd -= dt;
            _state = MobState.Attack;
            if (_attackCd <= 0f) {
                _attackCd = ATTACK_INTERVAL;
                _anim.Play("attack", loop: false, onDone: () => { if (_state == MobState.Attack) _anim.Still("walk", IdleFrame); }, fps: ATTACK_FPS, impactFrac: ATTACK_IMPACT);
            } else if (_anim.Clip != "attack" || _anim.Done) {
                if (!_anim.IsStill) _anim.Still("walk", IdleFrame);             // 공격 사이 대기(루프 재생 금지)
            }
        } else if (dist <= CHASE_RANGE) {
            _face = dx >= 0 ? 1 : -1; _sr.flipX = _face < 0;
            _state = MobState.Walk;
            _anim.Play("walk");
            transform.position += new Vector3(Mathf.Sign(dx) * CHASE_SPEED * dt, 0, 0);
        } else {
            Patrol(dt);
        }
    }

    void Impact() {
        if (_state != MobState.Attack || _anim.Clip != "attack") return;
        var player = PlayerController.Local;
        if (player != null && !player.IsDead) player.TakeDamage(Def.atk);
    }

    // 플레이어 impact 에서 호출: 이 몬스터 애니·이동만 정지 + 흰 플래시 1프레임 + 2px 넉백.
    public void HitStop(float sec, int dir) {
        if (_state != MobState.Dead) { _stop = Mathf.Max(_stop, sec); _anim.Freeze(sec); }
        _vis.Flash(0.033f);
        transform.position += new Vector3(dir * 2f / 40f, 0, 0);
        PlayerController.CombatLog?.Invoke($"hitstop t{Def.tier} {sec * 1000:0}ms");
    }

    void UpdateCorpse(float dt) {
        if (!_anim.Done) return;
        _deadT += dt;
        if (_deadT > CORPSE_HOLD) _sr.color = new Color(1, 1, 1, Mathf.Clamp01(1 - (_deadT - CORPSE_HOLD) / CORPSE_FADE));
        if (_deadT >= CORPSE_HOLD + CORPSE_FADE) { if (Application.isPlaying) Destroy(gameObject); else DestroyImmediate(gameObject); }
    }

    void Patrol(float dt) {
        if (_state == MobState.Dead) return;
        _state = MobState.Walk;
        _anim.Play("walk");
        float previous = transform.position.x;
        if (previous >= PatrolMaxX) _face = -1;
        else if (previous <= PatrolMinX) _face = 1;
        float x = previous + PATROL_SPEED * dt * _face;
        // A chase can leave the patrol range; return smoothly instead of teleporting to its edge.
        x = previous > PatrolMaxX ? Mathf.Max(x, PatrolMaxX) : previous < PatrolMinX ? Mathf.Min(x, PatrolMinX) : Mathf.Clamp(x, PatrolMinX, PatrolMaxX);
        _sr.flipX = _face < 0;
        transform.position = new Vector3(x, transform.position.y, 0);
    }

    // 데미지가 실제로 들어갔으면 true (히트스톱/플래시 대상 판정)
    public bool TakeDamage(int dmg) => ApplyDamage(CombatMath.Mitigate(dmg, Def.def));

    public bool TakeDamageBatch(IReadOnlyList<int> hits) {
        if (hits == null || hits.Count == 0) return false;
        int real = 0;
        foreach (int dmg in hits) real += CombatMath.Mitigate(dmg, Def.def);
        return ApplyDamage(real);
    }

    public bool TakeProjectileDamage(int damage) => ApplyDamage(CombatMath.Mitigate(damage, Def.def), true);

    bool ApplyDamage(int real, bool projectile = false) {
        if (_state == MobState.Dead) return false;
        if (!projectile && _hitCooldownTimer > 0f) return false;
        _hitCooldownTimer = HIT_COOLDOWN;
        Hp -= real;
        PlayerController.CombatLog?.Invoke($"damage t{Def.tier} -{real} hp={Mathf.Max(0, Hp)}");
        if (Hp <= 0) { Die(); return true; }
        _anim.Play("hurt", loop: false, onDone: () => { if (_state != MobState.Dead) { _state = MobState.Walk; _anim.Play("walk"); } }, fps: Mathf.Max(8f, _anim.FrameCount("hurt") / HURT_MAX));
        _state = MobState.Hurt;
        return true;
    }

    void Die() {
        _state = MobState.Dead;
        Hp = 0;
        _anim.Play("dead", loop: false, fps: Mathf.Max(DEAD_FPS, _anim.FrameCount("dead") / 1f)); // 쓰러짐 ≤1s → 사망~파괴 ≤2s
        var col = GetComponent<BoxCollider2D>();
        if (col) col.enabled = false;
        OnDied?.Invoke(this);
    }
}
}
