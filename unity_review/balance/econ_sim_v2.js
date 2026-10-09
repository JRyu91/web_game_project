// econ_sim.js 개선판. 사용: node econ_sim_v2.js [--seeds=30] [--bosses=off|summon] [--natural=on|off] [--early=hold|ramp:0.5]
//   [--interp=linear|prev|next|shape] [--potion=200] [--profile=allocated_all_books|unallocated_prior_books] [--policy=disassemble|sell] [--sweep]
// items.js / CSV 만 읽는다(파라미터는 전부 CLI 로). 원본과 다른 점: 시드 N개+백분위, 초반 처치율 대안, 보간 대안, 보스 소환/자연 스폰, 포션 소모율 인자, Lv100 도달 즉시 종료.
const fs = require('fs'), path = require('path');
const ITEMS = (process.argv.find(a => a.startsWith('--items=')) || '').slice(8) || path.join(__dirname, '../../field/items.js'); // --items=스냅샷경로 로 규칙 고정 가능
const I = require(path.resolve(ITEMS));
const SKILL_PRICES = require(path.join(path.dirname(path.resolve(ITEMS)), 'skillbook_prices.json'));
const A = Object.fromEntries(process.argv.slice(2).map(a => { const m = a.match(/^--([^=]+)(?:=(.*))?$/); return [m[1], m[2] ?? true]; }));
const D = { seeds: 30, bosses: 'off', natural: 'off', early: 'hold', interp: 'linear', potion: 200, profile: 'allocated_all_books', policy: 'disassemble', bossskill: 'all' };
const cfgOf = o => ({ ...D, ...A, ...o });
const MAX_H = 300, MILESTONES = [20, 35, 50, 70, 85, 100];
const BOOKS = [['qi', 20], ['rain', 40], ['volc', 60], ['king', 80], ['end', 100]];
const POTION_TARGET = 20, ENH_CAP = 10;
const LOGS = path.join(__dirname, '../../unity_client/Logs/');
const rows = f => fs.readFileSync(LOGS + f, 'utf8').trim().split('\n').slice(1).map(l => l.split(','));
const csv = rows('balance-playtest.csv'), bcsv = rows('balance-boss.csv');

const rawCurve = (profile, weapon) => { const m = new Map(); for (const r of csv) if (r[0] === profile && r[3] === weapon && Number(r[4]) === 86.625) m.set(+r[1], +r[6] * 2); return [...m].sort((a, b) => a[0] - b[0]); };
function interp(c, lv, mode) { // c: [[lv,y]...] 정렬됨. 범위 밖은 끝값.
  if (lv <= c[0][0]) return c[0][1];
  for (let i = 1; i < c.length; i++) if (lv <= c[i][0]) {
    const [a, x] = c[i - 1], [b, y] = c[i];
    return mode === 'prev' ? (lv >= b ? y : x) : mode === 'next' ? y : x + (y - x) * (lv - a) / (b - a);
  }
  return c[c.length - 1][1];
}
// 레벨 -> 시간당 처치. early: Lv<20 처리(hold = Lv20 값 유지, ramp:f = Lv1 에 f배에서 Lv20 까지 선형)
function kphFn(cfg, weapon) {
  const c = rawCurve(cfg.profile, weapon), mode = cfg.interp;
  let base;
  if (mode === 'shape') { // 미배분 곡선(촘촘함)의 모양 x 배분/미배분 비율(공통 레벨 보간)
    const u = rawCurve('unallocated_prior_books', weapon), ratio = c.filter(([l]) => u.some(([k]) => k === l)).map(([l, y]) => [l, y / u.find(([k]) => k === l)[1]]);
    base = lv => interp(u, lv, 'linear') * interp(ratio, lv, 'linear');
  } else base = lv => interp(c, lv, mode);
  const f = String(cfg.early).startsWith('ramp:') ? +cfg.early.slice(5) : 1, b20 = c[0][0];
  return lv => lv >= b20 || f === 1 ? base(lv) : base(b20) * (f + (1 - f) * (lv - 1) / (b20 - 1));
}
// 보스 처치 시간(초): balance-boss.csv, 데이터 레벨 밖은 끝값
function bossSecFn(tier, weapon, skills) {
  const c = bcsv.filter(r => +r[0] === tier && r[2] === weapon && r[3] === skills).map(r => [+r[1], +r[4]]).sort((a, b) => a[0] - b[0]);
  return c.length ? lv => interp(c, lv, 'linear') : null;
}
const BOSS_MINLV = { 19: 60, 20: 85, 21: 95 }; // MONS minLv 이상에서만 상대(데이터 최저 레벨)

