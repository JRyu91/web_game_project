using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Game.Data;
using Game.Rendering;

namespace Game.Gameplay {

// field/core.js spawnTable/pickSpawn 포팅 + 존 필터. 존별 티어 풀 = Resources/Config/zone_spawn.json.
public class MonsterSpawner : MonoBehaviour {
    public float WorldMinX = WorldConfig.MapMargin, WorldMaxX = WorldConfig.MapWidth - WorldConfig.MapMargin;
    public int MaxAlive = 5;
    public int MapMonsterCap { get; private set; }

    public void SetPopulation(int playersOnMap) {
        MapMonsterCap = Mathf.Max(0, playersOnMap) * 5;
        // Five owned encounters per real player keep the whole-map total <= population * 5.
        MaxAlive = playersOnMap > 0 ? 5 : 0;
        foreach (var monster in _alive.Where(m => MaxAlive == 0 || m != Boss).Skip(MaxAlive).ToArray()) Remove(monster); // 보스는 상한 밖 1칸, 인원 0이면 보스도 정리
    }
    public float SpawnInterval = 1.2f;
    // 보스 등장 방식 미정이라 weight 0 티어(t19-21)가 풀에 있으면 이 값으로 드물게 등장. USER_DECISION.
    public float ZeroWeightFallback = 1f;

    // ── 보스 스폰(unity_review/spec_boss_spawn.md): 필드 보스 1마리, 존 체류 중 매초 확률, 소환 카운트, t21 10분 퇴장 ──
    // tier → (등장 존, 평균 간격 초, 소환 필요 처치 수(0=소환 없음))
    static readonly (int tier, Zone[] zones, float mean, int need)[] Bosses = {
        (19, new[] { Zone.B, Zone.C }, 3600f, 500),
        (20, new[] { Zone.C }, 7200f, 1000),
        (21, new[] { Zone.A, Zone.B, Zone.C }, 86400f, 0),
    };
    public const float T21_LIFETIME = 600f;
    public float BossTimeScale = 1f;                      // 검증용 가속(배치 로그). 게임 = 1
    public MonsterController Boss { get; private set; }
    float _bossTick, _bossAge;

    // 소환 카운트는 서버 권위(state.killCountT19/T20). 여기선 필드 조건(보스 없음·등장 존)만.
    // 보스는 일반 상한(MaxAlive) 밖 1칸. 필드가 꽉 차도 소환 가능(server.js spawn 도 같은 규칙)
    int RegularAlive => _alive.Count(m => m != Boss);
    public bool SummonZoneOk(int tier) => Bosses.Any(b => b.tier == tier && b.need > 0 && b.zones.Contains(CurZone));
    public bool CanSummon(int tier) => Boss == null && MaxAlive > 0 && Bosses.Any(b => b.tier == tier && b.need > 0 && b.zones.Contains(CurZone));
    public static int Need(int tier) => Bosses.First(b => b.tier == tier).need;
    static Zone CurZone => ZoneController.Current != null ? ZoneController.Current.Zone : Zone.A;
    public bool SpawnSummoned(int tier) { if (!CanSummon(tier)) return false; var pl = PlayerController.Local; SpawnBossAt(tier, pl != null ? pl.transform.position.x + pl.Face * 4f : (float?)null); return true; } // 소환 보스는 플레이어 앞 160px(화면 안) // 서버 summon ok 후에만 호출

    // 관리자 소환: 서버 admin_spawn ok 후에만 호출. 등록(영수증)은 OnMonsterSpawned → 일반 spawn 경로가 한다
    public bool SpawnTier(int tier, float x) {
        int idx = Array.FindIndex(GameData.Monsters, m => m.tier == tier); // MonsterDef 는 struct
        if (idx < 0 || (tier < 19 ? RegularAlive >= MaxAlive : Boss != null)) return false;
        var mc = Spawn(GameData.Monsters[idx], Mathf.Clamp(x, WorldMinX, WorldMaxX));
        if (tier >= 19) { Boss = mc; _bossAge = 0; }
        return true;
    }

    // 매초 1회 굴림(dt 누적). 반환: 이번 틱에 스폰한 보스 tier(없으면 0) — 검증 로그용
    public int TickBoss(float dt) {
        if (Boss != null) {
            _bossAge += dt;
            if (Boss.Def.tier == 21 && _bossAge >= T21_LIFETIME && !Boss.IsDead) { Remove(Boss); } // 10분 내 못 잡으면 사라짐
            return 0;
        }
        if (RegularAlive >= MaxAlive) { _bossTick = 0; return 0; }
        _bossTick += dt * BossTimeScale;
        while (_bossTick >= 1f) {
            _bossTick -= 1f;
            foreach (var b in Bosses)
                if (b.zones.Contains(CurZone) && _rng.NextDouble() < 1.0 / b.mean) { SpawnBoss(b.tier); return b.tier; }
        }
        return 0;
    }

