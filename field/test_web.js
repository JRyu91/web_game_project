// Run against a disposable headless Chrome profile: node field/test_web.js [http://localhost:18080]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const WebSocket = require('ws');
const base = process.argv.slice(2).find(x=>!x.startsWith('--')) || 'http://localhost:18080';
const mobile=process.argv.includes('--mobile');
const checkPeer=process.argv.includes('--peer');
assert(!checkPeer || ['localhost','127.0.0.1'].includes(new URL(base).hostname),'peer fixture is local-only');
const stage=new URL(base).host.replace(/[^a-zA-Z0-9_-]/g,'_');
const captureName=`web-${stage}-${mobile?'mobile':'desktop'}`;
const out = path.resolve(__dirname, '../unity_client/Logs');
const account = 'webqa_' + crypto.randomBytes(6).toString('hex');
const password = crypto.randomBytes(16).toString('hex');
let rewardCapture, ws, peer, peerPresent=false, seq = 0, kills = 0, joined = 0, latest, cookie;
const peerId='peer_'+crypto.randomBytes(6).toString('hex');
const pending = new Map(), errors = [], notes = [];
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn, label, timeout = 60000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if(errors.length) throw Error(errors[0]); if (await fn()) return; await pause(250); }
  throw Error('Timeout: ' + label);
}
function call(method, params = {}) {
  return new Promise((resolve,reject) => { const id = ++seq; const timeout = setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout: '+method));},15000);pending.set(id,{resolve:v=>{clearTimeout(timeout);resolve(v);},reject:e=>{clearTimeout(timeout);reject(e);}});ws.send(JSON.stringify({id,method,params})); });
}
async function evaluate(expression) {
  const r = await call('Runtime.evaluate', {expression,returnByValue:true,awaitPromise:true});
  if (r.exceptionDetails) throw Error(r.exceptionDetails.text + ': ' + r.exceptionDetails.exception?.description);
  return r.result.value;
}
(async () => {
  const page = await (await fetch('http://localhost:9223/json/new?about:blank',{method:'PUT'})).json();
  ws = new WebSocket(page.webSocketDebuggerUrl);
  ws.on('message', raw => {
    const e=JSON.parse(raw);
    if (e.id) {const p=pending.get(e.id);pending.delete(e.id);e.error?p.reject(Error(e.error.message)):p.resolve(e.result);return;}
    if(e.method==='Page.javascriptDialogOpening') {errors.push(e.params.message);call('Page.handleJavaScriptDialog',{accept:true}).catch(()=>{});}
    if(e.method==='Runtime.exceptionThrown') errors.push(e.params.exceptionDetails.exception?.description || e.params.exceptionDetails.text);
    if(e.method==='Runtime.consoleAPICalled') {
      const text=e.params.args.map(a=>a.value||a.description||'').join(' ');
      if(e.params.type==='error') {errors.push(text);console.error(text.slice(0,1600));}
      if(/Exception|\[net\]|\[save\]|\[fps\]/.test(text)) notes.push(text);
    }
    if(e.method==='Network.webSocketFrameReceived') {
      try {const m=JSON.parse(e.params.response.payloadData);if(m.roster)peerPresent=m.roster.some(p=>p.id===peerId);if(m.type==='inv') {if(m.req==='join'&&m.ok)joined++;if(m.req==='kill'&&m.ok){kills++;if(!rewardCapture)rewardCapture=pause(200).then(()=>call('Page.captureScreenshot',{format:'png'})).then(r=>{fs.mkdirSync(out,{recursive:true});fs.writeFileSync(path.join(out,captureName+'-drop.png'),Buffer.from(r.data,'base64'));});}if(m.state)latest=m.state;}} catch {}
    }
  });
  await new Promise(resolve=>ws.once('open',resolve));
  await call('Runtime.enable');await call('Network.enable');await call('Page.enable');
  if(mobile){await call('Emulation.setDeviceMetricsOverride',{width:393,height:852,deviceScaleFactor:1,mobile:true});await call('Emulation.setTouchEmulationEnabled',{enabled:true,maxTouchPoints:1});}
  await call('Network.clearBrowserCookies');
  await call('Page.navigate',{url:base});
  await until(()=>evaluate("!!document.getElementById('auth')"),'login form');
  assert.equal(await evaluate('document.title'), '시간 낭비의 숲 · v3.0.4');
  assert.equal((await fetch(base+'/server.js')).status,404,'server source must not be public');
  assert([403,404].includes((await fetch(base+'/web/%2e%2e/server.js')).status),'encoded path traversal');
  await evaluate(`document.getElementById('mode').click();const f=document.getElementById('auth');f.elements.id.value=${JSON.stringify(account)};f.elements.pw.value=${JSON.stringify(password)};f.elements.name.value='웹검증${account.slice(-4)}';f.requestSubmit();`);
  await until(()=>evaluate("!document.getElementById('ready').hidden"),'signup and login');
  assert.equal(await evaluate("document.cookie.includes('game_session')"),false,'session must be HttpOnly');
  await evaluate("document.getElementById('play').click()");
  await until(()=>evaluate('!!window.gameInstance'),'Unity boot',180000);
  await until(()=>joined>0,'real browser WebSocket join');
  await until(()=>kills>=2,'automatic combat rewards',90000);
  assert(latest.gold>0&&latest.exp>=0&&latest.inv.length>=1);
  await rewardCapture;
  const before={gold:latest.gold,level:latest.level};
  // Pausing transport freezes gameplay; reload must restore persisted server progression.
  await evaluate("window.gameSocket.close()");
  await pause(1000);
  await call('Page.reload',{ignoreCache:true});
  await until(()=>evaluate("!!document.getElementById('ready')&&!document.getElementById('ready').hidden"),'session after reload');
  await evaluate("document.getElementById('play').click()");
  await until(()=>joined>=2,'rejoin',180000);
  assert(latest.gold>=before.gold&&latest.level>=before.level,'progress retained after reload');
  fs.mkdirSync(out,{recursive:true});
  if(checkPeer){
    peer=new WebSocket(base.replace(/^http/,'ws'));
    await new Promise((resolve,reject)=>{peer.once('error',reject);peer.once('open',()=>peer.send(JSON.stringify({type:'join',id:peerId,name:'퇴장검증',gender:'female'})));peer.on('message',data=>{const m=JSON.parse(data);if(m.type==='inv'&&m.req==='join'&&m.ok)resolve();});});
    await until(()=>peerPresent,'remote roster arrival');
    peer.send(JSON.stringify({type:'pos',x:12,face:-1}));await pause(1200);
    const shot=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-peer.png'),Buffer.from(shot.data,'base64'));
    peer.close();await until(()=>!peerPresent,'remote roster removal');await pause(500);
    console.log('PASS real peer join/position/leave; peer screenshot and final screenshot require visual review');
  }
  await pause(1500);
  const screenshot=await call('Page.captureScreenshot',{format:'png'});
  fs.writeFileSync(path.join(out,captureName+'.png'),Buffer.from(screenshot.data,'base64'));
  if(mobile){
    // Same running Unity instance: iPhone's unavailable API and a browser rejection must both fall back safely.
    for(const unsupported of [true,false]){
      await evaluate(`Object.defineProperty(document.getElementById('game'),'requestFullscreen',{configurable:true,value:${unsupported?'undefined':"()=>Promise.reject(Error('browser denied fullscreen'))"}});document.getElementById('fullscreen').click();`);
      await until(()=>evaluate("document.getElementById('game').classList.contains('expanded')"),'safe fullscreen fallback');
      assert(await evaluate('!!window.gameInstance'),'fullscreen preserves Unity instance');
      await evaluate("document.getElementById('fullscreen').click();");
      await until(()=>evaluate("!document.getElementById('game').classList.contains('expanded')"),'fullscreen restore');
      assert.equal(errors.length,0,'fullscreen fallback must not throw or show Unity error dialog');
    }
    await evaluate("delete document.getElementById('game').requestFullscreen;");
    await pause(300); // Allow Unity's render surface to settle after viewport resize.
    const bounds=await evaluate("(()=>{const c=document.getElementById('unity-canvas'),r=c.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,renderWidth:c.width,renderHeight:c.height};})()");
    assert(Math.abs(bounds.renderWidth/bounds.renderHeight-bounds.width/bounds.height)<0.03,'Unity render aspect must track display aspect');
    assert(bounds.width>=300&&bounds.height>300,'usable mobile render surface');
    const point=(x,y)=>({x:bounds.x+x*bounds.width/bounds.renderWidth,y:bounds.y+y*bounds.height/bounds.renderHeight});
    const columns=Math.max(1,Math.min(7,Math.floor((bounds.renderWidth-20)/120)));
    const touch=point(10+(bounds.renderWidth-20)/columns/2,116);
    await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[touch]});await pause(100);
    await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await pause(1000);
    const menu=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-touch-menu.png'),Buffer.from(menu.data,'base64'));
    const start=point(bounds.renderWidth/2,bounds.renderHeight/2+160);
    await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[start]});
    for(let n=1;n<=5;n++){await call('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:start.x,y:start.y-n*40}]});await pause(40);}
    await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await pause(500);
    const scrolled=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-touch-scroll.png'),Buffer.from(scrolled.data,'base64'));
    const panelWidth=Math.min(bounds.renderWidth-20,620),panelHeight=Math.min(bounds.renderHeight-20,620);
    const close=point(bounds.renderWidth/2+panelWidth/2-34,bounds.renderHeight/2-10-panelHeight/2+34);
    await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[close]});await pause(100);
    await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await pause(300);
    const closed=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-touch-closed.png'),Buffer.from(closed.data,'base64'));
    await call('Emulation.setDeviceMetricsOverride',{width:852,height:393,deviceScaleFactor:1,mobile:true});
    await pause(1500);
    const landscape=await evaluate("(()=>{const c=document.getElementById('unity-canvas'),r=c.getBoundingClientRect(),t=document.getElementById('toolbar').getBoundingClientRect();return {x:r.x,y:r.y,right:r.right,bottom:r.bottom,width:r.width,height:r.height,renderWidth:c.width,renderHeight:c.height,viewportWidth:innerWidth,viewportHeight:innerHeight,toolbarBottom:t.bottom};})()");
    assert(Math.abs(landscape.renderWidth/landscape.renderHeight-landscape.width/landscape.height)<0.03,'orientation updates Unity render aspect');
    assert(landscape.x>=-1&&landscape.right<=landscape.viewportWidth+1&&landscape.y>=landscape.toolbarBottom-1&&landscape.bottom<=landscape.viewportHeight+1,'landscape canvas fits toolbar and viewport');
    const rotated=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-landscape.png'),Buffer.from(rotated.data,'base64'));
    console.log('Mobile menu tap, scroll and close injected; screenshots require visual review. Chromium emulation is not Safari.');
  }
  assert.equal(errors.length,0,errors.join('\n'));
  console.log(JSON.stringify({result:'PASS',base,mobile,captureName,joins:joined,kills,gold:latest.gold,level:latest.level,errors,notes:notes.slice(-10)}));
})().catch(e=>{console.error(e.stack);console.error(JSON.stringify({errors,notes:notes.slice(-15),joined,kills}));process.exitCode=1;}).finally(async()=>{
  peer?.close();
  if(ws?.readyState===WebSocket.OPEN){
    try {await evaluate("window.gameSocket?.close()");await evaluate(`fetch('/account/remove',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:${JSON.stringify(account)}})})`);await call('Page.close');}catch{}
    ws.close();
  }
});
