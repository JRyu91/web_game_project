'use strict';
const $ = id => document.getElementById(id);
let signup = false, account = null, instance = null;
// 게임 서버 주소. 포트폴리오(1bit.kr/game/, GitHub Pages)에 올린 index.html 은 meta 로 game-origin 을 가리킨다
const API = new URL(document.querySelector('meta[name="game-api"]')?.content || '.', location.href);
if (API.origin !== location.origin) {
  // Unity 의 UnityWebRequest(/save)는 fetch 기본값(same-origin)이라 교차 출처에 쿠키를 안 싣는다 → 게임 서버행만 include
  const nativeFetch = window.fetch.bind(window);
  window.fetch = (input, init = {}) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url, location.href);
    return nativeFetch(input, url.origin === API.origin && !init.credentials ? { ...init, credentials: 'include' } : init);
  };
}
function fitGame() {
  const viewport = window.visualViewport;
  if (viewport && viewport.scale !== 1) return; // Keep browser pinch zoom available.
  const style = $('game').style;
  style.setProperty('--game-height', `${viewport?.height ?? window.innerHeight}px`);
  style.setProperty('--game-width', `${viewport?.width ?? window.innerWidth}px`);
  style.setProperty('--game-top', `${viewport?.offsetTop ?? 0}px`);
  style.setProperty('--game-left', `${viewport?.offsetLeft ?? 0}px`);
}
window.addEventListener('resize', fitGame);
window.visualViewport?.addEventListener('resize', fitGame);
window.visualViewport?.addEventListener('scroll', fitGame);
fitGame();
/* ---------- 입장 화면 세계: 실제 게임 존 레이어(web/ui) + 기본 캐릭터 스프라이트 ---------- */
const UI = p => new URL('web/ui/' + p, API).href;
const still = matchMedia('(prefers-reduced-motion: reduce)');
// 존 매니페스트(Resources/Sprites/Zones/*/manifest.json)의 레이어 순서·패럴랙스. foot = 720 - 지면선 - 26(프레임 바닥~발)
const ZONES = {
  A: {top: '#68A0E2', foot: 76, sky: [94, 626], layers: [['sky_day', 0], ['clouds', .05], ['far', .15], ['mid', .35], ['near', .6], ['ground', 1], ['shadows', 1], ['props_back', 1], ['props_front', 1], ['ground_front', 1]]},
  B: {top: '#7896A8', foot: 98, sky: [124, 596], layers: [['sky', 0], ['clouds', .05], ['far', .15], ['mid', .35], ['ground', 1], ['shadows', 1], ['props_back', 1], ['lamp_glow', 1], ['props_front', 1], ['ground_front', 1]]},
  C: {top: '#150D11', foot: 100, layers: [['back_wall', .05], ['far', .15], ['mid', .35], ['ceiling', .35], ['lavafalls', .35], ['ground', 1], ['shadows', 1], ['props_back', 1], ['props_front', 1], ['ground_front', 1], ['embers', .35]]},
};
const zoneEls = {};
function buildZone(z) {
  if (zoneEls[z]) return zoneEls[z];
  const el = document.createElement('div'); el.className = 'zone'; el.style.setProperty('--top', ZONES[z].top);
  for (const [file, p] of ZONES[z].layers) {
    const l = document.createElement('div'); l.className = 'layer' + (p === 0 ? ' sky' : '') + (file === 'clouds' ? ' clouds' : '');
    l.style.backgroundImage = `url(${UI(z + '/' + file + '.png')})`; l.style.setProperty('--p', p);
    if (p === 0) { l.style.bottom = ZONES[z].sky[0] + 'px'; l.style.height = ZONES[z].sky[1] + 'px'; }
    el.append(l);
  }
  $('world').append(el);
  return zoneEls[z] = el;
}
let zone = 'A', rotateTimer = 0;
function showZone(z, cam) {
  zone = z; const el = buildZone(z);
  for (const k in zoneEls) zoneEls[k].classList.toggle('on', k === z);
  if (cam !== undefined) el.style.setProperty('--cam', cam + 'px');
  document.body.style.backgroundColor = ZONES[z].top;
  $('cast').style.setProperty('--foot', ZONES[z].foot + 'px');
}
// 스프라이트: idle 4프레임·walk 8프레임, 인게임 FrameAnimator 기본 10fps 와 같은 100ms
const CLIPS = {idle: 4, walk: 8};
for (const g of ['m', 'f']) for (const c in CLIPS) for (let i = 0; i < CLIPS[c]; i++) new Image().src = UI(`${g}/${c}${i}.png`);
let tick = 0;
setInterval(() => {
  tick++;
  for (const a of document.querySelectorAll('.actor')) a.src = UI(`${a.dataset.g}/${a.dataset.clip}${tick % CLIPS[a.dataset.clip]}.png`);
}, 100);
function actor(g, left, big) {
  const a = document.createElement('img'); a.alt = ''; a.dataset.g = g; a.dataset.clip = 'idle';
  a.className = `actor ${g}${big ? ' big' : ''}`; a.style.left = left; a.src = UI(`${g}/idle0.png`);
  return a;
}
const narrow = matchMedia('(max-width: 760px)');
// 장면: login(숲 A, 남녀 둘) · create(숲 A 안쪽, 고른 성별 2배) · lobby(A→B→C 순환, 내 캐릭터 + 이름)
let lastScene = ['login', {}];
narrow.addEventListener('change', () => { const z = zone; scene(...lastScene); if (lastScene[0] === 'lobby') showZone(z, 0); }); // 브레이크포인트가 바뀌면 배우 위치만 다시
function scene(name, opts = {}) {
  clearInterval(rotateTimer); document.body.dataset.scene = name; lastScene = [name, opts];
  const cast = $('cast'); cast.replaceChildren();
  if (name === 'login') {
    showZone('A', 600);
    cast.append(actor('m', narrow.matches ? '18%' : 'max(4vw, 80px)'), actor('f', narrow.matches ? '48%' : 'calc(max(4vw, 80px) + 96px)'));
  } else if (name === 'create') {
    showZone('A', 1700);
    cast.append(actor(opts.g || 'm', narrow.matches ? 'calc(50% - 176px)' : 'calc(34vw - 176px)', true));
  } else if (name === 'lobby') {
    showZone('A', 0);
    const left = narrow.matches ? 'calc(50% - 88px)' : 'max(14vw, 120px)';
    cast.append(actor(opts.g || 'm', left));
    const plate = document.createElement('div'); plate.className = 'nameplate'; plate.textContent = opts.name || ''; plate.style.left = `calc(${left} + 88px)`; cast.append(plate);
    if (!still.matches) { // 항상 A 부터 시작해 15초마다 B → C → A
      const order = ['A', 'B', 'C']; buildZone('B'); buildZone('C');
      rotateTimer = setInterval(() => showZone(order[(order.indexOf(zone) + 1) % 3], 0), 15000);
    }
  }
}
function previewGender(g) {
  const a = $('cast').querySelector('.actor.big'); if (!a) return;
  a.dataset.g = g; a.className = `actor ${g} big`; tick = -1; // 고르면 idle 첫 프레임부터
}
for (const f of [$('auth'), $('google-signup')]) f.addEventListener('change', e => { if (e.target.name === 'gender') previewGender(e.target.value === 'female' ? 'f' : 'm'); });
const title = t => { $('board-title').textContent = t; };
async function api(path, body) {
  const res = await fetch(new URL(path, API), body === undefined ? {cache:'no-store',credentials:'include'} : {method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  const data = res.status === 204 ? {} : await res.json();
  if (!res.ok || data.err) throw new Error(data.err || '요청에 실패했습니다');
  return data;
}
async function showAccount(user) {
  account = user;
  $('auth').hidden = true; $('google').hidden = true; $('ready').hidden = false;
  $('greeting').textContent = `${user.name}, 어느 채널로 갈까요?`; title('채널 선택');
  scene('lobby', {g: user.gender === 'female' ? 'f' : 'm', name: user.name});
  const result = await api('channels');
  const channels = result.channels.length ? result.channels : [{index:result.me,base:'',full:false,draining:false}];
  $('channels').replaceChildren();
  $('channel-list').replaceChildren();
  for (const ch of channels.filter(c => !c.draining)) {
    const option = new Option(`채널 ${ch.index + 1}${ch.full ? ' · 정원 초과' : ''}`, ch.base || '');
    option.disabled = ch.full; $('channels').add(option);
    const b = document.createElement('button'); b.type = 'button'; b.setAttribute('role', 'radio'); b.disabled = ch.full; b.dataset.value = option.value;
    b.innerHTML = `<span></span><small></small>`; b.firstChild.textContent = `채널 ${ch.index + 1}`; b.lastChild.textContent = ch.full ? '정원 초과' : `${ch.players ?? 0}/${result.cap}`;
    b.onclick = () => pickChannel(option.value); $('channel-list').append(b);
  }
  $('play').disabled = ![...$('channels').options].some(o => !o.disabled);
  const available = [...$('channels').options].find(o => !o.disabled);
  if (available) pickChannel(available.value);
}
function pickChannel(value) {
  $('channels').value = value;
  for (const b of $('channel-list').children) { const on = b.dataset.value === value; b.setAttribute('aria-checked', on); b.setAttribute('aria-pressed', on); }
}
// 회원가입은 화면만 2단계(계정 → 캐릭터). 서버 호출은 예전처럼 signup + login 한 번
function signupStep(step) {
  signup = step > 0;
  $('account-fields').hidden = step === 2; $('signup-fields').hidden = step !== 2;
  $('auth').elements.name.required = step === 2;
  $('auth').elements.pw.autocomplete = signup ? 'new-password' : 'current-password';
  $('submit').textContent = step === 1 ? '다음' : step === 2 ? '가입하고 시작' : '로그인';
  $('mode').textContent = signup ? '로그인으로 돌아가기' : '회원가입';
  $('google').hidden = signup;
  title(step === 1 ? '새 모험 시작' : step === 2 ? '캐릭터 만들기' : '숲으로 들어가기');
  $('status').textContent = '';
  scene(step === 2 ? 'create' : 'login', {g: $('auth').elements.gender.value === 'female' ? 'f' : 'm'});
  if (step === 2) $('auth').elements.name.focus();
}
$('mode').onclick = () => signupStep(signup ? 0 : 1);
$('auth').onsubmit = async event => {
  event.preventDefault();
  if (signup && !event.target.elements.name.value) { signupStep(2); return; }
  $('submit').disabled = true; $('status').textContent = '';
  const body = Object.fromEntries(new FormData(event.target));
  try {
    if (signup) await api('account/signup', body);
    await showAccount((await api('account/login', {id:body.id,pw:body.pw})).ok);
    event.target.elements.pw.value = '';
  } catch (e) { $('status').textContent = e.message; }
  finally { $('submit').disabled = false; }
};
$('google').onclick = () => { $('google').disabled = true; $('status').textContent = 'Google 로그인으로 이동하는 중…'; location.href = new URL('auth/google', API).href; };
$('google-signup').onsubmit = async event => {
  event.preventDefault(); $('status').textContent = '';
  try { await showAccount((await api('account/google/finish', Object.fromEntries(new FormData(event.target)))).ok); $('google-signup').hidden = true; history.replaceState(null, '', location.pathname); }
  catch (e) { $('status').textContent = e.message; }
};
$('logout').onclick = async () => { await api('account/logout', {}); location.reload(); };
$('exit').onclick = async () => { if (instance) await instance.Quit(); location.reload(); };
function expandGame() {
  const expanded = $('game').classList.toggle('expanded');
  $('fullscreen').textContent = expanded ? '화면 복원' : '전체 화면';
}
$('fullscreen').onclick = async () => {
  try {
    if (document.fullscreenElement) { await document.exitFullscreen(); return; }
    if ($('game').classList.contains('expanded') || typeof $('game').requestFullscreen !== 'function') {
      expandGame(); return;
    }
    await $('game').requestFullscreen();
  } catch { expandGame(); }
};
document.addEventListener('fullscreenchange', () => {
  $('fullscreen').textContent = document.fullscreenElement ? '화면 복원' : '전체 화면';
});
$('play').onclick = async () => {
  $('play').disabled = true; $('status').textContent = '';
  clearInterval(rotateTimer);
  if (!still.matches) { // 캐릭터가 오른쪽으로 걸어 나가며 어두워진다(0.7초)
    const a = $('cast').querySelector('.actor'); if (a) { a.dataset.clip = 'walk'; a.style.left = '110vw'; }
    $('veil').classList.add('on'); await new Promise(r => setTimeout(r, 700));
  }
  try {
    const current = (await api('account/me')).ok;
    const wsUrl = new URL($('channels').value || '.', API);
    wsUrl.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    window.gameSession = {...current, wsUrl:wsUrl.href};
    const build = await api('web/build.json');
    for (const k in build) build[k] = new URL(build[k].replace(/^\//, ''), API).href; // WebBuild.cs 가 /web/... 절대경로로 쓴다 → 게임 서버 기준으로
    await new Promise((resolve,reject) => {const script = document.createElement('script');script.src=build.loaderUrl;script.onload=resolve;script.onerror=()=>reject(new Error('게임 파일을 불러오지 못했습니다'));document.body.append(script);});
    $('entry').hidden = true; $('game').hidden = false; document.body.classList.add('playing');
    instance = await createUnityInstance($('unity-canvas'), {...build,companyName:'1bit',productName:'시간 낭비의 숲',productVersion:'3.0.13',devicePixelRatio:1}, progress => {$('loading').textContent=`게임 준비 중 ${Math.round(progress*100)}%`;});
    window.gameInstance = instance;
    try { $('chat').hidden = localStorage.chatHidden === '1'; } catch { $('chat').hidden = false; }
    $('loading').textContent = ''; $('unity-canvas').focus();
  } catch (e) { document.body.classList.remove('playing');$('entry').hidden=false;$('game').hidden=true;$('veil').classList.remove('on');scene('lobby',{g:account?.gender==='female'?'f':'m',name:account?.name});$('status').textContent=e.message;$('play').disabled=false; }
};
// HTML 채팅 독(GameUI.cs HtmlChat). 키는 여기서 끊어 Unity 로 안 넘긴다
window.gameChat = {
  toggle() { $('chat').hidden = !$('chat').hidden; try { localStorage.chatHidden = $('chat').hidden ? '1' : ''; } catch {} },
  line(text) {
    const log = $('chat-log'), row = document.createElement('div'); row.textContent = text; log.append(row);
    while (log.childElementCount > 30) log.firstChild.remove();
    log.scrollTop = log.scrollHeight; showLog();
  },
};
// 로그는 새 줄·입력 중에만 보이고 8초 뒤 흐려짐 → 스킬 HUD·전장 가림 최소화
let logTimer;
function showLog() {
  $('chat-log').classList.remove('idle'); clearTimeout(logTimer);
  logTimer = setTimeout(() => { if (document.activeElement !== $('chat-input')) $('chat-log').classList.add('idle'); }, 8000);
}
$('chat-input').addEventListener('focus', showLog);
$('chat-input').addEventListener('blur', showLog);
function sendChat() {
  const text = $('chat-input').value.trim();
  if (!text || !instance) return;
  instance.SendMessage('GameUI', 'OnHtmlChat', text); $('chat-input').value = '';
}
for (const type of ['keydown', 'keyup', 'keypress']) $('chat-input').addEventListener(type, e => e.stopPropagation());
let compositionEnd = -1e9;
$('chat-input').addEventListener('compositionend', () => { compositionEnd = performance.now(); });
$('chat-input').addEventListener('keydown', e => {
  if (e.isComposing || e.keyCode === 229 || performance.now() - compositionEnd < 50) return; // 한글 조합 확정용 Enter(Safari 는 compositionend 직후에 옴)
  if (e.key === 'Enter') { e.preventDefault(); sendChat(); }
  else if (e.key === 'Escape') $('chat-input').blur();
});
$('chat-send').onclick = () => { sendChat(); $('chat-input').focus(); };
$('chat-potion').onclick = () => instance?.SendMessage('GameUI', 'Toggle', 'chat');
scene('login');
api('account/me').then(data => showAccount(data.ok)).catch(() => {
  // Google 콜백이 #google-signup(첫 가입)이나 #google-error 를 붙여 돌려보낸다
  if (location.hash === '#google-signup') { $('auth').hidden = true; $('google').hidden = true; $('google-signup').hidden = false; title('캐릭터 만들기'); scene('create'); }
  else if (location.hash === '#google-error') { $('status').textContent = 'Google 로그인에 실패했습니다. 다시 시도해 주세요'; history.replaceState(null, '', location.pathname); }
});
