// v3.0 자동플레이: 이동·공격·스킬은 자동, 성장 정보는 서버 상태에서 적용.
// 최근접 몬스터로 이동 -> 사거리서 정지 -> 자동공격. 무기 종류(칼/지팡이)에 따라 단일타겟/관통이 갈린다.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Game.Data;
using Game.Rendering;

namespace Game.Gameplay {

public enum PlayerState { Idle, Move, Attack, Hurt, Dead }

public class PlayerController : MonoBehaviour {
    public static PlayerController Local { get; private set; }

    [Header("성장")] public int Level = 1;
    public long Exp = 0;
    public long Gold = 0;

    [Header("장비 (인덱스 -1 = 미장착)")]
    public string WeaponKind = "sword";     // "sword" | "staff"
    public int WeaponTierIdx = 0;           // Swords/Staves 배열 인덱스
    public int WeaponEnhance = 0;
    public int HelmetTierIdx = -1;
    public int HelmetEnhance = 0;
    public int ArmorTierIdx = -1;
    public int ArmorEnhance = 0;
    public string GearClass = "warrior";    // 투구/갑옷 6티어 이상은 warrior|mage 계열 중 하나만 착용 가능

    [Header("물약 (아트_전면개편_계획 §F/§9)")]
    public int PotionCount = 3;               // 상점 판매 스텁 전이라 시작 보유량으로 임시 부여
    public int PotionHpThresholdPercent = 40;  // 드롭다운 대응값 — [20,30,40,50,60] 중 하나, 기본 40
    const float POTION_HEAL_PCT = 0.5f;
    const float POTION_COOLDOWN = 5f;
    float _potionCd;

    public int Hp { get; private set; }
    public int MaxHp { get; private set; }
    public bool IsDead => _state == PlayerState.Dead;
    public bool PendingKillRewards;
    public bool GameplayReady = true; // GameManager는 권위 join 완료까지 false, 독립 캡처·전투 검증은 true.
    public int Face { get; private set; } = 1;

    PlayerState _state = PlayerState.Idle;
    FrameAnimator _anim;
    SpriteRenderer _sr;
    GearAttachment _gear;
    ActorVisual _vis;
    float _atkCd;
    float _regenTimer;
    float _hitTimer;
    float _respawnTimer;
    System.Random _rng = new System.Random();
    Func<IEnumerable<MonsterController>> _monsterProvider;
    readonly Dictionary<string, float> _skillCd = new Dictionary<string, float>();
    public event Action<int, int> OnHpChanged;      // hp, maxHp
    public event Action<int, long, long> OnLevelChanged; // level, exp, need
    public event Action<SkillDef> OnSkillCast;

    // field/core.js 의 px 단위 상수를 그대로 옮기면 유니티 유닛(월드가 대략 -10~10) 기준으로 터무니없이 커진다
    // (몬스터가 화면을 한 프레임에 가로질러 "순간이동"처럼 보이던 버그의 원인). 1 unit = 40 "game px" 로 환산.
    const float PX_TO_UNIT = 1f / 40f;
    public static float MovePx = 75f; // game px/s (캡처 비교용 static, 기본값 불변)
    static float MOVE_SPEED => MovePx * PX_TO_UNIT;
    float _regenAcc; // 비전투 리젠 소수 누적(프레임레이트 무관). 지연·비율 = CombatMath.RegenDelay/RegenRate (balance.json)
    const float RESPAWN_SEC = 3f;
    const float HIT_COOLDOWN = 1f;
    const float SWORD_RANGE = 52f * PX_TO_UNIT;
    const float STAFF_RANGE_MULT = 1.4f;
    const float ATK_SPEED = 1.5f; // 초당 타격

    // 파츠 기반 관절 스켈레톤(BodySkeleton) 사용 여부. 기본 false — 기존에 확인된 프레임시트 렌더링을
    // 그대로 두고, 확인 후 켜서 비교해보라고 일부러 자동 적용하지 않았다(에디터 GUI를 못 보는 상태에서
    // 기본 렌더 경로를 바꾸는 건 위험 부담이 큼).
    public bool UseSkeletonRig = false;

