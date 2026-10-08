//채널 파드 서버. 접속자 상태를 메모리에 들고 있어서 이 파드는 무상태가 아니다.
//그래서 함부로 죽이면 안 되고, 죽일 땐 접속자를 다른 채널로 넘겨야 한다. 그게 이 파일의 알맹이다.

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { WebSocketServer } = require('ws');
const { createClient } = require('redis');
const { Pool } = require('pg');
const C = require('./core.js');
const I = require('./items.js');

const PORT = Number(process.env.PORT) || 8080;
const CHANNEL_ID = process.env.CHANNEL_ID || 'channel-0';
const REDIS_URL = process.env.REDIS_URL || '';
const DATABASE_URL = process.env.DATABASE_URL || ''; //Cloud SQL. 없으면 세이브 저장이 꺼진다(로컬 테스트용)
const DRAIN_TIMEOUT_SEC = Number(process.env.DRAIN_TIMEOUT_SEC) || 600;

const HEARTBEAT_SEC = 5;      //명부에 "나 살아있다" 도장 찍는 주기
const CHANNEL_TTL_SEC = 15;   //도장이 이만큼 안 찍히면 명부에서 저절로 사라진다. 파드가 죽으면 이게 청소부다
const TRANSFER_TTL_SEC = 30;  //사물함에 맡긴 짐을 아무도 안 찾아가면 버리는 시간

//파드 이름 끝의 숫자가 곧 채널 번호다. channel-0 이면 0.
const INDEX = Number((CHANNEL_ID.match(/(\d+)$/) || [, 0])[1]);

//밖에서 이 채널이 어떤 주소로 보이는지. 파드는 자기 외부 주소를 모르니 환경변수로 받는다.
//쉼표로 나눠서 채널 번호로 골라 쓴다. 예: "/ch0,/ch1" 또는 "http://localhost:8080,http://localhost:8081"
const MY_BASE = (process.env.CHANNEL_BASES || '').split(',')[INDEX] || '';

/* ---------- 접속자 ---------- */
const players = new Map(); //ws -> { id, name, level, snapshot }
const saveQueues = new Map();

//ponytail: 계정별 로컬 큐. 다른 파드의 동시 접속은 Redis 계정 소유권으로 막아야 한다.
function serialSave(id, operation) {
  const task = (saveQueues.get(id) || Promise.resolve()).catch(() => {}).then(operation);
  saveQueues.set(id, task);
  const clear = () => { if (saveQueues.get(id) === task) saveQueues.delete(id); };
  task.then(clear, clear);
  return task;
}

//채널 포화·KEDA 스케일을 브라우저 탭 여러 개 안 열고 테스트하려고 두는 가짜 접속자.
//관리자 메뉴 버튼으로 늘리고 줄인다. 메모리에만 있고 이관 대상이 아니다(드레인되면 그냥 사라짐).
const bots = new Map(); //botId -> { id, name, level }

let draining = false;

//실접속자 + 봇. 정원·명부·지표는 전부 이걸 기준으로 센다.
const everyone = () => [...players.values(), ...bots.values()];

const livePlayers = () => [...players].filter(([ws,p]) => ws.readyState === 1 && p.save && !p.transferring).map(([,p])=>p);
const mapPlayers = zone => livePlayers().filter(p => p.save.zone === zone);

// Roster positions seed peer rendering before the first live position message.
const roster = () =>
  [...livePlayers(), ...bots.values()].map(p => ({
    id: p.id, name: p.name, level: p.level, zone: p.save?.zone || '', bot: !p.save,
    gender: p.gender || 'male', x: p.x ?? p.snapshot?.x ?? 40, face: p.face ?? p.snapshot?.face ?? 1,
    equip: (p.snapshot && p.snapshot.equip) || null,
  }));

function send(ws, msg) {
  if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(msg));
}

function broadcast(msg, except) {
  for (const ws of players.keys()) if (ws !== except) send(ws, msg);
}

/* ---------- redis ---------- */
//redis 가 없어도 서버는 뜬다. 혼자 도는 로컬 테스트용 — 대신 채널 명부와 이관은 꺼진다.
let redis = null;

async function initRedis() {
  if (!REDIS_URL) {
    console.log('[redis] REDIS_URL 없음. 채널 명부와 이관 없이 단독으로 돈다.');
    return;
  }
  const c = createClient({ url: REDIS_URL });
  c.on('error', e => console.error('[redis]', e.message)); //핸들러가 없으면 연결 끊길 때 프로세스가 통째로 죽는다
  await c.connect();
  redis = c;
  console.log(`[redis] ${REDIS_URL} 연결`);
}

//"나 살아있고 몇 명 있다" 를 명부에 적는다. TTL 이 있어서 갱신을 멈추면 알아서 지워진다.
async function heartbeat() {
  if (!redis) return;
  const m = C.channelMetrics(everyone());
  const row = { index: INDEX, base: MY_BASE, players: m.active_players, full: m.full, draining };
  try {
    await redis.set(`channel:${INDEX}`, JSON.stringify(row), { EX: CHANNEL_TTL_SEC });
    for (const me of players.values()) if (me.owner) await redis.eval(
      "if redis.call('get',KEYS[1]) == ARGV[1] then return redis.call('expire',KEYS[1],30) end return 0",
      { keys: ['owner:' + me.id], arguments: [me.owner] });
  } catch (e) {
    console.error('[heartbeat]', e.message);
  }
}

async function listChannels() {
  if (!redis) return [];
  try {
    const keys = await redis.keys('channel:*'); //채널이 수십 개 수준이라 KEYS 로 충분하다. 수천 개가 되면 SCAN 으로 바꿔야 한다
    if (!keys.length) return [];
    const vals = await redis.mGet(keys);
    return vals.filter(Boolean).map(v => JSON.parse(v)).sort((a, b) => a.index - b.index);
  } catch (e) {
    console.error('[channels]', e.message);
    return [];
  }
}

