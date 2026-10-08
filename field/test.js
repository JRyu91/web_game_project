// 코어 검증. 프레임워크 없이 assert 만 쓴다.  실행: node field/test.js
const assert = require('assert');
const C = require('./core.js');

let n = 0;
const t = (name, fn) => { fn(); n++; console.log('  ok  ' + name); };

console.log('필드 코어 검증');

t('레벨업 요구치가 레벨당 약 2.55배씩 오른다', () => {
  const r = C.xpToLevel(2) / C.xpToLevel(1);
  assert.ok(Math.abs(r - Math.pow(2, 1.35)) < 0.1, '비율=' + r.toFixed(2));
});

t('요구치는 100 레벨까지 계속 증가한다', () => {
  for (let lv = 1; lv < C.LEVEL_MAX; lv++) assert.ok(C.xpToLevel(lv + 1) > C.xpToLevel(lv));
});

t('30 레벨에서 곡선이 꺾인다 (그 뒤로 급해진다)', () => {
  const before = C.xpToLevel(29) / C.xpToLevel(28);
  const after = C.xpToLevel(41) / C.xpToLevel(40);
  assert.ok(after > before * 1.05, `꺾이지 않았다 (이전 ${before.toFixed(3)} / 이후 ${after.toFixed(3)})`);
  // 30 이후 20 레벨 더 가는 비용이 30 까지의 총합보다 훨씬 커야 한다
  const to30 = Array.from({ length: 29 }, (_, i) => C.xpToLevel(i + 1)).reduce((a, b) => a + b);
  const to50 = Array.from({ length: 20 }, (_, i) => C.xpToLevel(i + 30)).reduce((a, b) => a + b);
  const to80 = Array.from({ length: 50 }, (_, i) => C.xpToLevel(i + 30)).reduce((a, b) => a + b);
  assert.ok(to50 > to30 * 5, `30→50 이 너무 싸다 (${to30} vs ${to50})`);
  assert.ok(to80 > to30 * 50, `30→80 이 너무 싸다 (${to30} vs ${to80})`);
});

t('몬스터 경험치는 요구치의 5분의 1 속도로 오른다', () => {
  // lv1 → lv2 에서 요구치는 2.09배, 몬스터는 1.16배 (10 → 12)
  assert.strictEqual(C.monsterExp('slime', 1), 10);
  assert.strictEqual(C.monsterExp('slime', 2), 12);
  // 벽이 서기 전(30 이전) 구간에서 확인한다. 벽은 일부러 몬스터 경험치에 반영하지 않는다.
  const rReq = Math.log(C.xpToLevel(25) / C.xpToLevel(1));
  const rMob = Math.log(C.monsterExp('slime', 25) / C.monsterExp('slime', 1));
  assert.ok(Math.abs(rReq / rMob - 5) < 0.15, '비율=' + (rReq / rMob).toFixed(2));
});

t('레어는 10 레벨, 유니크는 20 레벨부터 등장한다', () => {
  const at = lv => C.spawnTable(lv);
  assert.ok(!at(9).includes('unicorn'));
  assert.ok(at(10).includes('unicorn') && at(10).includes('sasquatch') && at(10).includes('zombie'));
  assert.ok(!at(19).includes('veteran'));
  assert.ok(at(20).includes('veteran'));
});

t('소환 보스는 자연 스폰에 절대 안 나온다', () => {
  for (let lv = 1; lv <= C.LEVEL_MAX; lv++) {
    assert.ok(!C.spawnTable(lv).includes('senator'));
    assert.ok(!C.spawnTable(lv).includes('pooh'));
  }
});

t('소환 조건은 100 / 300 처치다', () => {
  assert.strictEqual(C.SUMMON_KILLS.senator, 100);
  assert.strictEqual(C.SUMMON_KILLS.pooh, 300);
});

