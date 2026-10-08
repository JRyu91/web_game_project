//F안 서버 API 검증. 실행: node unity_review/stage3/team_v6/f_round/server/test_api.js (Postgres 불필요, 인메모리 폴백으로 서버를 직접 띄운다)
const assert = require('assert');
const path = require('path');
const { spawn } = require('child_process');
const FIELD = path.resolve(__dirname, '../../../../../field');
const I = require(path.join(FIELD, 'items.js'));
const WebSocket = require(path.join(FIELD, 'node_modules/ws'));

const fresh = (extra = {}) => I.normalize({ name: 't', ...extra });

//시드 고정 RNG(mulberry32). ±1%p 는 N=10000 에서 약 2σ라 9단계를 Math.random 으로 돌리면 30% 확률로 우연히 깨진다.
let seed = 20261007;
const rng = () => { seed = (seed + 0x6D2B79F5) | 0; let t = Math.imul(seed ^ (seed >>> 15), 1 | seed); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };

// Saved inventory must round-trip; malformed data must fail before any resource mutation.
assert.deepStrictEqual(I.normalize(JSON.parse(JSON.stringify(fresh()))), fresh());
assert.throws(() => I.normalize({ inv: [null] }), /invalid saved inventory/);

/* 1. 강화 확률: 단계별 N=10000, 설정값 ±1%p. 파괴율(+15 이상 실패 중 30%)도 같이. */
for (const from of [0, 4, 5, 11, 15, 16, 20, 21, 24]) {
  const N = 10000; const n = { success: 0, keep: 0, down: 0, destroy: 0 };
  for (let i = 0; i < N; i++) {
    const s = fresh({ weaponTier: 0 }); s.gold = 1e12; s.stones = 1e9;
    s.inv[0].enh = from;
    n[I.enhance(s, s.inv[0].uid, rng).enh.result]++;
  }
  const p = I.ENH_SUCC[from + 1], got = n.success / N * 100;
  assert(Math.abs(got - p) <= 1, `+${from}→+${from + 1} 성공 ${got.toFixed(2)}% vs ${p}%`);
  const fails = N - n.success;
  if (from >= I.DESTROY_FROM && fails > 1000) {
    const d = n.destroy / fails * 100;
    assert(Math.abs(d - 30) <= 3, `+${from} 파괴 ${d.toFixed(2)}% (실패 ${fails}건 중, 허용 ±3%p)`);
  }
  if (from < 4) assert.strictEqual(n.down + n.destroy, 0);
  if (from >= 4 && from < I.DESTROY_FROM) assert.strictEqual(n.destroy, 0);
  console.log(`enhance +${from}->+${from + 1}: 성공 ${got.toFixed(2)}% (설정 ${p}%) ${JSON.stringify(n)}`);
}

/* 2. 강화 비용·자원 부족·파괴 후 기본 무기 */
{
  const s = fresh({ weaponKind: 'staff', weaponTier: 10 }); //L50
  assert.deepStrictEqual(I.enhCost(s.inv[0]), { stones: 22, gold: 4400 }); //ceil(2*(50/5+1)), 40*55*2
  assert.strictEqual(I.enhance(s, s.inv[0].uid).code, 'no_stones');
  s.stones = 1e9; assert.strictEqual(I.enhance(s, s.inv[0].uid).code, 'no_gold');
  s.gold = 1e12; s.inv[0].enh = 20;
  let r; do { s.inv.find(i => i.uid === s.equip.weapon).enh = 20; r = I.enhance(s, s.equip.weapon, rng); } while (r.enh.result !== 'destroy');
  const w = s.inv.find(i => i.uid === s.equip.weapon);
  assert(w && w.tier === 0 && w.kind === 'staff' && w.enh === 0, '파괴 후 같은 계열 기본 무기');
  assert.strictEqual(s.weaponTier, 0);
}