// Redis 소유권으로 서로 다른 파드의 동시 인벤 변경을 막는다. 토큰이 같은 연결만 갱신·해제한다.
async function releaseOwner(me) {
  if (!redis || !me.owner) return;
  await redis.eval("if redis.call('get',KEYS[1]) == ARGV[1] then return redis.call('del',KEYS[1]) end return 0",
    { keys: ['owner:' + me.id], arguments: [me.owner] });
}

//플레이어 상태를 사물함에 맡긴다. 받는 채널이 이걸 꺼내서 이어붙인다.
async function park(p) {
  if (!redis || !p.snapshot) return false;
  try {
    await redis.set(`transfer:${p.id}`, JSON.stringify(I.transport(p.save,p.snapshot)), { EX: TRANSFER_TTL_SEC });
    return true;
  } catch (e) {
    console.error('[park]', e.message);
    return false;
  }
}

//사물함에서 짐을 꺼내고 바로 비운다. 한 번 쓰면 없어져야 같은 짐을 두 번 못 쓴다.
async function claim(id) {
  if (!redis) return null;
  try {
    const raw = await redis.getDel(`transfer:${id}`);
    if (!raw) return null;
    return JSON.parse(raw);
  } catch (e) {
    console.error('[claim]', e.message);
    return null;
  }
}

/* ---------- 세이브 DB (Cloud SQL) ---------- */
//세이브는 채널마다 다르지 않고 계정에 딸린 값이라, 어느 채널 파드가 받아도 같은 DB 한 곳에 쓴다.
//드레인 중 넘기는 이관 페이로드(redis)와는 별개다 — 저건 "지금 화면", 이건 "다음 접속".
let db = null;

async function initDb() {
  if (!DATABASE_URL) {
    console.log('[db] DATABASE_URL 없음. 세이브 저장 없이 돈다.');
    return;
  }
  const pool = new Pool({ connectionString: DATABASE_URL, max: 4 });
  pool.on('error', e => console.error('[db]', e.message)); //유휴 커넥션 에러로 프로세스가 죽지 않게
  //로그인 계정. saves 보다 먼저 만든다 — saves.user_id 가 이 테이블을 FK로 참조하기 때문.
  //예전엔 이걸 브라우저 localStorage 에만 뒀는데, 그러면 다른 기기에서 만든 계정을 알 방법이 없다.
  await pool.query(`
    CREATE TABLE IF NOT EXISTS accounts (
      user_id    text        PRIMARY KEY,
      name       text        NOT NULL,
      pw_hash    text        NOT NULL,
      gender     text        NOT NULL DEFAULT 'male',
      role       text        NOT NULL DEFAULT 'user',
      created_at timestamptz NOT NULL DEFAULT now()
    )`);
  //세이브는 계정 없이 존재할 수 없다 — accounts 를 FK로 참조, 계정이 삭제되면 세이브도 같이 지운다.
  //name·level 은 세이브를 안 열고도 목록·랭킹에서 쓰려고 컬럼으로도 뺐다. data 에도 그대로 들어있다.
  await pool.query(`
    CREATE TABLE IF NOT EXISTS saves (
      user_id    text        PRIMARY KEY REFERENCES accounts(user_id) ON DELETE CASCADE,
      name       text        NOT NULL,
      level      int         NOT NULL DEFAULT 1,
      data       jsonb       NOT NULL,
      updated_at timestamptz NOT NULL DEFAULT now()
    )`);
  // Legacy tables may lack this FK. NOT VALID preserves existing orphan saves without scanning/deleting them.
  // Serialize concurrent pod startups before checking/adding the same constraint.
  await pool.query(`
    DO $$
    BEGIN
      LOCK TABLE saves IN SHARE ROW EXCLUSIVE MODE;
      IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE contype = 'f' AND conrelid = 'saves'::regclass AND confrelid = 'accounts'::regclass
          AND confdeltype = 'c'
          AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'saves'::regclass AND attname = 'user_id')]
          AND confkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'accounts'::regclass AND attname = 'user_id')]
      ) THEN
        ALTER TABLE saves ADD CONSTRAINT saves_account_cascade_fk
          FOREIGN KEY (user_id) REFERENCES accounts(user_id) ON DELETE CASCADE NOT VALID;
      END IF;
    END $$;
  `);
  await pool.query('CREATE INDEX IF NOT EXISTS saves_level_idx ON saves (level DESC)');
  //유저가 친 채팅만 남기는 로그. 시스템 메시지(입장/드레인 안내)는 안 들어온다.
  //한 유저가 채팅 여러 개 = 1:N 이라 자동증가 id 가 PK, user_id 는 saves 를 참조하는 FK.
  await pool.query(`
    CREATE TABLE IF NOT EXISTS chats (
      id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
      user_id    text        NOT NULL REFERENCES saves(user_id) ON DELETE CASCADE,
      text       text        NOT NULL,
      created_at timestamptz NOT NULL DEFAULT now()
    )`);
  await pool.query('CREATE INDEX IF NOT EXISTS chats_user_time_idx ON chats (user_id, created_at DESC)');
  db = pool;
  console.log('[db] Cloud SQL 연결, accounts·saves·chats 테이블 준비');
  if (process.env.ADMIN_PASSWORD) await ensureAdmin();
}