    // Stage 3: 히트스톱(플레이어 애니 + 맞은 몬스터만, timeScale 무관). 캘리브레이션용 60~100ms.
    [Range(0.06f, 0.1f)] public float HitStopSec = 0.08f;
    public static Action<string> CombatLog; // 캡처 툴 타이밍 로그용(없으면 무시)
    MonsterController _atkTarget;
    bool _impactApplied;
    bool _swordAlt;
    readonly List<SkillDef> _pendingSkills = new List<SkillDef>();
    BodySkeleton _skeleton;

    public WeaponDef CurrentWeapon => WeaponKind == "sword" ? GameData.Swords[WeaponTierIdx] : GameData.Staves[WeaponTierIdx];
    public float Range => WeaponKind == "sword" ? (WeaponTierIdx <= 1 ? 26f : 44f) * PX_TO_UNIT : SWORD_RANGE * STAFF_RANGE_MULT;
    public bool Penetrating => WeaponKind == "staff";

    public void Init(bool isLocal, string gender, Func<IEnumerable<MonsterController>> monsterProvider) {
        CombatMath.EnsureBalance();
        _monsterProvider = monsterProvider;
        MaxHp = CombatMath.PlayerMaxHp(Level);
        Hp = MaxHp;

        _sr = gameObject.AddComponent<SpriteRenderer>();
        _sr.sortingOrder = 6;
        _anim = gameObject.AddComponent<FrameAnimator>();
        _anim.Renderer = _sr;
        _anim.Fps = 10f;
        _anim.SetSource($"{ActorScale.PlayerRoot}/{(gender == "female" ? "main_f" : "main_m")}");
        _anim.Play("idle");
        _anim.OnImpact += Impact;

        if (UseSkeletonRig) {
            _skeleton = gameObject.AddComponent<BodySkeleton>();
            _skeleton.Init(gender == "female" ? "main_f" : "main_m");
            if (_skeleton.IsReady) _sr.enabled = false; // 파츠 렌더러가 몸을 대신 그리므로 통짜 스프라이트는 숨김
        }

        _gear = gameObject.AddComponent<GearAttachment>();
        _gear.Init(_sr, _anim);
        RefreshGearVisual();
        _vis = gameObject.AddComponent<ActorVisual>();
        _vis.Init(_sr, ActorScale.Player, false);

        if (isLocal) Local = this;
    }

    // field/server.js GET/POST /save/:id 왕복용 — GameManager 가 접속 시 로드/주기 저장에 사용.
    public Game.Network.SaveData ToSaveData(string name, string gender) {
        return new Game.Network.SaveData {
            name = name, gender = gender, level = Level, exp = Exp, gold = Gold,
            weaponKind = WeaponKind, weaponTier = WeaponTierIdx, weaponEnh = WeaponEnhance,
            helmetTier = HelmetTierIdx, helmetEnh = HelmetEnhance,
            armorTier = ArmorTierIdx, armorEnh = ArmorEnhance,
            gearClass = GearClass, potionCount = PotionCount, potionThreshold = PotionHpThresholdPercent,
        };
    }

    public void ApplySave(Game.Network.SaveData d) {
        if (d == null) return;
        Level = Mathf.Max(1, d.level); Exp = d.exp; Gold = d.gold;
        if (!string.IsNullOrEmpty(d.weaponKind)) WeaponKind = d.weaponKind;
        WeaponTierIdx = Mathf.Clamp(d.weaponTier, 0, (WeaponKind == "sword" ? GameData.Swords.Length : GameData.Staves.Length) - 1);
        WeaponEnhance = d.weaponEnh;
        HelmetTierIdx = Mathf.Clamp(d.helmetTier, -1, GameData.Helmets.Length - 1);
        HelmetEnhance = d.helmetEnh;
        ArmorTierIdx = Mathf.Clamp(d.armorTier, -1, GameData.Armors.Length - 1);
        ArmorEnhance = d.armorEnh;
        if (!string.IsNullOrEmpty(d.gearClass)) GearClass = d.gearClass;
        PotionCount = d.potionCount;
        if (d.potionThreshold > 0) PotionHpThresholdPercent = d.potionThreshold;
        MaxHp = CombatMath.PlayerMaxHp(Level);
        Hp = MaxHp;
        RefreshGearVisual();
        OnHpChanged?.Invoke(Hp, MaxHp);
        OnLevelChanged?.Invoke(Level, Exp, GameData.XpToLevel(Level));
    }