/* 3. 처치 보상: 장비 드랍률(basic 10%)·강화석(1.95%)·레벨 제한·보스 100% */
{
  const N = 20000; let items = 0, stones = 0;
  for (let i = 0; i < N; i++) { const s = fresh(); const r = I.kill(s, 1); if (r.drop.item) items++; if (r.drop.stones) stones++; }
  assert(Math.abs(items / N - 0.10) < 0.01, `basic 장비 ${items / N}`);
  assert(Math.abs(stones / N - 0.0195) < 0.004, `basic 강화석 ${stones / N}`);
  assert.strictEqual(I.kill(fresh({zone:'C'}), 13).code, 'too_strong'); //Lv1 이 t13(minLv57)
  assert.strictEqual(I.kill(fresh(), 99).code, 'bad_tier');
  const b = fresh({level:100,zone:'C'}); const r = I.kill(b, 20);
  assert(r.drop.item && [90, 95, 100].includes(r.drop.item.level) && r.drop.gold === 36000 && b.level > 1);
  assert.strictEqual(I.monExp(1), 3); assert.strictEqual(I.monExp(18), Math.round(2450 * 0.69 / 0.77));
}

/* 3b. 소환 카운트: B+C 일반 → T19, C만 → T20 */
{
  const s = fresh({level:100,zone:'C'}); I.autoSell(s,true,100);
  for (let i = 0; i < 500; i++) I.kill(s, 13);
  I.map(s,'B');I.kill(s, 7);I.map(s,'A');I.kill(s, 1);I.map(s,'C');
  assert(s.killCountT19 === 501 && s.killCountT20 === 500);
  assert.strictEqual(I.summon(s, 20).code, 'not_enough');
  assert(I.summon(s,19).ok && s.killCountT19===501);
  assert(I.summonAck(s,19,true,s.pendingSummon.token).ok && s.killCountT19===0 && I.view(s).killCountT20===500);
}

/* 4. 상점·장착·판매 */
{
  const s = fresh(); s.gold = 1000; const p = I.potionPrice(1); //round(4*6^1.5)=59
  assert.strictEqual(p, 59);
  assert(I.buy(s, 'potion', 2).ok && s.potions === 5 && s.gold === 1000 - 118);
  assert.strictEqual(I.buy(s, 'stone', 1).code, 'bad_item');
  assert(I.usePotion(s).ok && s.potionCount === 4);
  s.level = 30; s.inv.push({ uid: s.nextUid++, slot: 'helmet', kind: '', tier: 6, enh: 0 }); //L30 뿔
  const h = s.inv.at(-1).uid;
  assert(I.equip(s, 'helmet', h).ok && s.helmetTier === 6);
  assert.strictEqual(I.sell(s, h).code, 'equipped');
  assert.strictEqual(I.equip(s, 'weapon', h).code, 'bad_slot');
  assert(I.equip(s, 'helmet', 0).ok && s.helmetTier === -1);
  const g = s.gold; assert(I.sell(s, h).ok && s.gold === g + I.sellPrice({ slot: 'helmet', tier: 6, enh: 0 }));
  assert.strictEqual(I.equip(s, 'weapon', 0).code, 'bad_slot');
}

/* 4b. 일괄 판매: 혼합 배열(정상·장착·없는 uid·중복), 빈 배열, 전부 건너뜀 */
{
  const s = fresh(); s.level = 30;
  for (let t = 1; t <= 3; t++) s.inv.push({ uid: s.nextUid++, slot: 'weapon', kind: 'sword', tier: t, enh: 0 });
  const [w, a, b, c] = s.inv.map(i => i.uid), g = s.gold;
  const r = I.sell(s, 0, [a, b, b, w, 999, '' + c]);
  assert(r.ok && r.sell.sold.join() === [a, b, c].join(), JSON.stringify(r));
  assert.deepStrictEqual(r.sell.skipped, [{ uid: w, code: 'equipped' }, { uid: 999, code: 'no_item' }]);
  const exp = [1, 2, 3].reduce((x, t) => x + I.sellPrice({ slot: 'weapon', tier: t, enh: 0 }), 0);
  assert(r.sell.gold === exp && s.gold === g + exp && s.inv.length === 1);
  assert.strictEqual(I.sell(s, 0, []).code, 'empty');
  const n = I.sell(s, 0, [w]); assert(!n.ok && n.code === 'none_sold' && n.sell.skipped.length === 1);
  assert(I.sell(s, w).code === 'equipped'); //단일 uid 호환
}

