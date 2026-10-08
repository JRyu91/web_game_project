"""방치형 RPG 진행 몬테카를로 (라운드1, 통계학자). 킬 단위 이벤트 시뮬.
값 출처: gamedata.json (GameData.Generated.cs 파싱), CombatMath.cs, PlayerController.cs, MonsterController.cs.
코드에 없는 부분은 ASSUME 로 표시. 사용: python3 sim.py [N]
"""
import json, math, random, sys, csv, os
from statistics import median

D = json.load(open(os.path.join(os.path.dirname(__file__), 'gamedata.json')))
W = D['W']; H = D['H']; A = D['A']
M = [dict(tier=int(t), rank=r, minLv=int(ml), hp=int(hp), atk=int(at), df=int(df), exp=int(ex), gold=int(g), w=float(w))
     for t, r, ml, hp, at, df, ex, g, w in D['M']]
ZONES = {'A': range(1, 7), 'B': range(7, 13), 'C': range(13, 19)}
ENH_PCT = [0,1,2,3,4,6,8,10,13,16,19,23,27,31,35,40,45,50,55,60,65,70,75,80,85,90]
ENH_SUCC = [100,100,95,90,85,70,58,48,40,34,28,23,18,15,12,9,7,5,4,3,2.5,2,1.5,1.2,0.9,0.6]
SKILLS = [(20,10,2.0),(40,20,2.8),(60,30,5.0),(80,60,8.5),(100,120,19)]  # 검 기준 (lv, cd, mult)
DROP = {'basic':.05,'rare':.15,'unique':.45}

BASE = dict(
    mon_hp=1.0, mon_atk=1.0, xp_mult=1.0, hp_per_lv=16, base_hp=60, atk_per_lv=2.0, gear_def=1.0,
    drop_mult=1.0, enh_pct_mult=1.0, enh_succ_mult=1.0,
    # r2 공통 가정: 코드 근거 k = MaxAlive14 x 추격폭(2xCHASE_RANGE 6.5u=13u) / 이동폭(80-2x1.5=77u) = 2.36
    attackers=2.36,
    gap=0.5,            # ASSUME: 킬 사이 이동/타겟 전환 초 (스폰 1.2s, 14마리 상주라 대기 없음)
    stone={'basic':.10,'rare':.20,'unique':.40},  # ASSUME: 강화석 드랍률(킬당 1개). 코드에 드랍 경로 없음
    surv_kills=3,       # 존 선택: 풀피에서 최소 3킬 버틸 수 있는 존 중 exp/s 최대
    potions=3, cap_h=400, rule='highest', kind_lock=True, exp_tier_g=1.0,
    spawn_cap=20,       # MonsterSpawner: minLv <= playerLv + 20 (코드)
    atk_table=None, exp_mult=1.0, gold_mult=1.0, enh_succ=None, destroy_from=7, destroy_p=.5,
    kill_heal=0.0,      # r3: 처치 시 MaxHp 비율 회복
    c_atk_mult=1.0,     # r3: C존(t13-18) atk 배수
    bosses=False,       # r3: spec_boss_spawn 일정 + 보스 드랍 장비(t19 L85~90, t20 L90~100, t21 L100)
    t21_gate=False, enh_mode='auto', hoard_share=1/3, boss_gear=None,    # 'auto' 킬마다 자동 강화 / 'hoard' 만렙까지 모아두고 끝에 무기에 몰아 누름
    pot_price=None, tables=None, stone_amt=1,  # tables: {'hp'|'atk'|'exp'|'gold': {tier: v}} 덮어쓰기, stone_amt 'dl' = ceil(dropLevel/5+1)     # 제안 레버: 포션 골드 구매가(None=리필 없음, 코드 현재)
)

def xp_to(lv, p):
    v = 30.0*lv*lv
    if lv > 70: v *= 1.05**(lv-70)
    return round(v*p['xp_mult'])

def mit(raw, d): return max(1.0, raw*100/(100+d))