    public void ApplyPreferences(Game.Network.SaveData d) {
        if (d != null && d.potionThreshold >= 20 && d.potionThreshold <= 60 && d.potionThreshold % 10 == 0)
            PotionHpThresholdPercent = d.potionThreshold;
    }

    void RefreshGearVisual() {
        var w = CurrentWeapon;
        _gear.SetWeapon(System.IO.Path.GetFileName(w.spritePath)); // WeaponsDir 8방향(5/6)
        // 투구·갑옷은 아이콘 전용(Stage 3 §3-5) — 몸 위에 렌더하지 않는다.
    }

    void Update() => Step(Time.deltaTime);

    // 캡처 툴이 고정 dt 로 직접 호출할 수 있게 분리.
    public void Step(float dt) {
        if (!GameplayReady) return;
        if (_state == PlayerState.Dead) { UpdateRespawn(dt); return; }
        if (this != Local) return; // 원격 플레이어는 유령 렌더만, AI/입력 없음 (RemotePlayerGhost 담당)

        HandleRegen(dt);
        _atkCd -= dt; // 공격 간격은 애니와 독립(루프 재생 안 함)
        UpdateSkills(dt);

        // 공격 클립은 끝까지(impact 포함) 재생 — 중간에 이동/idle 로 끊지 않는다
        if (_state == PlayerState.Attack && _anim.Clip != "idle" && !_anim.Done) return;

        var target = FindNearestTarget();
        if (target == null) {
            SetState(PlayerState.Idle);
        } else {
            float dx = target.transform.position.x - transform.position.x;
            float gap = ActorVisual.Gap(target.transform, transform); // 몸 반폭 합 제외 거리(spec 2-4)
            Face = dx >= 0 ? 1 : -1; // 좌우 반전은 몸 flipX 하나로(GearAttachment.SetFace)
            _gear.SetFace(Face);

            if (gap > Range) {
                SetState(PlayerState.Move);
                var p = transform.position;
                float x = Mathf.Clamp(p.x + Mathf.Sign(dx) * MOVE_SPEED * dt, WorldConfig.MapMargin, WorldConfig.MapWidth - WorldConfig.MapMargin);
                transform.position = new Vector3(x, p.y, p.z);
            } else if (_atkCd <= 0f) {
                StartAttack(target);
            } else {
                // 공격 사이: idle 루프를 처음부터(기획 검토 must-fix 2). 다음 공격이 어디서 끊어도 됨
                _state = PlayerState.Attack;
                if (_anim.Clip != "idle") _anim.Play("idle");
            }
        }

        _skeleton?.Animate(_state == PlayerState.Move, 1f);
    }

    void StartAttack(MonsterController target) {
        float interval = 1f / ATK_SPEED;
        _atkCd = interval;
        _atkTarget = target;
        _impactApplied = false;
        _state = PlayerState.Attack;
        _anim.Play(PickAttackClip(), loop: false);
        _anim.TrimTo(interval); // 간격 < 클립이면 impact 이후 회수 프레임부터 자름
        CombatLog?.Invoke($"start {_anim.Clip}");
    }

    // impact 프레임 진입: 기본공격 + 대기 중인 스킬 데미지를 여기서만 넣는다. 히트스톱은 1회(누적 없음).
    void Impact() {
        if (!GameplayReady || _state != PlayerState.Attack || _impactApplied) return;
        _impactApplied = true;
        var damage = new Dictionary<MonsterController, List<int>>();
        var pool = _monsterProvider?.Invoke()?.ToArray() ?? Array.Empty<MonsterController>();
        var hit = new HashSet<MonsterController>();
        if (_atkTarget != null && !_atkTarget.IsDead) DoAttack(_atkTarget, pool, damage);
        foreach (var sk in _pendingSkills) CastSkill(sk, pool, damage);
        _pendingSkills.Clear();
        // 동일 impact의 기본 공격·스킬을 묶는다. 처치 이벤트는 대상 수집이 끝난 뒤 발생한다.
        foreach (var entry in damage)
            if (entry.Key != null && entry.Key.TakeDamageBatch(entry.Value)) hit.Add(entry.Key);
        CombatLog?.Invoke($"impact {_anim.Clip} src={_anim.SourceIndex} hits={hit.Count}");
        if (hit.Count == 0) return;
        _anim.Freeze(HitStopSec);
        foreach (var m in hit) if (m != null) m.HitStop(HitStopSec, Face);
    }