function rngOf(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ t >>> 15, t | 1); t ^= t + Math.imul(t ^ t >>> 7, t | 61); return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
const armLv = t => t <= 20 ? t * 5 : 25 + (t - 21) * 5;
const armCls = t => t <= 4 ? 'common' : t <= 20 ? 'warrior' : 'mage';
const monWeight = t => 30.6 - 1.4 * (t - 1);

function run(weapon, seed, cfg) {
  const rng = rngOf(seed * 7919 + (weapon === 'staff' ? 1 : 0)), policy = cfg.policy, potionEvery = +cfg.potion;
  const s = I.normalize({ weaponKind: weapon }), cls = weapon === 'staff' ? 'mage' : 'warrior', kph = kphFn(cfg, weapon);
  const bsec = { 19: bossSecFn(19, weapon, cfg.bossskill), 20: bossSecFn(20, weapon, cfg.bossskill), 21: bossSecFn(21, weapon, cfg.bossskill) };
  let t = 0, kills = 0;
  const st = { ms: {}, bossN: { 19: 0, 20: 0, 21: 0 }, bossH: 0, potionGold: 0 };
  const owned = k => s.skills.includes(k + '_' + weapon), nextBook = () => BOOKS.find(([k]) => !owned(k)), W = () => s.inv.find(i => i.uid === s.equip.weapon);
  function gear() {
    const keep = new Set([s.equip.weapon, s.equip.helmet, s.equip.armor]);
    for (const slot of ['weapon', 'helmet', 'armor']) {
      const lvOf = i => slot === 'weapon' ? i.tier * 5 : armLv(i.tier);
      const ok = i => i.slot === slot && (slot === 'weapon' ? i.kind === weapon : ['common', cls].includes(armCls(i.tier)));
      const cur = s.inv.find(i => i.uid === s.equip[slot]), best = s.inv.filter(ok).sort((a, b) => lvOf(b) - lvOf(a))[0];
      if (best && (!cur || lvOf(best) > lvOf(cur))) { if (lvOf(best) <= s.level) { I.equip(s, slot, best.uid); keep.delete(cur?.uid); keep.add(best.uid); } else keep.add(best.uid); }
    }
    const junk = s.inv.filter(i => !keep.has(i.uid) && !Object.values(s.equip).includes(i.uid));
    if (!junk.length) return;
    if (policy === 'sell') I.sell(s, 0, junk.map(i => i.uid)); else I.disassemble(s, 0, junk.map(i => i.uid));
  }
  function shop() {
    if (s.potions < 15) { const q = POTION_TARGET - s.potions, p = I.potionPrice(s.level) * q; if (s.gold >= p) { I.buy(s, 'potion', q); st.potionGold += p; } }
    for (let nb = nextBook(); nb && s.level >= nb[1];) { if (!I.learn(s, nb[0] + '_' + weapon).ok) break; nb = nextBook(); }
    const nb = nextBook(), reserve = nb && s.level >= nb[1] ? SKILL_PRICES[nb[0] + '_' + weapon] : 0;
    for (;;) { const w = W(); if (w.enh >= ENH_CAP) break; const cost = I.enhCost(w); if (s.stones < cost.stones || s.gold - cost.gold < reserve) break; if (!I.enhance(s, w.uid, rng).ok) break; }
  }
  const allowed = () => { const ts = []; for (let tr = 1; tr <= 18; tr++) if (I.spawnAllowed(s, tr).ok) ts.push(tr); return ts; };
  const stamp = () => { for (const m of MILESTONES) if (s.level >= m && st.ms[m] === undefined) st.ms[m] = t / 3600; };
  function boss(tier, viaSummon) { // 소환(items.summon/summonAck) 또는 자연 스폰 -> I.kill(tier) -> 전투 시간 소비
    const f = bsec[tier]; if (!f || s.level < BOSS_MINLV[tier]) return false;
    if (viaSummon) { const r = I.summon(s, tier); if (!r.ok) return false; I.summonAck(s, tier, true, s.pendingSummon.token); }
    const res = I.kill(s, tier, rng); if (!res.ok) return false;
    const sec = f(s.level); t += sec; st.bossN[tier]++; st.bossH += sec / 3600;
    gear(); shop(); stamp(); return true;
  }
  while (t < MAX_H * 3600 && st.ms[100] === undefined) {
    const lv = s.level;
    if (lv >= 70 && s.zone !== 'C') I.map(s, 'C'); else if (lv >= 35 && lv < 70 && s.zone === 'A') I.map(s, 'B');
    const ts = allowed(), tot = ts.reduce((a, x) => a + monWeight(x), 0);
    let r = rng() * tot, tier = ts[ts.length - 1]; for (const x of ts) { r -= monWeight(x); if (r < 0) { tier = x; break; } }
    const res = I.kill(s, tier, rng);
    if (!res.ok) throw new Error('kill fail ' + res.code + ' t' + tier + ' z' + s.zone + ' L' + lv);
    const dt = 3600 / kph(lv); t += dt; kills++;
    if (potionEvery > 0 && kills % potionEvery === 0 && s.potions > 0) s.potions--;
    if (res.drop.item || res.drop.levelUp) gear();
    shop();
    if (res.drop.levelUp) stamp();
    if (cfg.bosses === 'summon' && s.zone !== 'A') { // 소환 버튼: 조건 충족 즉시(염제 우선)
      if (s.zone === 'C' && (s.killCountT20 || 0) >= 1000) boss(20, true);
      if ((s.killCountT19 || 0) >= 500) boss(19, true);
    }
    if (cfg.natural === 'on') { // 시간 기준 포아송: t19 1h(B,C) / t20 2h(C) / t21 24h(전 존). 레벨 미달이면 조우해도 무시
      const p = h => 1 - Math.exp(-dt / 3600 / h);
      if (s.zone !== 'A' && rng() < p(1)) boss(19, false);
      if (s.zone === 'C' && rng() < p(2)) boss(20, false);
      if (rng() < p(24)) boss(21, false);
    }
  }
  st.endH = t / 3600; return st;
}

