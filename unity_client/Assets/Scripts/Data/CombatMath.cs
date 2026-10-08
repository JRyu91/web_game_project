// field/core.js 의 전투 공식을 v3.0 설계(260909_아트_전면개편_계획)에 맞게 C#으로 포팅.
using UnityEngine;

namespace Game.Data {

public static class CombatMath {
    // 밸런스 r5(Config/balance.json): 몬스터 표 덮어쓰기 + 생존 레버. 몬스터/플레이어 Init 에서 1회.
    [System.Serializable] class Balance { public int[] hp, atk, exp, gold, def; public float killHeal = 0.05f, regenDelay = 2f, regenRate = 0.05f; public int spawnCap = 8; }
    public static float KillHeal = 0f, RegenDelay = 3f, RegenRate = 0.02f; // 파일 없을 때 = 기존 코드값
    public static int SpawnCap = 20;
    static bool _bal;
    public static void EnsureBalance() {
        if (_bal) return; _bal = true;
        var t = Resources.Load<TextAsset>("Config/balance");
        if (t == null) { Debug.LogWarning("[balance] Config/balance 없음 — 생성 데이터 그대로"); return; }
        var b = JsonUtility.FromJson<Balance>(t.text);
        var ms = GameData.Monsters;
        for (int i = 0; i < ms.Length; i++) {
            int k = ms[i].tier - 1;
            if (k < 0 || k >= b.hp.Length) continue;
            ms[i].hp = b.hp[k]; ms[i].atk = b.atk[k]; ms[i].exp = b.exp[k]; ms[i].gold = b.gold[k]; ms[i].def = b.def[k];
        }
        KillHeal = b.killHeal; RegenDelay = b.regenDelay; RegenRate = b.regenRate; SpawnCap = b.spawnCap;
    }

    // 방어력은 비율 경감 — 빼기로 하면 방어력이 공격력을 넘는 순간 무적이 된다.
    public static int Mitigate(int raw, int def) => Mathf.Max(1, Mathf.RoundToInt(raw * 100f / (100f + def)));

    // 플레이어 레벨 고정분: baseAtk 6 + lv*2.0 (Lv100 = 206)
    public const float BASE_ATK = 6f;
    public const float ATK_PER_LV = 2.0f;
    public const float BASE_HP = 60f;
    public const float HP_PER_LV = 16f;

    public static int PlayerFixedAtk(int lv) => Mathf.RoundToInt(BASE_ATK + lv * ATK_PER_LV);
    public static int PlayerMaxHp(int lv) => Mathf.RoundToInt(BASE_HP + lv * HP_PER_LV);

    // 무기 롤 + 강화% 적용 후 최종 데미지 (1회 타격)
    public static int RollWeaponDamage(WeaponDef w, int enhanceLevel, System.Random rng) {
        int roll = rng.Next(w.dmgLo, w.dmgHi + 1);
        float pct = 1f + GameData.EnhPct[Mathf.Clamp(enhanceLevel, 0, GameData.ENH_MAX)] / 100f;
        return Mathf.Max(1, Mathf.RoundToInt(roll * pct));
    }

    public static float StatDamageMultiplier(int points, float perPoint = 0.005f) => 1f + Mathf.Max(0, points) * perPoint;
    public static float CritChance(int luck, float perPoint = 0.001f, float cap = 0.3f) => Mathf.Min(cap, Mathf.Max(0, luck) * perPoint);
    public static int ApplyGrowthDamage(int damage, float multiplier, float critChance, float critDamage, System.Random rng) {
        // Zero luck keeps the pre-growth RNG sequence and damage unchanged.
        bool critical = critChance > 0f && rng.NextDouble() < critChance;
        return Mathf.Max(1, Mathf.RoundToInt(damage * multiplier * (critical ? critDamage : 1f)));
    }

    public static int GearDefWithEnhance(GearDef g, int enhanceLevel) {
        float pct = 1f + GameData.EnhPct[Mathf.Clamp(enhanceLevel, 0, GameData.ENH_MAX)] / 100f;
        return Mathf.RoundToInt(g.def * pct);
    }

    // 강화 시도: 성공하면 +1, 실패하면 8강+ 부터 파괴 확률 50%, 아니면 -1 (5강+)
    public struct EnhanceResult { public bool success; public bool destroyed; public int newLevel; }
    public static EnhanceResult TryEnhance(int currentLevel, System.Random rng) {
        int target = currentLevel + 1;
        if (target > GameData.ENH_MAX) return new EnhanceResult { success = false, destroyed = false, newLevel = currentLevel };
        float succ = GameData.EnhSucc[target];
        bool ok = rng.NextDouble() * 100.0 < succ;
        if (ok) return new EnhanceResult { success = true, destroyed = false, newLevel = target };
        if (currentLevel < 4) return new EnhanceResult { success = false, destroyed = false, newLevel = currentLevel }; // +1~4 무패널티
        bool destroy = currentLevel >= 7 && rng.NextDouble() < 0.5; // 8강 이상 실패는 50% 파괴
        int newLevel = destroy ? 0 : Mathf.Max(0, currentLevel - 1);
        return new EnhanceResult { success = false, destroyed = destroy, newLevel = destroy ? -1 : newLevel };
    }

    // 강화석 필요량: ceil(단계배수 * (L/5 + 1))
    public static int EnhanceStoneCost(int equipLevel, int targetEnhance) {
        float mult = targetEnhance <= 4 ? 2 : targetEnhance <= 7 ? 6 : targetEnhance <= 10 ? 14 : targetEnhance <= 14 ? 30 : 60;
        return Mathf.CeilToInt(mult * (equipLevel / 5f + 1));
    }
    public static int EnhanceGoldCost(int equipLevel, int targetEnhance) {
        float mult = targetEnhance <= 4 ? 2 : targetEnhance <= 7 ? 6 : targetEnhance <= 10 ? 14 : targetEnhance <= 14 ? 30 : 60;
        return Mathf.RoundToInt(40 * (equipLevel + 5) * mult);
    }

    // 남는 경험치 이월, 여러 레벨 한 번에 상승 가능
    public struct GainResult { public int level; public long exp; public int gained; }
    public static GainResult GainExp(int lv, long exp, long amount) {
        int level = lv; long xp = exp + amount; int gained = 0;
        while (level < GameData.LEVEL_MAX && xp >= GameData.XpToLevel(level)) {
            xp -= GameData.XpToLevel(level); level++; gained++;
        }
        if (level >= GameData.LEVEL_MAX) xp = 0;
        return new GainResult { level = level, exp = xp, gained = gained };
    }

    // 몬스터 처치 시 골드 범위 롤 (레벨 스케일 없음 — v3.0은 존별 고정)
    public static int RollMonsterGold(MonsterDef m, System.Random rng) {
        int lo = Mathf.RoundToInt(m.gold * 0.8f), hi = Mathf.RoundToInt(m.gold * 1.4f);
        return rng.Next(lo, hi + 1);
    }
}
}