    void CountKill(MonsterController m) { if (m == Boss) Boss = null; }

    void SpawnBoss(int tier) => SpawnBossAt(tier, null);
    void SpawnBossAt(int tier, float? x) {
        var def = GameData.Monsters.First(m => m.tier == tier);
        Boss = Spawn(def, x.HasValue ? Mathf.Clamp(x.Value, WorldMinX, WorldMaxX) : Mathf.Lerp(WorldMinX, WorldMaxX, (float)_rng.NextDouble()));
        _bossAge = 0;
        if (Application.isPlaying) { Game.Rendering.HitFx.Play("fx_boss_warning", new Vector3(Boss.transform.position.x, WorldConfig.GroundY + 0.6f, 0), 10f, 2f, false, 4); Game.Rendering.HitFx.Shake(2, 0.4f); } // 보스 등장 마법진(몸 뒤)
    }

    [System.Serializable] class PoolCfg { public int[] A, B, C; }
    static PoolCfg _pools;

    readonly List<MonsterController> _alive = new List<MonsterController>();
    float _timer;
    System.Random _rng = new System.Random();

    public IReadOnlyList<MonsterController> Alive => _alive;
    public System.Action<MonsterController> OnMonsterDied, OnMonsterSpawned, OnMonsterRemoved;

    public void Remove(MonsterController monster) {
        if (monster == null) return;
        _alive.Remove(monster); CountKill(monster);
        OnMonsterRemoved?.Invoke(monster);
        if (Application.isPlaying) Destroy(monster.gameObject); else DestroyImmediate(monster.gameObject);
    }

    Zone _lastZone;
    bool _hasZone;
    void OnEnable() {
        ZoneController.OnZoneChanged += OnZone;
        if (!_hasZone || _lastZone != CurZone) OnZone(CurZone);
    }
    void OnDisable() => ZoneController.OnZoneChanged -= OnZone;

    // 존 전환: 살아있는 몬스터 전부 제거 후 새 풀로 재시작
    void OnZone(Zone z) {
        _lastZone = z; _hasZone = true;
        foreach (var m in _alive.ToArray()) if (m != null) Remove(m);
        _alive.Clear(); Boss = null;
        _timer = 0;
    }

    static int[] TierPool(Zone z) {
        _pools ??= JsonUtility.FromJson<PoolCfg>(Resources.Load<TextAsset>("Config/zone_spawn").text);
        return z == Zone.A ? _pools.A : z == Zone.B ? _pools.B : _pools.C;
    }

    void Update() {
        _alive.RemoveAll(m => m == null);
        _timer -= Time.deltaTime;
        TickBoss(Time.deltaTime);
        if (_timer <= 0f && RegularAlive < MaxAlive) {
            _timer = SpawnInterval;
            SpawnOne();
        }
    }

    float W(MonsterDef m) => m.weight > 0 ? m.weight : ZeroWeightFallback;

    void SpawnOne() {
        int playerLv = PlayerController.Local != null ? PlayerController.Local.Level : 1;
        CombatMath.EnsureBalance();
        int cap = playerLv + CombatMath.SpawnCap; // r5: Lv+8
        var tiers = TierPool(ZoneController.Current != null ? ZoneController.Current.Zone : Zone.A);
        var zonePool = GameData.Monsters.Where(m => tiers.Contains(m.tier) && m.tier < 19).ToList(); // 보스는 TickBoss 로만
        var pool = zonePool.Where(m => m.minLv <= cap).ToList();
        if (pool.Count == 0) pool.Add(zonePool.OrderBy(m => m.minLv).First());
        float total = pool.Sum(W);
        double r = _rng.NextDouble() * total;
        MonsterDef pick = pool[0];
        foreach (var m in pool) { r -= W(m); if (r < 0) { pick = m; break; } }

        Spawn(pick, Mathf.Lerp(WorldMinX, WorldMaxX, (float)_rng.NextDouble()));
    }

    MonsterController Spawn(MonsterDef pick, float x) {
        var go = new GameObject($"Mob_{pick.name}");
        go.transform.SetParent(transform, false);
        var mc = go.AddComponent<MonsterController>();
        float patrolHalf = 1.5f;
        mc.Init(pick, new Vector3(x, 0, 0), Mathf.Max(WorldMinX, x - patrolHalf), Mathf.Min(WorldMaxX, x + patrolHalf));
        mc.OnDied += m => { _alive.Remove(m); CountKill(m); OnMonsterDied?.Invoke(m); };
        _alive.Add(mc);
        OnMonsterSpawned?.Invoke(mc);
        return mc;
    }
}
}
