// 장기 경제/성장 시뮬. 사용: node econ_sim.js  -> econ_sim_result.md 생성
// 서버 규칙은 field/items.js 를 그대로 호출(수정 없음). 보스 소환/보스 처치 제외.
const fs = require('fs'), path = require('path');
const I = require('../../field/items');
const SKILL_PRICES = require('../../field/skillbook_prices.json');
const MAX_H = 300, SNAP_H = 60, SEEDS = [1, 2, 3], MILESTONES = [20, 35, 50, 70, 85, 100];
const BANDS = [[1, 20], [20, 35], [35, 50], [50, 70], [70, 85], [85, 100]];
const BOOKS = [['qi', 20], ['rain', 40], ['volc', 60], ['king', 80], ['end', 100]];
const POTION_TARGET = 20, POTION_PER_KILLS = 200; // 가정: 처치 200회당 포션 1개 소모(CSV potions≈0)
const ENH_CAP = 10;

// 처치 처리량: balance-playtest.csv (30분 처치 수 -> 처치/시간). 프로필 2종, 기본은 allocated_all_books.
const csv = fs.readFileSync(path.join(__dirname, '../../unity_client/Logs/balance-playtest.csv'), 'utf8').trim().split('\n').slice(1).map(l => l.split(','));
function curve(profile, weapon) {
  const m = new Map();
  for (const r of csv) if (r[0] === profile && r[3] === weapon && Number(r[4]) === 86.625) m.set(+r[1], +r[6] * 2);
  return [...m].sort((a, b) => a[0] - b[0]);
}
function kph(c, lv) { // 선형 보간, 범위 밖은 끝값 유지(Lv<20 은 Lv20 값 가정)
  if (lv <= c[0][0]) return c[0][1];
  for (let i = 1; i < c.length; i++) if (lv <= c[i][0]) { const [a, x] = c[i - 1], [b, y] = c[i]; return x + (y - x) * (lv - a) / (b - a); }
  return c[c.length - 1][1];
}
function rngOf(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ t >>> 15, t | 1); t ^= t + Math.imul(t ^ t >>> 7, t | 61); return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

// 방어구 레벨/클래스 (items.js 표 인덱스 규칙: 0~4 common, 5~20 warrior, 21~36 mage)
const armLv = t => t <= 20 ? t * 5 : 25 + (t - 21) * 5;
const armCls = t => t <= 4 ? 'common' : t <= 20 ? 'warrior' : 'mage';
const monWeight = t => 30.6 - 1.4 * (t - 1); // GameData.Generated.cs weight (t1..t18)