t('몬스터 접촉 피해가 1번 → 10번으로 갈수록 세진다', () => {
  const order = ['slime', 'rat', 'boar', 'bear', 'unicorn', 'sasquatch', 'zombie', 'veteran', 'senator', 'pooh'];
  for (let i = 1; i < order.length; i++) {
    assert.ok(C.MONSTERS[order[i]].atk > C.MONSTERS[order[i - 1]].atk,
      order[i] + ' 이 ' + order[i - 1] + ' 보다 약하다');
  }
});

t('장비는 세 종류이고 등급이 오를수록 세진다', () => {
  assert.deepStrictEqual(C.SLOT_KEYS, ['weapon', 'helmet', 'armor']);
  for (const s of C.SLOT_KEYS) {
    for (let g = 2; g <= C.GRADE_MAX; g++) {
      assert.ok(C.itemPower(s, g) > C.itemPower(s, g - 1), s + ' ' + g + '등급');
    }
  }
  assert.ok(C.itemPower('weapon', 10) / C.itemPower('weapon', 1) > 40);
});

t('무기는 공격력, 투구·갑옷은 방어력에 붙는다', () => {
  const base = C.playerStats(10, {});
  const w = C.playerStats(10, { weapon: { grade: 5 } });
  const h = C.playerStats(10, { helmet: { grade: 5 } });
  assert.ok(w.atk > base.atk && w.def === base.def);
  assert.ok(h.def > base.def && h.atk === base.atk);
});

t('방어력은 비율 경감이라 무적이 되지 않는다', () => {
  assert.ok(C.mitigate(100, 0) === 100);
  assert.ok(C.mitigate(100, 100) === 50);
  assert.ok(C.mitigate(100, 100000) >= 1, '방어력이 아무리 높아도 1 은 들어간다');
});

t('기여도 합은 1 이고, 한 대라도 때리면 보상이 있다', () => {
  const sh = C.contribution({ a: 300, b: 100 });
  assert.ok(Math.abs(sh.a + sh.b - 1) < 1e-9);
  assert.strictEqual(sh.a, 0.75);
  const cut = C.splitReward(1000, C.contribution({ a: 999, b: 1 }));
  assert.ok(cut.b >= 50, '막타 독식 방지 최소 5% (' + cut.b + ')');
});

t('경험치가 넘치면 여러 레벨이 한 번에 오르고 나머지가 이월된다', () => {
  const r = C.gainExp(1, 0, C.xpToLevel(1) + C.xpToLevel(2) + 7);
  assert.strictEqual(r.level, 3);
  assert.strictEqual(r.gained, 2);
  assert.strictEqual(r.exp, 7);
});

t('100 레벨에서 멈춘다', () => {
  const r = C.gainExp(C.LEVEL_MAX, 0, 9e9);
  assert.strictEqual(r.level, C.LEVEL_MAX);
  assert.strictEqual(r.gained, 0);
});

t('드롭은 등급 범위를 벗어나지 않는다', () => {
  for (let i = 0; i < 4000; i++) {
    const it = C.rollItem('pooh', 100, Math.random, i);
    if (!it) continue;
    assert.ok(it.grade >= 1 && it.grade <= C.GRADE_MAX);
    assert.ok(C.SLOT_KEYS.includes(it.slot));
    assert.strictEqual(it.power, C.itemPower(it.slot, it.grade));
  }
});

t('채널 정원은 10명이다', () => assert.strictEqual(C.CHANNEL_CAP, 10));

t('스킬은 20 레벨마다 하나씩, 쿨타임이 스펙과 맞는다', () => {
  assert.deepStrictEqual(C.SKILLS.map(s => s.lv), [20, 40, 60, 80, 100]);
  assert.deepStrictEqual(C.SKILLS.map(s => s.cd), [5, 10, 15, 20, 60]);
  assert.strictEqual(C.skillsAt(19).length, 0);
  assert.strictEqual(C.skillsAt(20).length, 1);
  assert.strictEqual(C.skillsAt(100).length, 5);
});

t('80·100 레벨 스킬은 맵 전체 공격이다', () => {
  const map = C.SKILLS.filter(s => s.kind === 'map').map(s => s.lv);
  assert.deepStrictEqual(map, [80, 100]);
  for (const s of C.SKILLS) {
    assert.strictEqual(C.skillRange(s, 100) === Infinity, s.kind === 'map');
  }
});