/* ---------- 계정 (Cloud SQL) ---------- */
//pbkdf2 는 node 표준 내장이라 의존성이 안 늘어난다. salt 는 계정마다 다르게 둬서
//같은 비번이라도 저장된 해시가 겹치지 않게 한다.
function hashPw(pw, salt = crypto.randomBytes(16).toString('hex')) {
  const hash = crypto.pbkdf2Sync(pw, salt, 100000, 32, 'sha256').toString('hex');
  return `${salt}:${hash}`;
}
function verifyPw(pw, stored) {
  const [salt, hash] = String(stored).split(':');
  if (!salt || !hash) return false;
  const a = Buffer.from(hash, 'hex'), b = Buffer.from(hashPw(pw, salt).split(':')[1], 'hex');
  return a.length === b.length && crypto.timingSafeEqual(a, b); //길이·시간 비교로 타이밍 공격을 줄인다
}

async function ensureAdmin() {
  await db.query(
    `INSERT INTO accounts (user_id, name, pw_hash, role) VALUES ($1, $2, $3, 'admin')
     ON CONFLICT (user_id) DO UPDATE SET pw_hash = $3`,
    ['admin', '관리자', hashPw(process.env.ADMIN_PASSWORD)]
  );
}

const localAccounts = new Map();
const sessions = new Map();
const SESSION_SEC = 86400;
const AUTH_REQUIRED = !!DATABASE_URL;
const cookieToken = req => (req.headers.cookie || '').match(/(?:^|;\s*)game_session=([a-f0-9]{64})(?:;|$)/)?.[1];
function sameOrigin(req) {
  if (!req.headers.origin) return true;
  try { return new URL(req.headers.origin).host === req.headers.host; } catch { return false; }
}
async function sessionFor(req) {
  const token = cookieToken(req);
  if (!token) return null;
  const raw = redis ? await redis.get('session:' + token) : sessions.get(token);
  const session = raw && JSON.parse(raw);
  return session && session.expires > Date.now() ? session : null;
}
async function issueSession(req, res, account) {
  const token = crypto.randomBytes(32).toString('hex');
  const data = JSON.stringify({ ...account, expires: Date.now() + SESSION_SEC * 1000 });
  if (redis) await redis.set('session:' + token, data, { EX: SESSION_SEC });
  else { sessions.set(token, data); setTimeout(() => sessions.delete(token), SESSION_SEC * 1000).unref(); }
  const secure = AUTH_REQUIRED || req.headers['x-forwarded-proto'] === 'https';
  res.setHeader('Set-Cookie', `game_session=${token}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${SESSION_SEC}${secure ? '; Secure' : ''}`);
}

function validAccount(name, id, pw) {
  if (typeof name !== 'string' || name.length < 1 || name.length > 10) return '캐릭터 이름은 1~10자로 정한다';
  if (typeof id !== 'string' || id.length < 3 || id.length > 64) return '아이디는 3자 이상이어야 한다';
  if (!/^[a-zA-Z0-9_]+$/.test(id)) return '아이디는 영문·숫자·밑줄만 쓸 수 있다';
  if (typeof pw !== 'string' || pw.length < 4 || pw.length > 128) return '비밀번호는 4자 이상이어야 한다';
  return null;
}

async function accountSignup(name, id, pw, gender) {
  if (DATABASE_URL && !db) return { err: 'DB 연결 안 됨' };
  const err = validAccount(name, id, pw);
  if (err) return { err };
  if (!db) {
    if (localAccounts.has(id) || [...localAccounts.values()].some(a => a.name === name)) return { err: '이미 있는 아이디이거나 캐릭터 이름이다' };
    const account = { id, name, gender: gender === 'female' ? 'female' : 'male', role: 'user' };
    localAccounts.set(id, { ...account, pw_hash: hashPw(pw) });
    return { ok: account };
  }
  try {
    const dup = await db.query('SELECT 1 FROM accounts WHERE user_id = $1 OR name = $2', [id, name]);
    if (dup.rowCount) return { err: '이미 있는 아이디이거나 캐릭터 이름이다' };
    const g = gender === 'female' ? 'female' : 'male';
    await db.query('INSERT INTO accounts (user_id, name, pw_hash, gender) VALUES ($1, $2, $3, $4)',
      [id, name, hashPw(pw), g]);
    return { ok: { id, name, gender: g, role: 'user' } };
  } catch (e) {
    console.error('[account signup]', e.message);
    return { err: '가입 실패' };
  }
}

async function accountLogin(id, pw) {
  if (typeof id !== 'string' || typeof pw !== 'string' || id.length > 64 || pw.length > 128) return { err: '아이디와 비밀번호를 확인하세요' };
  if (DATABASE_URL && !db) return { err: 'DB 연결 안 됨' };
  if (!db) {
    const row = localAccounts.get(id);
    if (!row || !verifyPw(pw, row.pw_hash)) return { err: '아이디와 비밀번호를 확인하세요' };
    const { pw_hash, ...account } = row;
    return { ok: account };
  }
  try {
    const r = await db.query('SELECT user_id, name, pw_hash, gender, role FROM accounts WHERE user_id = $1', [id]);
    const row = r.rows[0];
    if (!row || (row.role === 'admin' && !process.env.ADMIN_PASSWORD)) return { err: '아이디와 비밀번호를 확인하세요' };
    if (!verifyPw(pw, row.pw_hash)) return { err: '비밀번호가 다르다' };
    return { ok: { id: row.user_id, name: row.name, gender: row.gender, role: row.role } };
  } catch (e) {
    console.error('[account login]', e.message);
    return { err: '로그인 실패' };
  }
}

//관리자 화면의 접속자 현황. saves 를 왼쪽조인해서 레벨·처치도 같이 낸다(세이브가 없으면 미접속 취급).
async function accountRoster() {
  if (!db) return [];
  try {
    const r = await db.query(`
      SELECT a.user_id AS id, a.name, a.role, COALESCE(s.level, 0) AS level, COALESCE((s.data->>'kills')::int, 0) AS kills
      FROM accounts a LEFT JOIN saves s ON s.user_id = a.user_id
      ORDER BY level DESC`);
    return r.rows;
  } catch (e) {
    console.error('[account roster]', e.message);
    return [];
  }
}

