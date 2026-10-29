const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const Quiz = require('./quiz-progress');
const PORT = 18386;
const child = spawn(process.execPath, [path.join(__dirname, 'server.js')], { env: { ...process.env, PORT: String(PORT), DATABASE_URL: '', REDIS_URL: '', PUBLIC_PATH: '/game', PUBLIC_HOSTS: 'quiz.test' }, stdio: ['ignore', 'pipe', 'inherit'] });
const base = `http://localhost:${PORT}/game`;
const state = () => ({ version: 'saa-c03-v3-first10', count: 1, current: 1, records: { 1: { correct: false, selected: ['A'], attempts: 1, due: 11, markedWrong: true } } });
async function request(route, cookie = '', body, origin) {
  return fetch(base + route, { method: body === undefined ? 'GET' : 'POST', headers: { cookie, 'content-type': 'application/json', ...(origin ? { origin } : {}) }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
}
async function login(id) {
  assert.equal((await request('/account/signup', '', { id, name: id, pw: 'test-pass-123' })).status, 200);
  const res = await request('/account/login', '', { id, pw: 'test-pass-123' });
  assert.equal(res.status, 200);
  return res.headers.getSetCookie().map(c => c.split(';')[0]).join('; ');
}
(async () => {
  await new Promise((resolve, reject) => { const timeout = setTimeout(() => reject(Error('server startup timeout')), 10000); child.stdout.on('data', b => { if (String(b).includes('대기 중')) { clearTimeout(timeout); resolve(); } }); });
  assert.equal((await request('/quiz/progress')).status, 401);
  const signup = { id: 'quizCode', name: 'quizCode', pw: 'test-pass-123' };
  assert.equal((await request('/quiz/signup', '', signup)).status, 403);
  assert.equal((await request('/quiz/signup', '', { ...signup, code: 'wrong' })).status, 403);
  assert.equal((await request('/account/login', '', { id: signup.id, pw: signup.pw })).status, 400, 'invalid code cannot create account');
  assert.equal((await request('/quiz/signup', '', { ...signup, code: 'ktcloud', pw: 'tiny' })).status, 400);
  assert.equal((await request('/quiz/signup', '', { ...signup, code: 'ktcloud' })).status, 200);
  assert.equal((await request('/account/login', '', { id: signup.id, pw: signup.pw })).status, 200);
  const a = await login('quizA'), b = await login('quizB');
  assert.equal((await request('/save/quizA', a, { potionThreshold: 40 })).status, 204);
  const gameBefore = await (await request('/save/quizA', a)).json();
  assert.deepEqual(await (await request('/quiz/progress', a)).json(), { accountId: 'quizA', revision: 0, state: null });
  let res = await request('/quiz/progress', a, { accountId: 'quizA', revision: 0, state: state() }, 'https://quiz.test');
  assert.equal(res.status, 200); assert.equal(res.headers.get('access-control-allow-origin'), 'https://quiz.test');
  assert.deepEqual(await res.json(), { revision: 1 });
  assert.deepEqual((await (await request('/quiz/progress', a)).json()).state, state());
  assert.equal((await (await request('/quiz/progress', b)).json()).state, null);
  assert.equal((await request('/quiz/progress', b, { accountId: 'quizA', revision: 0, state: state() })).status, 403);
  assert.equal((await request('/quiz/progress', a, { accountId: 'quizA', revision: 0, state: state() })).status, 409);
  assert.equal((await request('/quiz/progress', a, { accountId: 'quizA', revision: 1, state: { ...state(), current: 51 } })).status, 400);
  assert.equal((await request('/quiz/progress', a, { accountId: 'quizA', revision: 1, state: state() }, 'https://evil.test')).status, 403);
  const concurrent = await Promise.all([1, 2].map(() => request('/quiz/progress', a, { accountId: 'quizA', revision: 1, state: state() })));
  assert.deepEqual(concurrent.map(r => r.status).sort(), [200, 409]);
  assert.deepEqual(await (await request('/save/quizA', a)).json(), gameBefore);
  const large = state();
  for (let id = 1; id <= 954; id++) if (![51, 73, 95, 147, 382, 933].includes(id)) large.records[id] = { ...large.records[1] };
  assert.ok(JSON.stringify(large).length > 16384);
  assert.equal((await request('/quiz/progress', a, { accountId: 'quizA', revision: 2, state: large })).status, 200);
  const calls = [];
  const db = { query: async (sql, params) => { calls.push({ sql, params }); return { rowCount: 1, rows: [{ quiz_progress: { revision: 3, state: state() } }] }; } };
  assert.equal((await Quiz.load(db, 'quizA')).revision, 3);
  assert.equal((await Quiz.save(db, 'quizA', 3, state())).revision, 4);
  assert.match(calls[1].sql, /UPDATE accounts SET quiz_progress/);
  assert.match(calls[1].sql, /user_id = \$1 AND COALESCE/);
  assert.equal(calls[1].params[0], 'quizA'); assert.equal(calls[1].params[2], 3);
  assert.ok(!Quiz.validState({ ...state(), records: JSON.parse('{"__proto__":{}}') }));
  assert.equal((await request('/account/me', a)).status, 200);
  console.log('quiz auth / own-account isolation / atomic revision / large progress / CORS / unchanged game save PASS');
})().catch(e => { console.error(e); process.exitCode = 1; }).finally(() => child.kill('SIGTERM'));