function run(weapon, seed, profile, policy) {
  const rng = rngOf(seed * 7919 + (weapon === 'staff' ? 1 : 0));
  const s = I.normalize({ weaponKind: weapon });
  const cls = weapon === 'staff' ? 'mage' : 'warrior', c = curve(profile, weapon);
  let t = 0, kills = 0;
  const st = { ms: {}, goldAt: {}, bookLv: {}, bookLearn: {}, bookHeld: {}, band: BANDS.map(() => ({ h: 0, kill: 0, sell: 0, kills: 0, stones: 0 })), spend: { potion: 0, book: 0, enh: 0 }, enhTries: 0, destroyed: 0, goldIdle: [], maxEnh: 0, stonesIn: { drop: 0, dis: 0 }, firstEnhAt: {} };
  const owned = k => s.skills.includes(k + '_' + weapon);
  const nextBook = () => BOOKS.find(([k]) => !owned(k));
  const W = () => s.inv.find(i => i.uid === s.equip.weapon);
  const bandOf = lv => BANDS.findIndex(([a, b]) => lv < b) < 0 ? BANDS.length - 1 : BANDS.findIndex(([a, b]) => lv < b);

  function gear() { // 장비 정리: 더 좋은 건 착용(또는 레벨 대기 보관), 나머지는 정책대로 처분
    const keep = new Set([s.equip.weapon, s.equip.helmet, s.equip.armor]);
    for (const slot of ['weapon', 'helmet', 'armor']) {
      const lvOf = i => slot === 'weapon' ? i.tier * 5 : armLv(i.tier);
      const ok = i => i.slot === slot && (slot === 'weapon' ? i.kind === weapon : ['common', cls].includes(armCls(i.tier)));
      const cur = s.inv.find(i => i.uid === s.equip[slot]);
      const best = s.inv.filter(ok).sort((a, b) => lvOf(b) - lvOf(a))[0];
      if (best && (!cur || lvOf(best) > lvOf(cur))) {
        if (lvOf(best) <= s.level) { I.equip(s, slot, best.uid); keep.delete(cur?.uid); keep.add(best.uid); }
        else keep.add(best.uid); // 레벨 대기
      }
    }
    const junk = s.inv.filter(i => !keep.has(i.uid) && !Object.values(s.equip).includes(i.uid));
    if (!junk.length) return;
    if (policy === 'sell') { const r = I.sell(s, 0, junk.map(i => i.uid)); if (r.ok !== false) bandOf && (st.band[bandOf(s.level)].sell += r.sell.gold); }
    else { const r = I.disassemble(s, 0, junk.map(i => i.uid)); if (r.ok) { st.stonesIn.dis += r.disassemble.stones; st.band[bandOf(s.level)].stones += r.disassemble.stones; } }
  }
  function shop() {
    if (s.potions < 15) { // 20 개까지 보충
      const q = POTION_TARGET - s.potions, p = I.potionPrice(s.level) * q;
      if (s.gold >= p) { I.buy(s, 'potion', q); st.spend.potion += p; }
    }
    for (let nb = nextBook(); nb && s.level >= nb[1];) { // 스킬북: 레벨+골드 되는 즉시
      const key = nb[0] + '_' + weapon, price = SKILL_PRICES[key];
      st.bookLv[nb[0]] ??= t; // 레벨 충족 시각
      if (!I.learn(s, key).ok) break;
      st.spend.book += price; st.bookLearn[nb[0]] = t; nb = nextBook();
    }
    const nb = nextBook(), reserve = nb && s.level >= nb[1] ? SKILL_PRICES[nb[0] + '_' + weapon] : 0;
    for (;;) { // 무기 강화 +10 까지 (위험 구간 +15 이상 없음), 책 구매용 골드는 보존
      const w = W(); if (w.enh >= ENH_CAP) break;
      const cost = I.enhCost(w);
      if (s.stones < cost.stones || s.gold - cost.gold < reserve) break;
      const r = I.enhance(s, w.uid, rng); if (!r.ok) break;
      st.spend.enh += cost.gold; st.enhTries++;
      if (r.enh.result === 'destroy') st.destroyed++;
      st.maxEnh = Math.max(st.maxEnh, W().enh);
    }
  }
  const wsum = lv => { const ts = []; for (let tr = 1; tr <= 18; tr++) if (I.spawnAllowed(s, tr).ok) ts.push(tr); return ts; };

  while (t < MAX_H * 3600 && !(st.ms[100] !== undefined && t / 3600 > st.ms[100] + 5 && st.snap)) {
    if (!st.snap && t >= SNAP_H * 3600) st.snap = { level: s.level, gold: s.gold, stones: s.stones, enh: W().enh, tier: W().tier, skills: s.skills.length, kills };
    const lv = s.level;
    if (lv >= 70 && s.zone !== 'C') I.map(s, 'C'); else if (lv >= 35 && lv < 70 && s.zone === 'A') I.map(s, 'B');
    const ts = wsum(), tot = ts.reduce((a, x) => a + monWeight(x), 0);
    let r = rng() * tot, tier = ts[ts.length - 1];
    for (const x of ts) { r -= monWeight(x); if (r < 0) { tier = x; break; } }
    const before = s.stones, res = I.kill(s, tier, rng);
    if (!res.ok) throw new Error('kill fail ' + res.code + ' t' + tier + ' z' + s.zone + ' L' + lv);
    t += 3600 / kph(c, lv); kills++;
    const b = st.band[bandOf(lv)]; b.kill += res.drop.gold; b.kills++; b.stones += res.drop.stones; st.stonesIn.drop += res.drop.stones;
    if (kills % POTION_PER_KILLS === 0 && s.potions > 0) s.potions--;
    if (res.drop.item || res.drop.levelUp) gear();
    shop();
    if (res.drop.levelUp) {
      for (const m of MILESTONES) if (s.level >= m && st.ms[m] === undefined) { st.ms[m] = t / 3600; st.goldAt[m] = s.gold; }
      for (const [k, req] of BOOKS) if (s.level >= req && st.bookLv[k] === undefined) st.bookLv[k] = t;
    }
  }
  // 시간 밴드별 소요 시간
  let prev = 0; BANDS.forEach(([a, bnd], i) => { const m = st.ms[bnd]; const end = m ?? t / 3600; st.band[i].h = Math.max(0, end - prev); if (m !== undefined) prev = m; else prev = t / 3600; });
  if (!st.snap) st.snap = { level: s.level, gold: s.gold, stones: s.stones, enh: W().enh, tier: W().tier, skills: s.skills.length, kills };
  st.endH = t / 3600;
  st.final = { gold: s.gold, stones: s.stones, level: s.level, enh: W().enh, tier: W().tier, skills: s.skills.length, potions: s.potions };
  return st;
}