async function accountRemove(id) {
  if (!db) { memSaves.delete(id); return localAccounts.delete(id); }
  let connection;
  try {
    connection = await db.connect();
    await connection.query('BEGIN');
    const account = await connection.query("SELECT user_id FROM accounts WHERE user_id = $1 AND role <> 'admin' FOR UPDATE", [id]);
    if (!account.rowCount) { await connection.query('ROLLBACK'); return false; }
    // Older deployed saves tables have no FK; delete owned rows explicitly in one transaction.
    await connection.query('DELETE FROM chats WHERE user_id = $1', [id]);
    await connection.query('DELETE FROM saves WHERE user_id = $1', [id]);
    await connection.query('DELETE FROM accounts WHERE user_id = $1', [id]);
    await connection.query('COMMIT');
    return true;
  } catch (e) {
    if (connection) await connection.query('ROLLBACK').catch(() => {});
    console.error('[account remove]', e.message);
    return false;
  } finally { if (connection) connection.release(); }
}

//채팅 1줄을 남긴다. 세이브 행이 아직 없으면(FK 위반) 조용히 버린다 — 접속 직후 몇 초의 채팅은 안 남을 수 있다.
async function logChat(userId, text) {
  if (!db) return;
  try {
    await db.query('INSERT INTO chats (user_id, text) VALUES ($1, $2)', [userId, text]);
  } catch (e) {
    if (e.code !== '23503') console.error('[chat log]', e.message); //23503 = FK 위반(세이브 아직 없음). 그건 무시
  }
}

//ponytail: DATABASE_URL 없을 때 세이브는 프로세스 메모리에만 둔다(로컬 베타용). 서버를 끄면 사라진다.
const memSaves = new Map();

async function loadSave(id) {
  if (!db) return memSaves.has(id) ? JSON.parse(memSaves.get(id)) : null;
  try {
    const r = await db.query('SELECT data FROM saves WHERE user_id = $1', [id]);
    return r.rows[0]?.data ?? null;
  } catch (e) {
    console.error('[db load]', e.message);
    throw e;
  }
}

async function putSave(id, data) {
  if (!db) { memSaves.set(id, JSON.stringify(data)); return true; }
  try {
    //name·level 은 data 에서 꺼내 컬럼에도 같이 쓴다. 게임 코드는 여전히 data 통째로만 읽는다.
    const name = String(data.name || '모험가').slice(0, 20);
    const level = Number(data.level) || 1;
    await db.query(
      `INSERT INTO saves (user_id, name, level, data) VALUES ($1, $2, $3, $4)
       ON CONFLICT (user_id) DO UPDATE
         SET name = $2, level = $3, data = $4, updated_at = now()`,
      [id, name, level, data]
    );
    return true;
  } catch (e) {
    console.error('[db save]', e.message);
    return false;
  }
}

//요청 본문을 JSON 으로 읽는다. 16KB 넘으면 자른다(세이브가 그것보다 클 일이 없다).
function readJson(req) {
  return new Promise(resolve => {
    let buf = '';
    req.on('data', c => {
      buf += c;
      if (buf.length > 16384) { buf = ''; req.destroy(); resolve(null); }
    });
    req.on('end', () => { try { resolve(JSON.parse(buf)); } catch { resolve(null); } });
    req.on('error', () => resolve(null));
  });
}

/* ---------- 정적 파일 ---------- */
const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm', '.data': 'application/octet-stream',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.ico': 'image/x-icon'
};
function stripBase(urlPath) {
  if (MY_BASE && (urlPath === MY_BASE || urlPath.startsWith(MY_BASE + '/'))) return urlPath.slice(MY_BASE.length) || '/';
  return urlPath;
}
function serveStatic(urlPath, res) {
  let rel;
  try { rel = decodeURIComponent(urlPath).replace(/^\/+/, '') || 'index.html'; }
  catch { res.writeHead(400).end(); return; }
  if (rel !== 'index.html' && rel !== 'launcher.js' && rel !== 'launcher.css' && !rel.startsWith('web/')) {
    res.writeHead(404).end('not found'); return;
  }
  const file = path.resolve(__dirname, rel);
  const webRoot = path.join(__dirname, 'web') + path.sep;
  if (rel.startsWith('web/') && !file.startsWith(webRoot)) { res.writeHead(403).end(); return; }
  fs.stat(file, (err, stat) => {
    if (err || !stat.isFile()) { res.writeHead(404).end('not found'); return; }
    const compressed = file.endsWith('.gz');
    const ext = path.extname(compressed ? file.slice(0, -3) : file);
    const headers = { 'content-type': MIME[ext] || 'application/octet-stream',
      'content-length': stat.size, 'cache-control': /\/[a-f0-9]{32}\./.test(file) ? 'public, max-age=31536000, immutable' : 'no-store',
      'x-content-type-options': 'nosniff' };
    if (compressed) headers['content-encoding'] = 'gzip';
    res.writeHead(200, headers);
    if (res.req.method === 'HEAD') { res.end(); return; }
    const stream = fs.createReadStream(file);
    stream.on('error', () => res.destroy()); stream.pipe(res);
  });
}

