"""Book price = median gross gold from a 30-minute fixed-level r5 hunt.
Reuse sim.py combat model. No bosses, resale, new book, enhancement or level-ups.
Available normal-drop gear +0; prior weapon books owned. Both weapon families sampled.
This is a combat-cycle approximation, not a measured Unity play time.
"""
import json, math, random, statistics, sys
from pathlib import Path
import sim

ROOT = Path(__file__).resolve().parents[3]
BALANCE = json.loads((ROOT / 'unity_client/Assets/Resources/Config/balance.json').read_text())
PARAMS = {**sim.BASE, **json.loads(Path(__file__).with_name('r5_set.json').read_text())}
PARAMS['tables'] = {key: {str(n+1): v for n, v in enumerate(BALANCE[key])} for key in ('hp','atk','exp','gold','def')}
PARAMS['bosses'] = False
PREFIX = ['qi','rain','volc','king','end']


def trial(level, weapon, seed, seconds=1800):
    zone = 'C' if level >= 70 else 'B' if level >= 35 else 'A'
    mobs = [m for m in sim.M if m['tier'] in sim.ZONES[zone] and m['minLv'] <= level + BALANCE['spawnCap']]
    assert mobs, (level, zone)
    best_level = min(level//5*5, max(m['minLv']//5*5 for m in mobs))
    run = sim.Run(PARAMS, random.Random(seed))
    run.lv = level; run.wi = best_level//5; run.hi = run.wi; run.ai = run.wi
    run.hp = run.maxhp()
    # Current book is not owned yet; no circular pricing from its damage bonus.
    multipliers = [2.0,2.8,5.0,8.5,19] if weapon == 'sword' else [2.2,3.0,5.5,9.0,20]
    sim.SKILLS = [(lv, cd, mult) for lv, cd, mult in zip([20,40,60,80,100],[10,20,30,60,120],multipliers) if lv < level]
    pool = [(m, *run.stats(m)) for m in mobs]
    elapsed = gold = kills = deaths = 0
    while elapsed < seconds:
        mob, cycle, incoming = run.r.choices(pool, [m['w'] for m in mobs])[0]
        damage = incoming * cycle
        if damage >= run.hp:
            elapsed += run.hp / incoming + 3
            run.hp = run.maxhp(); deaths += 1; continue
        if elapsed + cycle > seconds: break
        elapsed += cycle
        run.hp = min(run.maxhp(), run.hp - damage + run.maxhp()*BALANCE['killHeal'])
        reward = BALANCE['gold'][mob['tier']-1]
        gold += run.r.randint(round(reward*.8), round(reward*1.4)); kills += 1
    return {'gold':gold,'kills':kills,'deaths':deaths,'zone':zone,'gearLevel':best_level}


def calculate(samples=200):
    prices, detail = {}, []
    for n, level in enumerate([20,40,60,80,100]):
        for weapon in ['sword','staff']:
            runs = [trial(level,weapon,10000+seed) for seed in range(samples)]
            amount = int(statistics.median(r['gold'] for r in runs))
            assert amount > 0
            key = PREFIX[n]+'_'+weapon; prices[key] = amount
            detail.append({'key':key,'level':level,'price':amount,'zone':runs[0]['zone'],'gearLevel':runs[0]['gearLevel'],'medianKills':statistics.median(r['kills'] for r in runs),'medianDeaths':statistics.median(r['deaths'] for r in runs)})
    return prices, detail

if __name__ == '__main__':
    prices, detail = calculate()
    assert trial(20,'sword',123) == trial(20,'sword',123), 'seed must be reproducible'
    assert trial(20,'sword',123,0)['gold'] == 0
    (ROOT / 'field/skillbook_prices.json').write_text(json.dumps(prices,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({'samplesPerBook':200,'seconds':1800,'method':'r5 combat-cycle approximation, gross normal-mob gold, fixed level, +0 normal-drop gear, prior books, no bosses/resale/new book','result':detail},ensure_ascii=False,indent=2))