t('스킬 사거리는 레벨이 오를수록 넓어진다', () => {
  for (const s of C.SKILLS.filter(x => x.kind !== 'map')) {
    assert.ok(C.skillRange(s, s.lv + 20) > C.skillRange(s, s.lv), s.name);
  }
});

t('스킬 위력은 뒤로 갈수록 세진다', () => {
  const d = C.SKILLS.map(s => C.skillDamage(s, 100));
  for (let i = 1; i < d.length; i++) assert.ok(d[i] > d[i - 1], C.SKILLS[i].name);
});

t('채널 이관 페이로드에 좌표와 현재 체력이 들어간다', () => {
  // 이게 빠지면 채널을 옮길 때 화면이 튄다 — 프로젝트 명제가 걸린 부분이다.
  const src = { id: 'u1', name: 'A', level: 30, exp: 5, x: 800.6, face: -1,
                hp: 210.4, maxHp: 500, equip: { weapon: { grade: 5 } }, cds: { qi: 3 } };
  const t = C.transferState(src);
  for (const k of ['x', 'hp', 'maxHp', 'level', 'exp', 'equip', 'cds', 'id']) {
    assert.ok(t[k] !== undefined, k + ' 이 빠졌다');
  }
  const dst = {};
  assert.strictEqual(C.applyTransfer(dst, t), true);
  assert.strictEqual(dst.x, 801);
  assert.strictEqual(dst.hp, 210);
  assert.deepStrictEqual(dst.cds, { qi: 3 });
});

t('스킬 쿨타임도 함께 넘어간다 (이관으로 쿨 초기화 악용 차단)', () => {
  const t = C.transferState({ id: 'u1', name: 'A', level: 100, exp: 0, x: 0, face: 1,
                              hp: 1, maxHp: 1, equip: {}, cds: { meteor: 58 } });
  assert.strictEqual(t.cds.meteor, 58);
});

t('버전이 다른 이관 페이로드는 거부한다', () => {
  assert.strictEqual(C.applyTransfer({}, { v: 999 }), false);
  assert.strictEqual(C.applyTransfer({}, null), false);
});

t('채널 지표는 인원과 정원 도달 여부를 센다', () => {
  const m = C.channelMetrics([{}, {}, {}]);
  assert.strictEqual(m.active_players, 3);
  assert.strictEqual(C.channelMetrics(new Array(C.CHANNEL_CAP).fill({})).full, true);
  assert.strictEqual(C.channelMetrics([{}]).full, false);
});

t('식별자는 대량으로 만들어도 겹치지 않는다', () => {
  const ids = new Set();
  for (let i = 0; i < 20000; i++) ids.add(C.newId('it'));
  assert.strictEqual(ids.size, 20000);
});

t('기여도는 서로 다른 플레이어 id 로 갈린다', () => {
  // 자기 자신을 늘 'me' 로 두면 서버에서 여러 사람이 한 사람으로 합쳐진다.
  const sh = C.contribution({ u_a: 100, u_b: 300 });
  assert.strictEqual(Object.keys(sh).length, 2);
  assert.strictEqual(sh.u_b, 0.75);
});

console.log(`\n${n}개 통과`);

/* 참고용 진행 시뮬레이션 — 실패 조건은 아니고 곡선을 눈으로 보려는 것 */
console.log('\n레벨업에 필요한 마릿수 (레벨별)');
for (const lv of [1, 10, 20, 29, 30, 40, 60, 80, 99]) {
  const kills = Math.ceil(C.xpToLevel(lv) / C.monsterExp('slime', lv));
  const best = Math.ceil(C.xpToLevel(lv) / C.monsterExp(lv >= 20 ? 'veteran' : lv >= 10 ? 'sasquatch' : 'bear', lv));
  console.log(`  lv${String(lv).padStart(3)}  요구 ${String(C.xpToLevel(lv)).padStart(8)}  슬라임 ${String(kills).padStart(6)}마리  상위몹 ${String(best).padStart(5)}마리`);
}