const q = (a, p) => { const x = [...a].sort((u, v) => u - v), i = (x.length - 1) * p, lo = Math.floor(i); return x[lo] + (x[Math.min(lo + 1, x.length - 1)] - x[lo]) * (i - lo); };
const sum = a => { const x = a.filter(v => v !== undefined); return x.length ? [q(x, .1), q(x, .5), q(x, .9)] : [NaN, NaN, NaN]; };
const fmt = ([a, b, c]) => `${b.toFixed(1)} [${a.toFixed(1)}-${c.toFixed(1)}]`;
function study(cfg) {
  const n = +cfg.seeds, out = {};
  for (const w of ['sword', 'staff']) { const runs = Array.from({ length: n }, (_, i) => run(w, i + 1, cfg)); out[w] = { runs, lv100: sum(runs.map(r => r.ms[100])) }; }
  return out;
}
if (!A.sweep) {
  const cfg = cfgOf({}), o = study(cfg);
  console.log('cfg', JSON.stringify(cfg));
  for (const w of ['sword', 'staff']) {
    const r = o[w].runs;
    console.log(`${w}: ` + MILESTONES.map(m => `Lv${m} ${fmt(sum(r.map(x => x.ms[m])))}`).join(' | '));
    console.log(`  boss kills(avg) t19 ${(r.reduce((a, x) => a + x.bossN[19], 0) / r.length).toFixed(1)} t20 ${(r.reduce((a, x) => a + x.bossN[20], 0) / r.length).toFixed(1)} t21 ${(r.reduce((a, x) => a + x.bossN[21], 0) / r.length).toFixed(2)}, boss fight h ${(r.reduce((a, x) => a + x.bossH, 0) / r.length).toFixed(2)}`);
  }
} else { // 민감도: 기준에서 한 축씩만 바꿔 Lv100 시간(h) p50 [p10-p90]
  const base = cfgOf({});
  const axes = [['기준', {}], ['seeds=3(원본)', { seeds: 3 }],
    ['early=ramp:0.7', { early: 'ramp:0.7' }], ['early=ramp:0.5', { early: 'ramp:0.5' }], ['early=ramp:0.3', { early: 'ramp:0.3' }],
    ['interp=prev', { interp: 'prev' }], ['interp=next', { interp: 'next' }], ['interp=shape', { interp: 'shape' }],
    ['bosses=summon', { bosses: 'summon' }], ['natural=on', { natural: 'on' }], ['summon+natural', { bosses: 'summon', natural: 'on' }],
    ['potion=none', { potion: 0 }], ['potion=50', { potion: 50 }], ['potion=20', { potion: 20 }],
    ['profile=unallocated', { profile: 'unallocated_prior_books' }], ['policy=sell', { policy: 'sell' }]];
  console.log('| 변형 | sword Lv100 h | staff Lv100 h |\n|---|---|---|');
  for (const [name, o2] of axes) { const o = study({ ...base, ...o2 }); console.log(`| ${name} | ${fmt(o.sword.lv100)} | ${fmt(o.staff.lv100)} |`); }
}