/* ---------- HTTP ---------- */
const server = http.createServer(async (req, res) => {
  try {
  if (closing) { res.writeHead(503).end('closing'); return; }
  const urlPath = stripBase(req.url.split('?')[0]);
  if (!sameOrigin(req)) { res.writeHead(403).end(); return; }
  const session = await sessionFor(req);
  res.setHeader('Cache-Control', 'no-store');
  if (urlPath === '/account/me') {
    res.writeHead(session ? 200 : 401, { 'content-type': 'application/json' });
    res.end(JSON.stringify(session ? { ok: { id: session.id, name: session.name, gender: session.gender, role: session.role } } : { err: '로그인이 필요합니다' })); return;
  }
  if (urlPath === '/account/logout' && req.method === 'POST') {
    const token = cookieToken(req);
    if (token) { if (redis) await redis.del('session:' + token); else sessions.delete(token); }
    res.setHeader('Set-Cookie', 'game_session=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0');
    res.writeHead(204).end(); return;
  }
  if (urlPath === '/version') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ version: require('./package.json').version })); return;
  }

  if (urlPath === '/healthz') {
    const ready = !draining && (!DATABASE_URL || !!db) && (!REDIS_URL || !!redis);
    res.writeHead(ready ? 200 : 503, { 'content-type': 'text/plain' }); //드레인 중엔 503 을 줘야 LB 가 신규 트래픽을 끊는다
    res.end(ready ? 'ok' : 'unavailable');
    return;
  }

  //클라이언트가 "채널 몇 개 있고 어디로 가면 되냐" 를 물어보는 곳.
  //목록이 redis 명부에서 나오기 때문에 KEDA 가 파드를 늘리면 여기도 자동으로 늘어난다.
  if (urlPath === '/channels') {
    const list = await listChannels();
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ me: INDEX, cap: C.CHANNEL_CAP, channels: list }));
    return;
  }

  //KEDA metrics-api 가 읽는 전체 인원 집계. targetValue(3)로 나눠 파드 수를 정한다.
  if (urlPath === '/scale') {
    const list = await listChannels();
    //KEDA 는 sub 채널(1~4)만 스케일한다. 홈 채널(0번)은 main_channel.yaml 이 항상 띄우므로
    //인원 합에서 뺀다. 안 빼면 0번에 사람이 몰릴 때 빈 sub 채널이 과다하게 뜬다.
    const total = list.filter(c => c.index !== 0).reduce((s, c) => s + (c.players || 0), 0);
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ total_players: total, channels: list.length }));
    return;
  }

  //세이브 load/save. 경로의 id 는 계정 id. DB 한 곳을 보므로 어느 채널로 와도 된다.
  // 운영 DB 사용 시 로그인 계정의 세이브만 조회·변경할 수 있다.
  const saveMatch = urlPath.match(/^\/save\/([^/]+)$/);
  if (saveMatch) {
    const id = decodeURIComponent(saveMatch[1]);
    if ((AUTH_REQUIRED || session) && session?.id !== id) { res.writeHead(401).end('login required'); return; }
    if (req.method === 'GET') {
      const data = await serialSave(id, async () => [...players.values()].find(p => p.id === id && p.save)?.save || await loadSave(id));
      res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({ data }));
      return;
    }
    if (req.method === 'POST') {
      const body = await readJson(req);
      if (!body || typeof body !== 'object' || Array.isArray(body) ||
          (body.potionThreshold !== undefined && ![20, 30, 40, 50, 60].includes(body.potionThreshold))) {
        res.writeHead(400).end('bad preferences'); return;
      }
      const ok = await serialSave(id, async () => {
        const online = [...players.values()].find(p => p.id === id && p.save);
        const base = online ? online.save : await loadSave(id);
        const data = I.normalize(JSON.parse(JSON.stringify(base || {})));
        if (body.potionThreshold !== undefined) data.potionThreshold = body.potionThreshold;
        if (!(await putSave(id, data))) return false;
        if (online) online.save = data;
        return true;
      });
      res.writeHead(ok ? 204 : 503).end();
      return;
    }
    res.writeHead(405).end();
    return;
  }

  //계정 가입/로그인. DB 한 곳에 두니 세이브랑 마찬가지로 어느 채널 파드·어느 기기로 접속해도 같다.
  // 세션 쿠키는 Redis에 공유되어 채널을 옮겨도 같은 계정을 확인한다.
  if (urlPath === '/account/signup' && req.method === 'POST') {
    const body = await readJson(req);
    if (!body) { res.writeHead(400).end('bad json'); return; }
    const r = await accountSignup(body.name, body.id, body.pw, body.gender);
    res.writeHead(r.err ? 400 : 200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(r));
    return;
  }
  if (urlPath === '/account/login' && req.method === 'POST') {
    const body = await readJson(req);
    if (!body) { res.writeHead(400).end('bad json'); return; }
    const r = await accountLogin(body.id, body.pw);
    if (r.ok) await issueSession(req, res, r.ok);
    res.writeHead(r.err ? 400 : 200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(r));
    return;
  }
  if (urlPath === '/account/roster') {
    if (session?.role !== 'admin') { res.writeHead(403).end(); return; }
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(await accountRoster()));
    return;
  }
  if (urlPath === '/account/remove' && req.method === 'POST') {
    const body = await readJson(req);
    if (!session || session.id !== body?.id) { res.writeHead(403).end(); return; }
    const ok = await serialSave(session.id, async () => {
      for (const [socket, me] of players) if (me.id === session.id) socket.close(1000, 'account removed');
      return accountRemove(session.id);
    });
    res.writeHead(ok ? 204 : 404).end();
    return;
  }

  serveStatic(urlPath, res);
  } catch (err) {
    console.error('[http]', err.message);
    if (!res.headersSent) res.writeHead(503);
    res.end('request failed');
  }
});

/* ---------- 아이템 (권위 서버) ---------- */
// Server-issued per-connection receipts prevent duplicate, unknown and tier-swapped rewards.
// ponytail: the client still simulates damage/spawns; fully authoritative combat is needed to stop plausible fabricated encounters.
const KILL_BURST = 14, KILL_PER_SEC = 3, BOSS_GAP_MS = 60000;
const ITEM_REQ = new Set(['inv', 'kill', 'equip', 'enhance', 'buy', 'sell', 'potion', 'summon', 'learn', 'disassemble', 'expand', 'auto_sell', 'auto_disassemble', 'allocate_stat', 'allocate_auto', 'summon_ack', 'map']);