/* 5. 실서버: join → inv, kill, 속도 제한, 재접속 복원, POST /save 로 골드 위조 차단 */
const PORT = 18000 + Math.floor(Math.random() * 1000);
const srv = spawn(process.execPath, [path.join(FIELD, 'server.js')], { env: { ...process.env, PORT, DATABASE_URL: '', REDIS_URL: '' }, stdio: ['ignore', 'pipe', 'inherit'] });
const deadline = setTimeout(() => { srv.kill(); console.error('API test timed out after 20s'); process.exit(1); }, 20000);

function client(port = PORT, cookie) {
  const ws = new WebSocket(`ws://localhost:${port}`, { headers: cookie ? { Cookie: cookie } : {} }); const q = []; let wake = null;
  ws.on('message', m => { const j = JSON.parse(m); if (['inv','spawn'].includes(j.type)) { q.push(j); wake && wake(); } });
  const next = () => new Promise(r => { if (q.length) return r(q.shift()); wake = () => { wake = null; r(q.shift()); }; });
  return new Promise((r, reject) => { ws.once('error', reject); ws.once('open', () => r({ ws, next, send: o => ws.send(JSON.stringify(o)) })); });
}
let monsterId=0;
async function spawnReceipt(connection) {
  connection.send({type:'spawn',tier:1,monsterId:++monsterId});
  const reply=await connection.next();assert(reply.ok&&reply.type==='spawn'&&reply.receipt);
  return reply.receipt;
}

