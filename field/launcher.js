'use strict';
const $ = id => document.getElementById(id);
let signup = false, account = null, instance = null;
async function api(path, body) {
  const res = await fetch(path, body === undefined ? {cache:'no-store'} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  const data = res.status === 204 ? {} : await res.json();
  if (!res.ok || data.err) throw new Error(data.err || '요청에 실패했습니다');
  return data;
}
async function showAccount(user) {
  account = user;
  $('auth').hidden = true; $('ready').hidden = false;
  $('greeting').textContent = `${user.name}, 모험을 시작하세요`;
  const result = await api('/channels');
  const channels = result.channels.length ? result.channels : [{index:result.me,base:'',full:false,draining:false}];
  $('channels').replaceChildren();
  for (const ch of channels.filter(c => !c.draining)) {
    const option = new Option(`채널 ${ch.index + 1}${ch.full ? ' · 정원 초과' : ''}`, ch.base || '');
    option.disabled = ch.full; $('channels').add(option);
  }
  $('play').disabled = ![...$('channels').options].some(o => !o.disabled);
  const available = [...$('channels').options].find(o => !o.disabled);
  if (available) $('channels').value = available.value;
}
$('mode').onclick = () => {
  signup = !signup;
  $('signup-fields').hidden = !signup;
  $('auth').elements.name.required = signup;
  $('auth').elements.pw.autocomplete = signup ? 'new-password' : 'current-password';
  $('submit').textContent = signup ? '가입하고 시작' : '로그인';
  $('mode').textContent = signup ? '로그인으로 돌아가기' : '회원가입';
};
$('auth').onsubmit = async event => {
  event.preventDefault(); $('submit').disabled = true; $('status').textContent = '';
  const body = Object.fromEntries(new FormData(event.target));
  try {
    if (signup) await api('/account/signup', body);
    await showAccount((await api('/account/login', {id:body.id,pw:body.pw})).ok);
    event.target.elements.pw.value = '';
  } catch (e) { $('status').textContent = e.message; }
  finally { $('submit').disabled = false; }
};
$('logout').onclick = async () => { await api('/account/logout', {}); location.reload(); };
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
  try {
    const current = (await api('/account/me')).ok;
    const wsUrl = new URL($('channels').value || '/', location.origin);
    wsUrl.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    window.gameSession = {...current, wsUrl:wsUrl.href};
    const build = await api('/web/build.json');
    await new Promise((resolve,reject) => {const script = document.createElement('script');script.src=build.loaderUrl;script.onload=resolve;script.onerror=()=>reject(new Error('게임 파일을 불러오지 못했습니다'));document.body.append(script);});
    $('entry').hidden = true; $('game').hidden = false; document.body.classList.add('playing');
    instance = await createUnityInstance($('unity-canvas'), {...build,companyName:'1bit',productName:'시간 낭비의 숲',productVersion:'3.0.1',devicePixelRatio:1}, progress => {$('loading').textContent=`게임 준비 중 ${Math.round(progress*100)}%`;});
    window.gameInstance = instance;
    $('loading').textContent = ''; $('unity-canvas').focus();
  } catch (e) { document.body.classList.remove('playing');$('entry').hidden=false;$('game').hidden=true;$('status').textContent=e.message;$('play').disabled=false; }
};
api('/account/me').then(data => showAccount(data.ok)).catch(() => {});