function itemRequest(me, msg) {
  const s = me.save;
  switch (msg.type) {
    case 'map': return I.map(s, msg.zone);
    case 'inv': return { ok: true };
    case 'allocate_stat': return I.allocateStat(s, msg.item, msg.qty);
    case 'allocate_auto': return I.allocateAuto(s);
    case 'learn': return I.learn(s, msg.item);
    case 'disassemble': return I.disassemble(s, msg.uid, msg.uids, msg.level);
    case 'expand': return I.expand(s);
    case 'auto_sell': return I.autoSell(s, msg.enabled, msg.level);
    case 'auto_disassemble': return I.autoDisassemble(s, msg.enabled, msg.level);
    case 'kill': {
      const monster = me.monsters.get(msg.receipt);
      if (!monster || monster.tier !== msg.tier) return {ok:false,code:'bad_receipt'};
      if (Date.now()-monster.at < 300) return {ok:false,code:'too_fast'};
      const now = Date.now();
      me.tokens = Math.min(KILL_BURST, me.tokens + (now - me.tokenAt) / 1000 * KILL_PER_SEC); me.tokenAt = now;
      if (me.tokens < 1) return { ok: false, code: 'rate' };
      const boss = Number(msg.tier) >= 19;
      if (boss && now - me.bossAt < BOSS_GAP_MS) return { ok: false, code: 'rate' };
      const r = I.kill(s, msg.tier);
      if (r.ok) { me.tokens--; if (boss) me.bossAt = now; }
      return r;
    }
    case 'equip': return I.equip(s, msg.slot, msg.uid);
    case 'enhance': return I.enhance(s, msg.uid);
    case 'buy': return I.buy(s, msg.item, msg.qty);
    case 'sell': return I.sell(s, msg.uid, msg.uids);
    case 'potion': return I.usePotion(s);
    case 'summon': return I.summon(s, msg.tier);
    case 'summon_ack': return I.summonAck(s, msg.tier, msg.spawned, msg.token);
  }
}

/* ---------- WebSocket ---------- */
const wss = new WebSocketServer({ server, maxPayload: 16384 });