const med = a => { const x = a.filter(v => v !== undefined).sort((p, q) => p - q); return x.length ? x[Math.floor(x.length / 2)] : undefined; };
const f1 = v => v === undefined ? '-' : v.toFixed(1), n0 = v => v === undefined ? '-' : Math.round(v).toLocaleString('en');
let md = `# 경제/성장 장기 시뮬 결과 (econ_sim.js)\n\n생성: ${new Date().toISOString().slice(0, 10)} · items.js 를 직접 호출(I.kill/enhance/disassemble/sell/buy/learn/equip/map). 코드 수정 없음.\n\n`;
md += `## 가정/정책\n- 처치 속도: balance-playtest.csv 30분 처치 수 x2 = 처치/시간, 레벨 선형 보간(Lv<20 은 Lv20 값 유지). 프로필 P1=allocated_all_books(스탯 배분 완료, 기본), P2=unallocated_prior_books(미배분, 보수적). 무기별 곡선.\n- 몬스터 선택: 현재 존 티어 중 spawnAllowed 통과(minLv<=Lv+8)한 것을 MonsterSpawner 와 같은 가중치(GameData weight = 30.6-1.4*(t-1), 낮은 티어가 더 흔함)로 추첨.\n- 존: A -> Lv35 에 B -> Lv70 에 C. 보스 소환/보스 처치(t19~21) 제외(보스 장비 드랍·골드·경험치 없음 -> 장비는 일반 드랍만).\n- 장비: 자기 계열 무기, common/자기 클래스 방어구 중 레벨 최고를 착용(레벨 미달이면 보관). 나머지는 즉시 처분: 기본은 분해(D, 강화석 병목이라), 비교용으로 판매(S).\n- 포션: 15개 미만이면 20개까지 구매, 소모는 처치 200회당 1개 가정(CSV potions≈0).\n- 스킬북: 자기 무기 5권을 레벨+골드 충족 즉시 구매. 다음 책이 레벨 충족 상태면 강화에 그 책 값만큼 골드를 남겨 둠.\n- 강화: 착용 무기만 +10 까지(위험 +15 이상 안 감). 방어구는 강화 안 함(강화석 유휴 여부 확인용).\n- 최대 300h(Lv100 후 5h 더)까지 돌려 마일스톤 시각을 얻고, 60h 시점 스냅샷을 따로 기록. 3 시드(1,2,3) 중앙값. 스탯 포인트 배분은 처치 속도에 이미 반영(P1)된 것으로 보고 골드 비용 없음.\n\n`;