    // 아트_전면개편_계획 §7: 스킬은 상점에서 습득 후 요구레벨 이상이면 쿨마다 자동 시전. 맞는 무기 장착 시만 발동.
    void UpdateSkills(float dt) {
        foreach (var s in GameData.Skills) {
            if (!CanCast(s)) continue;
            float cd = _skillCd.TryGetValue(s.key, out var v) ? v : 0f;
            if (cd <= 0f) { if (!_pendingSkills.Contains(s)) _pendingSkills.Add(s); continue; } // 다음 impact 에 발동
            _skillCd[s.key] = cd - dt;
        }
    }

    public bool OwnsSkill(string key) => Inv?.skills != null && Array.IndexOf(Inv.skills, key) >= 0;
    public bool CanCast(SkillDef skill) => OwnsSkill(skill.key) && skill.weapon == WeaponKind && Level >= skill.lv;
    public float SkillCooldown(string key) => _skillCd.TryGetValue(key, out var cd) ? Mathf.Max(0, cd) : 0;

    static void AddDamage(Dictionary<MonsterController, List<int>> damage, MonsterController target, int amount) {
        if (!damage.TryGetValue(target, out var hits)) damage[target] = hits = new List<int>();
        hits.Add(amount);
    }

    void CastSkill(SkillDef s, MonsterController[] pool, Dictionary<MonsterController, List<int>> damage) {
        if (!CanCast(s)) return;
        _skillCd[s.key] = s.cd;
        int atk = CombatMath.PlayerFixedAtk(Level) + CombatMath.RollWeaponDamage(CurrentWeapon, WeaponEnhance, _rng);
        int dmg = Mathf.Max(1, Mathf.RoundToInt(atk * s.mult));
        bool AheadOf(MonsterController m) => (m.transform.position.x - transform.position.x) * Face >= 0;
        foreach (var m in pool) {
            if (m == null || m.IsDead) continue;
            float dist = ActorVisual.Gap(m.transform, transform);
            bool hit = s.kind switch {
                "line" => AheadOf(m) && dist <= Range * 3f,
                "area" => dist <= 3.2f,
                "facing" => AheadOf(m),
                "map" => true,
                _ => false,
            };
            if (hit) AddDamage(damage, m, dmg);
        }
        OnSkillCast?.Invoke(s);
    }

    void DoAttack(MonsterController primaryTarget, MonsterController[] pool, Dictionary<MonsterController, List<int>> damage) {
        int dmg = CombatMath.RollWeaponDamage(CurrentWeapon, WeaponEnhance, _rng);
        if (!Penetrating) {
            AddDamage(damage, primaryTarget, dmg);
        } else {
            // 지팡이: 사거리 내 모든 몹 관통
            foreach (var m in pool) {
                if (m == null || m.IsDead) continue;
                if (ActorVisual.Gap(m.transform, transform) <= Range) AddDamage(damage, m, dmg);
            }
        }
    }