wss.on('connection', (ws, req) => {
  if (!sameOrigin(req)) { ws.close(1008, 'origin'); return; }
  if (draining) {
    send(ws, { type: 'closed', reason: '폐쇄 중인 채널이다' });
    ws.close();
    return;
  }
  if (everyone().length >= C.CHANNEL_CAP) {
    send(ws, { type: 'full', cap: C.CHANNEL_CAP });
    ws.close();
    return;
  }

  const me = { id: C.newId('u'), name: '접속중', level: 1, snapshot: null };
  players.set(ws, me);

  ws.on('message', async raw => {
    let msg;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }
    if (!msg || typeof msg !== 'object' || Array.isArray(msg) || typeof msg.type !== 'string') return;
    if (closing) return;

    try {
    if (msg.type === 'join') {
      if (me.joining) return; me.joining = true;
      const account = await sessionFor(req);
      if ((AUTH_REQUIRED || cookieToken(req)) && !account) { send(ws, { type: 'closed', reason: '로그인이 필요합니다' }); ws.close(1008); return; }
      if (account) {
        if (msg.id && msg.id !== account.id) { ws.close(1008, 'account'); return; }
        msg.id = account.id; msg.name = account.name; msg.gender = account.gender;
      }
      if (msg.id) me.id = String(msg.id).slice(0, 64); //계정 id 를 그대로 쓴다. 이게 사물함 열쇠라 채널이 바뀌어도 같아야 한다
      await serialSave(me.id, async () => {
      if (ws.readyState !== 1) return;
      if (redis) {
        me.owner = crypto.randomBytes(16).toString('hex');
        if (!(await redis.set('owner:' + me.id, me.owner, { NX: true, EX: 30 }))) {
          me.owner = null; send(ws, { type: 'closed', reason: '이미 접속 중인 계정입니다' }); ws.close(1008); return;
        }
      }
      me.authExpires = account?.expires;
      me.name = String(msg.name || '모험가').slice(0, 10);
      me.level = Number(msg.level) || 1;
      me.gender = msg.gender === 'female' ? 'female' : 'male';

      //같은 계정이 이 채널에 이미 붙어 있으면 옛 연결을 끊는다(인벤 이중 사용 방지).
      let prev = null; //끊는 연결이 들고 있던 세이브가 DB 보다 최신이다(쓰기 대기 중일 수 있음)
      for (const [ows, p] of players) if (ows !== ws && msg.id && p.id === me.id) { prev = p.save; send(ows, { type: 'closed', reason: '다른 곳에서 접속했다' }); ows.close(); }
      me.account = !!msg.id;
      me.save = I.normalize(prev || (msg.id && await loadSave(me.id)) || { name: me.name, gender: me.gender });
      me.save.name = me.name; me.save.gender = me.gender;
      me.level = me.save.level;
      me.tokens = KILL_BURST; me.tokenAt = Date.now(); me.bossAt = 0;
      me.monsters = new Map(); me.spawnTokens = 5; me.spawnAt = Date.now();
      me.chatTokens = 4; me.chatAt = Date.now();

      const parked = msg.resume ? await claim(me.id) : null; //다른 채널에서 넘어온 거면 맡긴 짐이 있다
      if (parked) { me.snapshot = I.transport(me.save, parked); send(ws, { type: 'resume', state: parked }); }

      send(ws, {
        type: 'welcome',
        id: me.id,
        channel: CHANNEL_ID,
        index: INDEX,
        cap: C.CHANNEL_CAP,
        roster: roster(),
        resumed: !!parked,
      });
      send(ws, { type: 'inv', req: 'join', ok: true, code: '', state: I.view(me.save) });
      broadcast({ type: 'system', text: `${me.name} 님이 입장했다.` }, ws);
      broadcast({ type: 'roster', roster: roster() });
      });
      return;
    }

    if (!me.save) return; // No gameplay/chat/state messages before the authenticated join completes.

    if (msg.type === 'spawn' || msg.type === 'despawn') {
      await serialSave(me.id, async () => {
        if (!me.save || me.transferring || ws.readyState !== 1) return;
        if (AUTH_REQUIRED && (!me.authExpires || me.authExpires < Date.now())) { ws.close(1008, 'session expired'); return; }
        if (redis && (!me.owner || await redis.get('owner:' + me.id) !== me.owner)) { ws.close(1008, 'account owner'); return; }
        if (msg.type === 'despawn') { me.monsters.delete(msg.receipt); return; }
        const now = Date.now();
        me.spawnTokens = Math.min(5,me.spawnTokens+(now-me.spawnAt)/1000); me.spawnAt = now;
        let r = I.spawnAllowed(me.save,msg.tier);
        if (!Number.isSafeInteger(msg.monsterId) || msg.monsterId < 1 || [...me.monsters.values()].some(m=>m.id===msg.monsterId)) r={ok:false,code:'bad_spawn'};
        else if (me.monsters.size >= 5 || mapPlayers(me.save.zone).reduce((total,p)=>total+(p.monsters?.size||0),0) >= mapPlayers(me.save.zone).length*5) r={ok:false,code:'spawn_full'};
        else if (me.spawnTokens < 1) r={ok:false,code:'spawn_rate'};
        else if (msg.tier >= 19 && [...me.monsters.values()].some(m=>m.tier>=19)) r={ok:false,code:'boss_alive'};
        let receipt;
        if (r.ok) { receipt=crypto.randomUUID(); me.monsters.set(receipt,{id:msg.monsterId,tier:msg.tier,at:now}); me.spawnTokens--; }
        send(ws,{type:'spawn',monsterId:msg.monsterId,tier:msg.tier,receipt,ok:r.ok,code:r.code||''});
      });
      return;
    }

    if (msg.type === 'chat') {
      if (typeof msg.text !== 'string' || msg.text.length > 120) return;
      const text = msg.text.trim();
      if (!text) return;
      const now = Date.now();
      me.chatTokens = Math.min(4, me.chatTokens + (now - me.chatAt) / 1000); me.chatAt = now;
      if (me.chatTokens < 1) return;
      me.chatTokens--;
      broadcast({ type: 'chat', who: me.name, text }); //보낸 사람한테도 돌려준다. 클라가 자기 말을 따로 안 그려도 되게
      logChat(me.id, text); //유저 채팅만 DB 에 남긴다(봇은 WS 가 없어서 여기 못 온다)
      return;
    }

    //클라가 1초마다 자기 상태를 통째로 보낸다. 서버는 들고만 있다가 이관할 때 사물함에 넣는다.
    if (msg.type === 'state') {
      if (!me.save) me.level = Number(msg.level) || me.level; //세이브가 있으면 레벨은 서버 값
      if (msg.snapshot && me.save) me.snapshot = I.transport(me.save, msg.snapshot);
      broadcast({ type: 'roster', roster: roster() }, ws);
      return;
    }

    // Store bounded render positions for roster initialization; combat remains client simulated.
    //그래서 서로 "보이기만" 하고 접촉(충돌·전투) 판정은 애초에 없다.
    if (msg.type === 'pos') {
      const x = Number(msg.x);
      if (!Number.isFinite(x)) return;
      me.x = Math.min(78.5, Math.max(1.5, x)); me.face = msg.face === -1 ? -1 : 1;
      broadcast({ type: 'pos', id: me.id, x: me.x, face: me.face }, ws);
      return;
    }

    //아이템 요청. 결과는 항상 'inv' 한 종류로 돌려준다(성공·실패 모두 state 포함, seq 는 그대로 되돌려 줌).
    if (ITEM_REQ.has(msg.type)) {
      await serialSave(me.id, async () => {
      if (ws.readyState !== 1 || me.transferring) return;
      if (AUTH_REQUIRED && (!me.authExpires || me.authExpires < Date.now())) { ws.close(1008, 'session expired'); return; }
      if (redis && (!me.owner || await redis.get('owner:' + me.id) !== me.owner)) { ws.close(1008, 'account owner'); return; }
      if (!me.save) { send(ws, { type: 'inv', req: msg.type, seq: msg.seq, ok: false, code: 'not_joined' }); return; }
      const before = me.save, rateBefore = {tokens:me.tokens, tokenAt:me.tokenAt, bossAt:me.bossAt};
      me.save = JSON.parse(JSON.stringify(before));
      let r = itemRequest(me, msg);
      // 저장에 실패한 변경은 성공으로 알리지 않고 이전 자원·장비로 되돌린다.
      if (r.ok && msg.type !== 'inv' && me.account && !(await putSave(me.id, me.save))) {
        me.save = before; Object.assign(me,rateBefore);
        r = { ok: false, code: 'save_failed' };
      }
      send(ws, { type: 'inv', req: msg.type, seq: msg.seq, ok: r.ok, code: r.code || '', drop: r.drop, enh: r.enh, sell: r.sell, disassemble: r.disassemble, state: I.view(me.save) });
      if (r.ok && msg.type === 'kill') me.monsters.delete(msg.receipt);
      if (r.ok && msg.type === 'map') { me.monsters.clear(); broadcast({ type:'roster', roster:roster() }); }
      if (r.ok && msg.type !== 'inv') {
        if (me.level !== me.save.level) { me.level = me.save.level; broadcast({ type: 'roster', roster: roster() }); }
      }
      });
      return;
    }

    //관리자 메뉴의 "동료 추가/내보내기". 가짜 접속자를 늘리고 줄인다. 채널 포화·KEDA 테스트용.
    if (msg.type === 'bot') {
      if ((await sessionFor(req))?.role !== 'admin') return;
      const delta = msg.delta;
      if (delta !== 1 && delta !== -1) return;
      if (delta === 1 && (!Number.isInteger(msg.level) || msg.level < 1 || msg.level > 100)) return;
      if (delta > 0 && everyone().length < C.CHANNEL_CAP) {
        const id = C.newId('bot');
        bots.set(id, { id, name: `봇${bots.size + 1}`, level: msg.level });
      } else if (delta < 0) {
        const k = [...bots.keys()].pop();
        if (k) bots.delete(k);
      }
      broadcast({ type: 'roster', roster: roster() });
      heartbeat(); //명부 인원수도 바로 갱신해야 KEDA 가 빨리 반응한다
      return;
    }

    //게임 안에서 "채널 이동" 을 눌렀을 때. 드레인이랑 똑같은 짓을 한 명한테만 한다.
    if (msg.type === 'switch') {
      await serialSave(me.id, async () => {
      if (ws.readyState !== 1 || !me.save || me.transferring) return;
      const list = await listChannels();
      const target = list.find(c => c.index === Number(msg.to));
      if (!target || target.index === INDEX) {
        send(ws, { type: 'system', text: '그런 채널이 없다.' });
        return;
      }
      if (target.full)     { send(ws, { type: 'system', text: `${target.index}번 채널이 꽉 찼다.` }); return; }
      if (target.draining) { send(ws, { type: 'system', text: `${target.index}번 채널은 닫히는 중이다.` }); return; }
      if (!(await park(me))) {
        send(ws, { type: 'system', text: '상태 저장에 실패해서 이동을 취소했다.' }); //짐을 못 맡겼는데 보내면 상태가 날아간다
        return;
      }
      me.transferring = true;
      await releaseOwner(me);
      send(ws, { type: 'transfer', to: target.index, base: target.base, reason: 'switch' });
      ws.close();
      });
      return;
    }
    } catch (err) {
      console.error('[message]', err.message);
      send(ws, { type: 'system', text: '요청 처리에 실패했다.' });
      if (msg.type === 'join') ws.close(1011, 'join failed');
    }
  });

  ws.on('close', () => {
    releaseOwner(me).catch(e => console.error('[owner release]', e.message));
    players.delete(ws);
    broadcast({ type: 'system', text: `${me.name} 님이 나갔다.` });
    broadcast({ type: 'roster', roster: roster() });
    if (draining && players.size === 0) shutdown('드레인 완료');
  });
});

