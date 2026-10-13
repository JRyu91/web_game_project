// node field/test_google.js — Google 로그인 흐름을 가짜 토큰 서버로 확인한다(DB·Redis 없이 로컬 모드)
const assert = require('node:assert/strict');
const http = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');
const PORT = 18380, TOKEN_PORT = 18381, CLIENT = 'test-client.apps.googleusercontent.com';
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
// code 가 곧 시나리오: good=정상, badaud=다른 앱용 토큰
const tokenServer = http.createServer((req, res) => {
  let body = ''; req.on('data', c => body += c); req.on('end', () => {
    const code = new URLSearchParams(body).get('code');
    const aud = code === 'badaud' ? 'other-app' : CLIENT;
    res.end(JSON.stringify({ id_token: `x.${b64({ iss: 'https://accounts.google.com', aud, sub: '1234567890', exp: Date.now() / 1000 + 300 })}.sig` }));
  });
}).listen(TOKEN_PORT);
const child = spawn(process.execPath, [path.join(__dirname, 'server.js')], { env: { ...process.env, PORT: String(PORT), DATABASE_URL: '', REDIS_URL: '',
  GOOGLE_CLIENT_ID: CLIENT, GOOGLE_CLIENT_SECRET: 's', OAUTH_REDIRECT_URI: `http://localhost:${PORT}/auth/google/callback`,
  GOOGLE_TOKEN_URL: `http://localhost:${TOKEN_PORT}/token` }, stdio: ['ignore', 'pipe', 'inherit'] });
const get = (p, cookie = '') => fetch(`http://localhost:${PORT}${p}`, { redirect: 'manual', headers: { cookie } });
const cookies = res => res.headers.getSetCookie().map(c => c.split(';')[0]).filter(c => !c.endsWith('=')).join('; ');
async function start() { // 구글 로그인 버튼 → state 쿠키
  const res = await get('/auth/google');
  assert.equal(res.status, 302);
  const to = new URL(res.headers.get('location'));
  assert.equal(to.host, 'accounts.google.com'); assert.equal(to.searchParams.get('scope'), 'openid');
  return { state: to.searchParams.get('state'), cookie: cookies(res) };
}
(async () => {
  await new Promise(r => child.stdout.on('data', b => String(b).includes('대기 중') && r()));
  let s = await start();
  let res = await get(`/auth/google/callback?code=good&state=wrong`, s.cookie);
  assert.equal(res.headers.get('location'), '/#google-error', 'state 위조는 거절');
  s = await start();
  res = await get(`/auth/google/callback?code=badaud&state=${s.state}`, s.cookie);
  assert.equal(res.headers.get('location'), '/#google-error', '다른 앱용 토큰은 거절');
  s = await start();
  res = await get(`/auth/google/callback?code=good&state=${s.state}`, s.cookie);
  assert.equal(res.headers.get('location'), '/#google-signup', '처음 온 사람은 캐릭터 생성으로');
  const pending = cookies(res);
  assert.match(pending, /google_pending=/); assert.doesNotMatch(pending, /game_session=/);
  res = await fetch(`http://localhost:${PORT}/account/google/finish`, { method: 'POST', headers: { cookie: pending, 'content-type': 'application/json' }, body: JSON.stringify({ name: '구글용사', gender: 'female' }) });
  assert.equal(res.status, 200);
  const me = await (await get('/account/me', cookies(res))).json();
  assert.deepEqual(me.ok, { id: 'g_1234567890', name: '구글용사', gender: 'female', role: 'user' });
  res = await fetch(`http://localhost:${PORT}/account/google/finish`, { method: 'POST', headers: { cookie: pending, 'content-type': 'application/json' }, body: '{"name":"또만듦"}' });
  assert.equal(res.status, 400, '임시 표는 한 번만 쓴다');
  s = await start();
  res = await get(`/auth/google/callback?code=good&state=${s.state}`, s.cookie);
  assert.equal(res.headers.get('location'), '/', '두 번째부터는 바로 로그인');
  assert.match(cookies(res), /game_session=/);
  res = await fetch(`http://localhost:${PORT}/account/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{"id":"g_1234567890","pw":""}' });
  assert.equal(res.status, 400, '구글 계정은 비번 로그인 불가');
  res = await fetch(`http://localhost:${PORT}/account/signup`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{"name":"사칭","id":"g_999","pw":"abcd"}' });
  assert.equal(res.status, 400, 'g_ 아이디 선점 불가');
  console.log('google login ok');
})().catch(e => { console.error(e); process.exitCode = 1; }).finally(() => { child.kill(); tokenServer.close(); });