    MonsterController FindNearestTarget() {
        MonsterController best = null; float bestDist = float.MaxValue;
        var pool = _monsterProvider?.Invoke();
        if (pool == null) return null;
        foreach (var m in pool) {
            if (m == null || m.IsDead) continue;
            float d = ActorVisual.Gap(m.transform, transform);
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    void SetState(PlayerState s) {
        if (_state == s) return;
        _state = s;
        switch (s) {
            case PlayerState.Idle: _anim.Play("idle"); break;
            case PlayerState.Move: _anim.Play("walk"); break;
        }
    }

    // 검: attack ↔ attack1 교대, 지팡이: attack2 (무기는 숨김이어도 장착 종류로 고름)
    const bool STAFF_USES_ATTACK2 = true; // attack2 손 테이블 완료 → 지팡이 전용 밀기 모션
    string PickAttackClip() {
        if (WeaponKind == "staff" && STAFF_USES_ATTACK2) return "attack2";
        _swordAlt = !_swordAlt;
        return _swordAlt ? "attack" : "attack1";
    }

    void HandleRegen(float dt) {
        UpdatePotion(dt);
        if (_hitTimer > 0f) { _hitTimer -= dt; return; }
        if (Hp < MaxHp) {
            _regenAcc += MaxHp * CombatMath.RegenRate * dt;
            int add = (int)_regenAcc; _regenAcc -= add;
            Hp = Mathf.Min(MaxHp, Hp + add);
            OnHpChanged?.Invoke(Hp, MaxHp);
        }
    }

    void UpdatePotion(float dt) {
        if (_potionCd > 0f) { _potionCd -= dt; return; }
        if (PotionCount <= 0) return;
        if (Hp * 100 > MaxHp * PotionHpThresholdPercent) return; // 임계치 초과면 대기
        _potionCd = POTION_COOLDOWN;
        var net = Game.Network.NetworkClient.Instance;
        if (net != null) { if (net.Connected && net.Joined) net.Request(new Game.Network.InvReq { type = "potion" }); return; } // 회복은 응답 ok 때(GameManager → DrinkPotion)
        PotionCount--;
        DrinkPotion();
    }

    public void TakeDamage(int rawAtk) {
        if (!GameplayReady) return;
        if (_state == PlayerState.Dead) return;
        int def = (HelmetTierIdx >= 0 ? CombatMath.GearDefWithEnhance(GameData.Helmets[HelmetTierIdx], HelmetEnhance) : 0)
                + (ArmorTierIdx >= 0 ? CombatMath.GearDefWithEnhance(GameData.Armors[ArmorTierIdx], ArmorEnhance) : 0);
        int real = CombatMath.Mitigate(rawAtk, def);
        Hp -= real;
        _hitTimer = CombatMath.RegenDelay;
        _vis.Flash(0.033f);
        _anim.React("hurt"); // Visual reaction only: automatic attack/skill/cooldown timelines are not interrupted.
        CombatLog?.Invoke($"player hit -{real}");
        OnHpChanged?.Invoke(Hp, MaxHp);
        if (Hp <= 0) Die();
    }

    // F안: 골드·경험치·드랍은 서버 권위(API.md) — 처치 보고만 하고 결과는 ApplyState 로 덮어쓴다. 처치 회복 5%는 클라.
    public int GainKillReward(int tier, string receipt) {
        var net = Game.Network.NetworkClient.Instance;
        if (net == null || !net.Connected || !net.Joined) return 0;
        return net.Request(new Game.Network.InvReq { type = "kill", tier = tier, receipt = receipt });
    }

    public void ConfirmKillReward() {
        Hp = Mathf.Min(MaxHp, Hp + Mathf.RoundToInt(MaxHp * CombatMath.KillHeal));
        OnHpChanged?.Invoke(Hp, MaxHp);
    }

    public Game.Network.InvState Inv { get; private set; } // 마지막 서버 상태(UI 가 읽음)

    // 서버 inv.state → 레벨·경험치·골드·포션·장착 장비. 레벨이 오르면 풀피(기존 관례).
    public void ApplyState(Game.Network.InvState st) {
        if (st == null || st.equip == null) return;
        Inv = st;
        _pendingSkills.RemoveAll(skill => !OwnsSkill(skill.key));
        bool up = st.level > Level;
        Level = Mathf.Max(1, st.level); Exp = st.exp; Gold = st.gold; PotionCount = st.potions;
        Game.Network.InvItem Find(int uid) => uid == 0 || st.inv == null ? null : System.Array.Find(st.inv, x => x.uid == uid);
        var w = Find(st.equip.weapon);
        if (w != null) { WeaponKind = w.kind; WeaponTierIdx = w.tier; WeaponEnhance = w.enh; GearClass = w.kind == "staff" ? "mage" : "warrior"; }
        var h = Find(st.equip.helmet); HelmetTierIdx = h != null ? h.tier : -1; HelmetEnhance = h != null ? h.enh : 0;
        var a = Find(st.equip.armor); ArmorTierIdx = a != null ? a.tier : -1; ArmorEnhance = a != null ? a.enh : 0;
        MaxHp = CombatMath.PlayerMaxHp(Level);
        if (up) Hp = MaxHp; else Hp = Mathf.Min(Hp, MaxHp);
        _pendingSkills.RemoveAll(skill => !CanCast(skill));
        RefreshGearVisual();
        OnHpChanged?.Invoke(Hp, MaxHp);
        OnLevelChanged?.Invoke(Level, Exp, GameData.XpToLevel(Level));
    }

    // potion 응답 ok 일 때 회복(차감은 서버)
    public void DrinkPotion() {
        Hp = Mathf.Min(MaxHp, Hp + Mathf.RoundToInt(MaxHp * POTION_HEAL_PCT));
        OnHpChanged?.Invoke(Hp, MaxHp);
    }

    void Die() {
        _state = PlayerState.Dead;
        Hp = 0;
        _anim.Play("dead", loop: false);
        _respawnTimer = RESPAWN_SEC;
        OnHpChanged?.Invoke(Hp, MaxHp);
    }

    void UpdateRespawn(float dt) {
        _respawnTimer -= dt;
        if (_respawnTimer <= 0f) {
            Hp = MaxHp; _state = PlayerState.Idle; _anim.Play("idle");
            OnHpChanged?.Invoke(Hp, MaxHp);
        }
    }

    // NetworkClient 가 1초(state)/150ms(pos) 주기로 물어보는 스냅샷.
    public void RestoreTransport(Game.Network.Snapshot state) {
        transform.position = new Vector3(float.IsNaN(state.x) || float.IsInfinity(state.x) ? WorldConfig.MapMargin : Mathf.Clamp(state.x, WorldConfig.MapMargin, WorldConfig.MapWidth - WorldConfig.MapMargin), transform.position.y, 0);
        Face = state.face < 0 ? -1 : 1;
        MaxHp = CombatMath.PlayerMaxHp(Level);
        Hp = state.dead ? 0 : Mathf.Clamp(state.hp, 1, MaxHp);
        _state = state.dead ? PlayerState.Dead : PlayerState.Idle;
        _respawnTimer = FiniteTimer(state.respawnRemaining, RESPAWN_SEC);
        _potionCd = FiniteTimer(state.potionCd, POTION_COOLDOWN);
        _atkCd = FiniteTimer(state.attackCd, 1f / ATK_SPEED);
        _hitTimer = FiniteTimer(state.hitDelay, CombatMath.RegenDelay);
        _regenAcc = FiniteTimer(state.regenAcc, 1f);
        _skillCd.Clear(); _pendingSkills.Clear();
        foreach (var timer in state.skillTimers ?? Array.Empty<Game.Network.SkillTimer>()) {
            var skill = GameData.Skills.FirstOrDefault(s => s.key == timer.key);
            if (!string.IsNullOrEmpty(skill.key)) _skillCd[timer.key] = FiniteTimer(timer.remaining, skill.cd);
        }
        _anim.Play(state.dead ? "dead" : "idle", !state.dead);
        _gear.SetFace(Face);
        OnHpChanged?.Invoke(Hp, MaxHp);
    }

    static float FiniteTimer(float value, float max) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : Mathf.Clamp(value, 0, max);

    public Game.Network.Snapshot GetSnapshot() {
        return new Game.Network.Snapshot {
            x = transform.position.x, face = Face,
            skillTimers = _skillCd.Select(pair => new Game.Network.SkillTimer { key = pair.Key, remaining = pair.Value }).ToArray(),
            potionCd = _potionCd, attackCd = _atkCd, hitDelay = _hitTimer, regenAcc = _regenAcc,
            dead = IsDead, respawnRemaining = _respawnTimer,
            hp = Hp, maxHp = MaxHp, level = Level, exp = Exp,
            equip = new Game.Network.EquipMsg {
                weapon = new Game.Network.EquipWeaponMsg { kind = WeaponKind, tier = WeaponTierIdx, enh = WeaponEnhance }
            },
        };
    }
}
}