/* ---------- 종료 처리 ---------- */
let closing = false;

async function shutdown(reason) {
  if (closing) return;
  closing = true;
  console.log(`[shutdown] ${reason}`);
  // 이미 접수한 저장을 마친 뒤 종료한다. 저장 실패는 요청 응답에서도 실패로 알린다.
  await Promise.allSettled([...saveQueues.values()]);
  if (redis) redis.del(`channel:${INDEX}`).catch(() => {}); //명부에서 나를 지운다. 안 지워도 TTL 로 사라지지만 그만큼 빈 채널이 보인다
  wss.close();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 5000).unref();
}

//SIGTERM 을 받아도 바로 안 죽는다. 신규 입장만 막고, 접속자를 다른 채널로 하나씩 넘긴다.
process.on('SIGTERM', async () => {
  draining = true;
  console.log(`[drain] SIGTERM 수신. 접속자 ${players.size}명.`);
  await heartbeat(); //명부에 draining 을 먼저 알려야 다른 채널이 여기로 사람을 안 보낸다

  if (players.size === 0) {
    shutdown('접속자 없음');
    return;
  }

  // 이번 드레인에서 배정한 인원도 더해서 한 채널에 몰아 보내지 않는다.
  const targets = (await listChannels()).filter(c=>c.index !== INDEX && !c.draining && !c.full);
  for (const [ws,p] of players) {
    const target = targets.filter(c=>c.players<C.CHANNEL_CAP).sort((a,b)=>a.players-b.players)[0];
    if (!target) { send(ws,{type:'drain',text:'이 채널이 곧 닫힌다. 다른 채널의 빈자리를 기다려 주세요.'}); continue; }
    const transferred = await serialSave(p.id,async()=>{
      if (p.transferring || ws.readyState !== 1 || !(await park(p))) return false;
      p.transferring=true;
      await releaseOwner(p);
      send(ws,{type:'transfer',to:target.index,base:target.base,reason:'drain'});
      ws.close();
      return true;
    });
    if (transferred) target.players++;
    else send(ws,{type:'drain',text:'상태 저장에 실패해서 이관을 보류했다.'});
  }

  setTimeout(() => shutdown('드레인 시간 초과'), DRAIN_TIMEOUT_SEC * 1000);
});

/* ---------- 기동 ---------- */
//redis·db 어느 쪽이 없어도 서버는 뜬다. 각자 없으면 해당 기능만 꺼진다.
Promise.allSettled([
  initRedis().catch(e => console.error('[redis] 연결 실패, 명부/이관 끔:', e.message)),
  initDb().catch(e => console.error('[db] 연결 실패, 세이브 저장 끔:', e.message)),
]).then(() => {
  heartbeat();
  setInterval(heartbeat, HEARTBEAT_SEC * 1000).unref();
  server.listen(PORT, () => {
    console.log(`[${CHANNEL_ID}] :${PORT} 대기 중 (정원 ${C.CHANNEL_CAP}명, base="${MY_BASE}")`);
  });
});