class Run:
    def __init__(s, p, rng):
        T = p['tables'] or {}; s.T = T
        s.X = {m['tier']: (T['exp'][str(m['tier'])] if 'exp' in T else m['exp'])*p['exp_mult']*p['exp_tier_g']**(m['tier']-1) for m in M}
        s.ATK = {m['tier']: (p['c_atk_mult'] if 13 <= m['tier'] <= 18 else 1)*(T['atk'][str(m['tier'])] if 'atk' in T else (p['atk_table'][m['tier']-1] if p['atk_table'] and m['tier'] <= len(p['atk_table']) else m['atk']*p['mon_atk'])) for m in M}  # 몬스터 exp 티어 보정
        s.p = p; s.r = rng
        s.lv = 1; s.xp = 0; s.gold = 0; s.stone = 0; s.t = 0.0; s.pot = p['potions']
        s.wi = 0; s.kind = 'sword'; s.hi = 0; s.ai = 0; s.enh = {'w':0,'h':0,'a':0}
        s.hp = s.maxhp(); s.deaths = {}; s.tband = {}; s.destroyed = 0
        s.dz = {}; s.tz = {}; s.kB = 0; s.kC = 0; s.bossget = {}; s.bosslog = []
        s.zone = 'A'
    def maxhp(s): return s.p['base_hp'] + s.lv*s.p['hp_per_lv']
    def pdef(s):
        e = lambda k: 1 + ENH_PCT[s.enh[k]]*s.p['enh_pct_mult']/100
        return round(H[s.hi][1]*s.p['gear_def']*e('h')) + round(A[s.ai][1]*s.p['gear_def']*e('a'))
    def wavg(s): return (W[s.wi][1]+W[s.wi][2])/2*(1+ENH_PCT[s.enh['w']]*s.p['enh_pct_mult']/100)
    def stats(s, m):
        """(kill cycle 초, 받는 DPS)"""
        p = s.p; mdef = s.T['def'][str(m['tier'])] if 'def' in s.T else m['df']; hp = (s.T['hp'][str(m['tier'])] if 'hp' in s.T else m['hp'])*p['mon_hp']
        dps = 1.5*mit(s.wavg(), mdef)  # 기본공격 = 무기 롤만
        fixed = 6 + s.lv*p['atk_per_lv']
        for lv, cd, mult in SKILLS:
            if s.lv >= lv: dps += mit((fixed+s.wavg())*mult, mdef)/cd
        ttk = max(1/1.5, hp/dps)
        inc = p['attackers']*mit(s.ATK[m['tier']], s.pdef())/1.2
        return ttk + p['gap'], inc
    def pick_zone(s):
        """rule='safe': 풀피에서 surv_kills 킬 이상 버티는 존 중 exp/s 최대 (A는 항상 허용).
        rule='greedy': 사망 패널티가 없으므로 사망 시간(3s 부활)까지 포함한 실효 exp/s 최대."""
        best, bz = -1, 'A'; s.cache = {}
        for z, tiers in ZONES.items():
            ms = [M[t-1] for t in tiers]
            ms = [m for m in ms if m['minLv'] <= s.lv + s.p['spawn_cap']] or ms[:1]  # MonsterSpawner cap
            st = [(m, *s.stats(m)) for m in ms]; s.cache[z] = st
            ws = sum(m['w'] for m in ms)
            if s.p['rule'] == 'highest':
                # r2 공통: Lv >= 존 최저 minLv 이고 풀피 surv_kills 킬 생존 가능한 가장 높은 존
                cyc = sum(c*m['w'] for m, c, i in st)/ws; inc = sum(i*m['w'] for m, c, i in st)/ws
                ok = z == 'A' or (s.lv >= M[tiers[0]-1]['minLv'] and s.maxhp()/inc >= s.p['surv_kills']*cyc)
                v = 'ABC'.index(z) if ok else -1
            elif s.p['rule'] == 'safe':
                cyc = sum(c*m['w'] for m, c, i in st)/ws; inc = sum(i*m['w'] for m, c, i in st)/ws
                ex = sum(s.X[m['tier']]*m['w'] for m in ms)/ws
                if z != 'A' and s.maxhp()/inc < s.p['surv_kills']*cyc: continue
                v = ex/cyc
            else:
                hpx = s.maxhp()
                num = sum(m['w']*s.X[m['tier']] for m, c, i in st if hpx/i >= c)
                den = sum(m['w']*(c if hpx/i >= c else hpx/i+3) for m, c, i in st)
                v = num/den
            if v > best: best, bz = v, z
        s.zone = bz
        s.pool = s.cache[bz]; s.wts = [m['w'] for m, c, i in s.pool]
    def drop(s, m):
        if s.r.random() > DROP[m['rank']]*s.p['drop_mult']: return
        idx = (m['minLv']//5*5)//5; slot = s.r.randrange(4)
        if slot <= 1:
            kind = 'sword' if s.r.random() < .5 else 'staff'
            if idx > s.wi or (kind != s.kind and not s.p['kind_lock']):  # 코드 그대로: 종류가 다르면 낮은 티어여도 교체 + 강화 리셋
                s.wi, s.kind, s.enh['w'] = idx, kind, 0
        elif slot == 2 and idx > s.hi: s.hi, s.enh['h'] = idx, 0
        elif slot == 3 and idx > s.ai: s.ai, s.enh['a'] = idx, 0
        else: return
        s.pick_zone()
    def enhance(s):
        # ASSUME: 자원 되는 한 강화 단계가 가장 낮은 슬롯부터(동률 무기 우선) 자동 강화
        while True:
            k = min('wha', key=lambda k: s.enh[k]); cur = s.enh[k]
            if cur >= 25: return
            L = (W, H, A)['wha'.index(k)][(s.wi, s.hi, s.ai)['wha'.index(k)]][0]; tgt = cur+1
            mult = 2 if tgt <= 4 else 6 if tgt <= 7 else 14 if tgt <= 10 else 30 if tgt <= 14 else 60
            sc = math.ceil(mult*(L/5+1)); gc = round(40*(L+5)*mult)
            if s.stone < sc or s.gold < gc: return
            s.stone -= sc; s.gold -= gc
            if s.r.random()*100 < min(100, (s.p['enh_succ'] or ENH_SUCC)[tgt]*s.p['enh_succ_mult']): s.enh[k] = tgt
            elif cur < 4: pass
            elif cur >= s.p['destroy_from'] and s.r.random() < s.p['destroy_p']:
                s.destroyed += 1; s.enh[k] = 0
                if k == 'w': s.wi = 0
                elif k == 'h': s.hi = 0
                else: s.ai = 0  # ASSUME: 파괴 = 해당 슬롯 기본 장비로 복귀
                s.pick_zone()
            else: s.enh[k] = cur-1
    def boss_fight(s, t):
        # 보스 1:1(k=1), 10분 제한, 포션 3개+구매분. 성공 시 exp/gold + 장비 드랍(무기50/투구25/갑옷25)
        m = M[t-1]; cyc, _ = s.stats(m); ttk = cyc - s.p['gap']
        inc = mit(s.ATK[t], s.pdef())/1.2; wl = W[s.wi][0]
        surv = s.hp/inc + s.pot*0.5*s.maxhp()/inc
        if ttk > min(600, surv):
            s.t += min(600, surv) + 3; s.hp = s.maxhp(); s.pot = 0
            s.bosslog.append((t, s.lv, 0, wl)); return
        s.t += ttk; used = max(0, math.ceil((ttk*inc - s.hp + 1)/(0.5*s.maxhp()))); s.pot -= min(s.pot, used)
        s.hp = max(1, s.hp - ttk*inc + used*0.5*s.maxhp())
        s.xp += s.X[t]; s.gold += (s.T['gold'][str(t)] if 'gold' in s.T else m['gold'])*s.p['gold_mult']
        while s.lv < 100 and s.xp >= xp_to(s.lv, s.p): s.xp -= xp_to(s.lv, s.p); s.lv += 1
        lvl = {int(a): b for a, b in (s.p['boss_gear'] or {19: [85, 90], 20: [90, 95, 100], 21: [100]}).items()}[t]; idx = s.r.choice(lvl)//5
        slot = s.r.randrange(4); s.bosslog.append((t, s.lv, 1, wl))
        if slot <= 1 and idx > s.wi: s.wi, s.enh['w'] = idx, 0
        elif slot == 2 and idx > s.hi: s.hi, s.enh['h'] = idx, 0
        elif slot == 3 and idx > s.ai: s.ai, s.enh['a'] = idx, 0
        else: s.pick_zone(); return
        s.bossget.setdefault(idx*5, s.t/3600); s.pick_zone()
    def boss_tick(s, cyc):
        z = s.zone
        if z in 'BC': s.kB += 1
        if z == 'C': s.kC += 1
        if z in 'BC' and (s.r.random() < cyc/3600 or s.kB >= 500): s.kB = 0; s.boss_fight(19)  # 소환 버튼 자동 사용 가정
        if z == 'C' and (s.r.random() < cyc/7200 or s.kC >= 1000): s.kC = 0; s.boss_fight(20)
        if s.r.random() < cyc/86400 and (not s.p['t21_gate'] or s.wi == 20): s.boss_fight(21)  # r4: L100 무기 착용 시에만
    def hoard_enhance(s):
        # 형 결정 3: 만렙 누적 자원을 무기 1개에 '계속 누름'. 최고 도달 단계, 최종 단계 기록
        L = W[s.wi][0]; e = 0; best = 0; tries = 0
        s.gold *= s.p['hoard_share']; s.stone *= s.p['hoard_share']  # 3부위 균등 분배 중 1부위(무기) 몫
        while e < 25:
            tgt = e+1; mult = 2 if tgt <= 4 else 6 if tgt <= 7 else 14 if tgt <= 10 else 30 if tgt <= 14 else 60
            sc = math.ceil(mult*(L/5+1)); gc = round(40*(L+5)*mult)
            if s.stone < sc or s.gold < gc: break
            s.stone -= sc; s.gold -= gc; tries += 1
            if s.r.random()*100 < (s.p['enh_succ'] or ENH_SUCC)[tgt]: e = tgt
            elif e < 4: pass
            elif e >= s.p['destroy_from'] and s.r.random() < s.p['destroy_p']: e = 0; s.destroyed += 1
            else: e -= 1
            best = max(best, e)
        return e, best, tries
    def run(s):
        s.pick_zone(); cap = s.p['cap_h']*3600; p = s.p
        while s.lv < 100 and s.t < cap:
            m, cyc, inc = s.r.choices(s.pool, s.wts)[0]; band = (s.lv-1)//10
            dmg = inc*cyc
            while s.hp - dmg <= s.maxhp()*.4 and s.pot > 0:
                # 포션: 40% 이하에서 50% 회복 (쿨 5s는 사이클 단위라 무시)
                s.pot -= 1; s.hp = min(s.maxhp(), s.hp + s.maxhp()*.5)
                if s.hp - dmg > s.maxhp()*.4: break
            if s.hp - dmg <= 0:
                s.tband[band] = s.tband.get(band, 0) + s.hp/inc + 3; s.t += s.hp/inc + 3; s.hp = s.maxhp()  # 사망: 3초 부활, 패널티 없음(코드)
                s.deaths[band] = s.deaths.get(band, 0)+1; s.dz[s.zone] = s.dz.get(s.zone, 0)+1
                continue
            s.hp = min(s.maxhp(), s.hp - dmg + s.maxhp()*p['kill_heal']); s.tz[s.zone] = s.tz.get(s.zone, 0)+cyc; s.t += cyc; s.tband[band] = s.tband.get(band, 0)+cyc
            g = (s.T['gold'][str(m['tier'])] if 'gold' in s.T else m['gold'])*p['gold_mult']; s.gold += s.r.randint(round(g*.8), round(g*1.4))
            if p['pot_price']:
                pr = round(4*(s.lv+5)**1.5) if p['pot_price'] == 'v2' else p['pot_price']
                while s.pot < p['potions'] and s.gold >= pr: s.gold -= pr; s.pot += 1
            if s.r.random() < p['stone'][m['rank']]: s.stone += 1 if p['stone_amt'] == 1 else math.ceil((m['minLv']//5*5)/5+1)
            s.xp += s.X[m['tier']]; up = False
            while s.lv < 100 and s.xp >= xp_to(s.lv, p): s.xp -= xp_to(s.lv, p); s.lv += 1; up = True
            if up: s.hp = s.maxhp() if p.get('lvup_heal', True) else s.hp; s.pick_zone()
            s.drop(m)
            if p['enh_mode'] == 'auto': s.enhance()
            if p['bosses']: s.boss_tick(cyc)
        s.res0 = (s.gold, s.stone)
        he = s.hoard_enhance() if s.p['enh_mode'] == 'hoard' else (None, None, 0)
        return dict(hfinal=he[0], hbest=he[1], htries=he[2], res0=s.res0, dz=s.dz, tz=s.tz, bossget=s.bossget, bosslog=s.bosslog, hours=s.t/3600, done=s.lv >= 100, lv=s.lv, wi=s.wi, hi=s.hi, ai=s.ai,
                    we=s.enh['w'], he=s.enh['h'], ae=s.enh['a'], destroyed=s.destroyed,
                    deaths=sum(s.deaths.values()), dband=s.deaths, tband=s.tband, zone=s.zone)

def q(xs, f):
    xs = sorted(xs); return xs[min(len(xs)-1, int(f*len(xs)))]

def batch(n, **ov):
    p = dict(BASE, **ov); rng = random.Random(42)
    return p, [Run(p, rng).run() for _ in range(n)]

def summary(res):
    h = [r['hours'] for r in res]
    return dict(p10=q(h,.1), p50=q(h,.5), p90=q(h,.9), done=sum(r['done'] for r in res)/len(res),
                deaths=median([r['deaths'] for r in res]), we=median([r['we'] for r in res]))

if __name__ == '__main__':
    # 셀프체크: 강화 파라미터/드랍 인덱스 상수 검증
    assert xp_to(1, BASE) == 30 and len(ENH_PCT) == 26 and W[20][0] == 100 and M[17]['atk'] == 1969
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 200
    ov = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}; tag = sys.argv[3] if len(sys.argv) > 3 else 'r1'
    p, res = batch(n, **ov)
    out = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(out, f'{tag}_runs.csv'), 'w', newline='') as f:
        w = csv.writer(f); w.writerow(['hours','done','lv','wtier','htier','atier','wenh','henh','aenh','destroyed','deaths'])
        for r in res: w.writerow([round(r['hours'],2), r['done'], r['lv'], r['wi'], r['hi'], r['ai'], r['we'], r['he'], r['ae'], r['destroyed'], r['deaths']])
    print(summary(res))
    # 레벨 구간별 사망률(회/시간), 구간 체류시간
    with open(os.path.join(out, f'{tag}_band.csv'), 'w', newline='') as f:
        w = csv.writer(f); w.writerow(['band','med_hours','deaths_per_hour'])
        for b in range(10):
            hs = [r['tband'].get(b, 0)/3600 for r in res]; ds = [r['dband'].get(b, 0) for r in res]
            th = sum(hs); w.writerow([f'Lv{b*10+1}-{b*10+10}', round(median(hs),2), round(sum(ds)/th,1) if th else ''])
    print(open(os.path.join(out, f'{tag}_band.csv')).read())