//실제 SQL 연결 없이 pg.query만 대체해 실서버 putSave(false)·응답·rollback 경로를 검증한다.
async function storageFailure() {
  const port = 19000 + Math.floor(Math.random() * 1000);
  const mock = `const { Pool } = require(${JSON.stringify(path.join(FIELD, 'node_modules/pg'))});
    const crypto = require('crypto');
    const pwHash = 'mock:' + crypto.pbkdf2Sync('regression_pw', 'mock', 100000, 32, 'sha256').toString('hex');
    Pool.prototype.query = async sql => {
      if (sql.startsWith('SELECT user_id')) return {rows:[{user_id:'failed_save_tester',name:'검증',gender:'male',role:'user',pw_hash:pwHash}],rowCount:1};
      if (sql.trim().startsWith('INSERT INTO saves')) throw Object.assign(new Error('injected save FK failure'), { code: '23503' });
      return { rows: [], rowCount: 0 };
    };
    require(${JSON.stringify(path.join(FIELD, 'server.js'))});`;
  const child = spawn(process.execPath, ['-e', mock], { env: { ...process.env, PORT: port, DATABASE_URL: 'postgres://mock.invalid/mock', REDIS_URL: '' }, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stderr.on('data', () => {}); // 예상한 DB 실패 로그는 응답 assertion으로 검증한다.
  const timeout = setTimeout(() => { child.kill(); console.error('Injected storage failure test timed out'); process.exit(1); }, 10000);
  let connection;
  try {
    await new Promise((resolve, reject) => {
      child.stdout.on('data', data => String(data).includes('대기 중') && resolve());
      child.once('error', reject);
      child.once('exit', code => reject(new Error(`Mock server exited early: ${code}`)));
    });
    const login = await fetch(`http://localhost:${port}/account/login`, {method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({id:'failed_save_tester',pw:'regression_pw'})});
    assert.strictEqual(login.status, 200);
    const cookie = login.headers.get('set-cookie').split(';')[0];
    assert.strictEqual((await fetch(`http://localhost:${port}/save/failed_save_tester`)).status, 401, 'SQL mode rejects anonymous saves');
    assert.strictEqual((await fetch(`http://localhost:${port}/save/another_account`, {headers:{Cookie:cookie}})).status, 401, 'SQL mode rejects another account');
    assert.strictEqual((await fetch(`http://localhost:${port}/server.js`)).status, 404, 'server source is private');
    assert.strictEqual((await fetch(`http://localhost:${port}/account/me`, {headers:{Cookie:cookie,Origin:'https://evil.invalid'}})).status, 403, 'cross-origin requests rejected');
    connection = await client(port, cookie);
    connection.send({ type: 'join', id: 'failed_save_tester', name: '검증', level: 1 });
    const before = await connection.next();
    assert(before.ok && before.req === 'join');
    const receipt=await spawnReceipt(connection);await new Promise(r=>setTimeout(r,310));
    connection.send({ type: 'kill', tier: 1, receipt, seq: 501 });
    const failed = await connection.next();
    assert(!failed.ok && failed.req === 'kill' && failed.code === 'save_failed' && failed.seq === 501);
    assert.deepStrictEqual(failed.state, before.state, '저장 실패 후 처치 경험치·골드·드롭 전부 rollback');
    for(let n=0;n<15;n++) {
      connection.send({type:'kill',tier:1,receipt,seq:600+n});
      const retry=await connection.next();
      assert.equal(retry.code,'save_failed','failed save must preserve receipt and rate budget');
      assert.deepStrictEqual(retry.state,before.state);
    }
    connection.send({ type: 'potion', seq: 502 });
    const potion = await connection.next();
    assert(!potion.ok && potion.code === 'save_failed');
    assert.deepStrictEqual(potion.state, before.state, '저장 실패 후 포션 미소비');
    const post = await fetch(`http://localhost:${port}/save/failed_save_tester`, { method: 'POST', headers: { Cookie: cookie }, body: JSON.stringify({ potionThreshold: 50 }) });
    assert.strictEqual(post.status, 503, '설정 저장 실패 응답');
    console.log('injected storage failure: kill/potion rollback + POST 503 PASS (mock pg.query, no SQL connection)');
  } finally {
    connection?.ws.close(); child.kill(); clearTimeout(timeout);
  }
}

(async () => {
  await new Promise(r => srv.stdout.on('data', d => String(d).includes('대기 중') && r()));
  const a = await client();
  // 파싱 가능한 JSON도 객체 요청이 아닐 수 있다. 뒤의 join 응답으로 서버·연결 생존까지 확인한다.
  for (const raw of ['null', '[]', '1', '"join"', '{}', '{"type":null}', '{']) a.ws.send(raw);
  a.send({ type: 'join', id: 'tester', name: '테스터', level: 1 });
  let m = await a.next();
  assert(m.ok && m.req === 'join' && m.state.inv.length === 1 && m.state.potions === 3);
  let ok = 0, rate = 0;
  const receipts=[];for(let i=0;i<14;i++)receipts.push(await spawnReceipt(a));
  await new Promise(r=>setTimeout(r,310));
  for (let i = 0; i < 20; i++) { a.send({ type: 'kill', tier: 1, receipt:receipts[i%14], seq: i }); m = await a.next(); assert.strictEqual(m.seq, i); if(m.ok)ok++;else {rate++;assert.equal(m.code,'bad_receipt');} }
  assert(ok===14&&rate===6, `중복 영수증 ${ok}/${rate}`);
  const gold = m.state.gold, exp = m.state.exp, n = m.state.inv.length;
  assert(gold > 0);
  a.send({ type: 'enhance', uid: m.state.equip.weapon, seq: 99 }); m = await a.next();
  assert(!m.ok && ['no_stones', 'no_gold'].includes(m.code)); //처치 중 강화석이 떨어지면 골드 부족으로 막힌다
  await new Promise(r => setTimeout(r, 200)); //쓰기 대기
  a.ws.close();

  const res = await fetch(`http://localhost:${PORT}/save/tester`, { method: 'POST', body: JSON.stringify({ name: '테스터', gold: 999999999, potionThreshold: 50 }) });
  assert.strictEqual(res.status, 204);
  const saved = (await (await fetch(`http://localhost:${PORT}/save/tester`)).json()).data;
  assert.strictEqual(saved.gold, gold, 'POST /save 골드 위조 차단'); assert.strictEqual(saved.potionThreshold, 50);
  const badPreference = await fetch(`http://localhost:${PORT}/save/tester`, { method: 'POST', body: JSON.stringify({ potionThreshold: 55 }) });
  assert.strictEqual(badPreference.status, 400, '목록에 없는 포션 설정 거절');
  const first = await fetch(`http://localhost:${PORT}/save/fresh_tester`, { method: 'POST', body: JSON.stringify({ level: 100, gold: 999999999, inv: [null], equip: null, potionThreshold: 60 }) });
  assert.strictEqual(first.status, 204);
  const firstSaved = (await (await fetch(`http://localhost:${PORT}/save/fresh_tester`)).json()).data;
  assert(firstSaved.level === 1 && firstSaved.gold === 0 && firstSaved.inv.length === 1 && firstSaved.inv[0].tier === 0 && firstSaved.potionThreshold === 60, '최초 저장도 서버 기본 상태 + 설정만 허용');

  const b = await client();
  b.send({ type: 'join', id: 'tester', name: '테스터', level: 1 });
  m = await b.next();
  assert(m.state.gold === gold && m.state.exp === exp && m.state.inv.length === n, '재접속 복원');
  const invalid = await fetch(`http://localhost:${PORT}/save/tester`, { method: 'POST', body: '[null]' });
  assert.strictEqual(invalid.status, 400, '배열 저장 요청 거절');
  //HTTP 설정 변경과 WS 보상을 동시에 처리해도 서버 소유 상태를 잃지 않아야 한다.
  for (let i = 0; i < 3; i++) {
    const receipt=await spawnReceipt(b);await new Promise(r => setTimeout(r, 350));
    const post = fetch(`http://localhost:${PORT}/save/tester`, { method: 'POST', body: JSON.stringify({ potionThreshold: 50, level: 100, gold: 999999999, inv: [null], equip: null }) });
    b.send({ type: 'kill', tier: 1, receipt, seq: 200 + i });
    const [response, update] = await Promise.all([post, b.next()]);
    assert.strictEqual(response.status, 204);
    assert(update.ok && update.req === 'kill' && update.seq === 200 + i, '설정 저장 중 처치 성공');
    m = update;
  }
  const concurrent = (await (await fetch(`http://localhost:${PORT}/save/tester`)).json()).data;
  for (const key of ['level', 'exp', 'gold', 'stones', 'potions', 'killCountT19', 'killCountT20'])
    assert.strictEqual(concurrent[key] ?? 0, m.state[key], `동시 저장 후 ${key} 보존`);
  assert.deepStrictEqual(concurrent.equip, m.state.equip, '동시 저장 후 장착 보존');
  assert.deepStrictEqual(concurrent.inv.map(i => i.uid), m.state.inv.map(i => i.uid), '동시 저장 후 인벤 보존');
  assert.strictEqual(concurrent.potionThreshold, 50, '동시 저장 후 설정 보존');
  //같은 계정의 새 연결로 교체해도 최신 서버 상태를 복원한다.
  const c = await client();
  c.send({ type: 'join', id: 'tester', name: '테스터', level: 1 });
  const replaced = await c.next();
  assert(replaced.ok && replaced.state.gold === m.state.gold && replaced.state.exp === m.state.exp, '중복 접속 교체 후 최신 상태');
  c.ws.close(); b.ws.close(); srv.kill(); clearTimeout(deadline);
  console.log(`live: kill ok ${ok} / rate ${rate}, 재접속 gold ${gold} exp ${exp} inv ${n} 복원, 위조 차단 OK`);
  await storageFailure();
  console.log('ALL PASS');
})().catch(e => { srv.kill(); clearTimeout(deadline); console.error(e); process.exit(1); });