const sections = [];
for (const [pname, profile, policy] of [['P1 분해', 'allocated_all_books', 'disassemble'], ['P1 판매', 'allocated_all_books', 'sell'], ['P2 분해', 'unallocated_prior_books', 'disassemble']]) {
  for (const weapon of ['sword', 'staff']) {
    const runs = SEEDS.map(sd => run(weapon, sd, profile, policy));
    sections.push({ pname, weapon, runs });
  }
}
for (const { pname, weapon, runs } of sections) {
  md += `## ${pname} / ${weapon}\n\n| 지표 | ${MILESTONES.map(m => 'Lv' + m).join(' | ')} |\n|---|${MILESTONES.map(() => '---').join('|')}|\n`;
  md += `| 도달 시간(h) | ${MILESTONES.map(m => f1(med(runs.map(r => r.ms[m])))).join(' | ')} |\n`;
  md += `| 보유 골드 | ${MILESTONES.map(m => n0(med(runs.map(r => r.goldAt[m])))).join(' | ')} |\n\n`;
  md += `| 스킬북 | 요구Lv | 가격 | 레벨 충족(h) | 구매(h) | 격차(h) |\n|---|---|---|---|---|---|\n`;
  for (const [k, req] of BOOKS) {
    const lvH = med(runs.map(r => r.bookLv[k] === undefined ? undefined : r.bookLv[k] / 3600)), lH = med(runs.map(r => r.bookLearn[k] === undefined ? undefined : r.bookLearn[k] / 3600));
    md += `| ${k} | ${req} | ${n0(SKILL_PRICES[k + '_' + weapon])} | ${f1(lvH)} | ${f1(lH)} | ${lH === undefined || lvH === undefined ? '미구매' : f1(lH - lvH)} |\n`;
  }
  md += `\n| 레벨 구간 | 소요(h) | 처치 골드/h | 판매 골드/h | 분해 강화석/h(드랍 포함) |\n|---|---|---|---|---|\n`;
  BANDS.forEach(([a, b], i) => {
    const h = med(runs.map(r => r.band[i].h)); const per = (key) => h > 0 ? med(runs.map(r => r.band[i][key] / r.band[i].h)) : undefined;
    md += `| ${a}~${b} | ${f1(h)} | ${n0(per('kill'))} | ${n0(per('sell'))} | ${f1(per('stones'))} |\n`;
  });
  const sn = runs.map(r => r.snap), fin = runs.map(r => r.final);
  md += `\n**60h 시점**: Lv ${med(sn.map(x => x.level))}, 골드 ${n0(med(sn.map(x => x.gold)))}, 미사용 강화석 ${n0(med(sn.map(x => x.stones)))}, 무기 tier ${med(sn.map(x => x.tier))} +${med(sn.map(x => x.enh))}, 책 ${med(sn.map(x => x.skills))}/5권.\n\n종료(${f1(med(runs.map(r => r.endH)))}h): 골드 ${n0(med(fin.map(x => x.gold)))}, 강화석 ${n0(med(fin.map(x => x.stones)))}, 무기 tier ${med(fin.map(x => x.tier))} +${med(fin.map(x => x.enh))}, 강화 시도 ${med(runs.map(r => r.enhTries))}회. 누적 지출(중앙값): 포션 ${n0(med(runs.map(r => r.spend.potion)))}, 책 ${n0(med(runs.map(r => r.spend.book)))}, 강화 ${n0(med(runs.map(r => r.spend.enh)))}.\n\n`;
}
fs.writeFileSync(path.join(__dirname, 'econ_sim_result.md'), md);
console.log(md);
