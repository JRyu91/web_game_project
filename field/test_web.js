// Run against a disposable headless Chrome profile: node field/test_web.js [http://localhost:18080]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const WebSocket = require('ws');
const base = process.argv.slice(2).find(x=>!x.startsWith('--')) || 'http://localhost:18080';
const mobile=process.argv.includes('--mobile');
const checkPeer=process.argv.includes('--peer');
const checkEquipment=process.argv.includes('--equipment');
assert(!checkEquipment || ['localhost','127.0.0.1'].includes(new URL(base).hostname),'equipment fixture is local-only');
const checkSystems=process.argv.includes('--systems'),systemResponses=new Map();
const checkChat=process.argv.includes('--chat'),chats=[],invReqs=[];
assert(!checkSystems || ['localhost','127.0.0.1'].includes(new URL(base).hostname),'systems fixture is local-only');
assert(!checkPeer || ['localhost','127.0.0.1'].includes(new URL(base).hostname),'peer fixture is local-only');
const stage=new URL(base).host.replace(/[^a-zA-Z0-9_-]/g,'_');
const captureName=`web-${stage}-${process.argv.includes('--female')?'female-':''}${mobile?(process.argv.includes('--short')?'mobile-short':'mobile'):'desktop'}`;
const out = path.resolve(__dirname, '../unity_client/Logs');
const account = 'webqa_' + crypto.randomBytes(6).toString('hex');
const password = crypto.randomBytes(16).toString('hex');
let rewardCapture, ws, peer, peerPresent=false, peerRow, peerInv, seq = 0, kills = 0, joined = 0, latest, cookie;
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
      if(/Exception|\[net\]|\[save\]|\[fps\]|\[fx\]/.test(text)) notes.push(text);
    }
    if(e.method==='Network.webSocketFrameReceived') {
      try {const m=JSON.parse(e.params.response.payloadData);if(m.type==='chat')chats.push(m.text);if(m.type==='inv')invReqs.push(m.req+(m.ok?'':':'+m.code));if(m.roster){peerRow=m.roster.find(p=>p.id===peerId);peerPresent=!!peerRow;}if(m.type==='inv') {if(m.seq>=900001)systemResponses.set(m.seq,m);if(m.req==='join'&&m.ok)joined++;if(m.req==='kill'&&m.ok){kills++;if(!rewardCapture)rewardCapture=pause(200).then(()=>call('Page.captureScreenshot',{format:'png'})).then(r=>{fs.mkdirSync(out,{recursive:true});fs.writeFileSync(path.join(out,captureName+'-drop.png'),Buffer.from(r.data,'base64'));});}if(m.state)latest=m.state;}} catch {}
    }
  });
  await new Promise(resolve=>ws.once('open',resolve));
  await call('Runtime.enable');await call('Network.enable');await call('Page.enable');
  if(checkEquipment&&!mobile) await call('Emulation.setDeviceMetricsOverride',{width:1600,height:1000,deviceScaleFactor:1,mobile:false});
  if(process.argv.includes('--stats')&&!mobile) await call('Emulation.setDeviceMetricsOverride',{width:1280,height:1304,deviceScaleFactor:1,mobile:false});
  const short=process.argv.includes('--short'); // 짧은 가로 모바일
  if(mobile){await call('Emulation.setDeviceMetricsOverride',{width:short?852:393,height:short?393:852,deviceScaleFactor:1,mobile:true});await call('Emulation.setTouchEmulationEnabled',{enabled:true,maxTouchPoints:1});}
  await call('Network.clearBrowserCookies');
  await call('Page.navigate',{url:base});
  await until(()=>evaluate("!!document.getElementById('auth')"),'login form');
  assert.equal(await evaluate('document.title'), '시간 낭비의 숲 · v3.0.10');
  assert.equal((await fetch(base+'/server.js')).status,404,'server source must not be public');
  assert([403,404].includes((await fetch(base+'/web/%2e%2e/server.js')).status),'encoded path traversal');
  const adminPw=process.argv.includes('--admin')&&process.env.ADMIN_PASSWORD; // 로컬 관리자(메모리 서버 시드)
  if(adminPw) await evaluate(`const f=document.getElementById('auth');f.elements.id.value='admin';f.elements.pw.value=${JSON.stringify(adminPw)};f.requestSubmit();`);
  else await evaluate(`document.getElementById('mode').click();const f=document.getElementById('auth');f.elements.id.value=${JSON.stringify(account)};f.elements.pw.value=${JSON.stringify(password)};f.elements.name.value='웹검증${account.slice(-4)}';f.elements.gender.value=${JSON.stringify(process.argv.includes('--female')?'female':'male')};f.requestSubmit();`);
  await until(()=>evaluate("!document.getElementById('ready').hidden"),'signup and login');
  assert.equal(await evaluate("document.cookie.includes('game_session')"),false,'session must be HttpOnly');
  await evaluate("document.getElementById('play').click()");
  await until(()=>evaluate('!!window.gameInstance'),'Unity boot',180000);
  await until(()=>joined>0,'real browser WebSocket join');
  await until(()=>kills>=2,'automatic combat rewards',90000);
  assert(latest.gold>0&&latest.exp>=0&&latest.inv.length>=1);
  await rewardCapture;
  if(adminPw) {
    for(const id of ['admin','allocate','sum']){await evaluate(`gameInstance.SendMessage('GameUI','Show','${id}')`);await pause(800);const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,`${captureName}-${id}.png`),Buffer.from(r.data,'base64'));}
    // --click x,y;x,y : 관리 패널을 연 상태에서 실제 마우스 클릭 → 서버 inv 응답 + 클릭마다 캡처
    const clicks=(process.argv.find(a=>a.startsWith('--click='))||'').slice(8);
    if(clicks){
      await evaluate(`gameInstance.SendMessage('GameUI','Show','${(process.argv.find(a=>a.startsWith('--panel='))||'--panel=admin').slice(8)}')`);await pause(800);
      let n=0;for(const xy of clicks.split(';')){const [x,y]=xy.split(',').map(Number);invReqs.length=0;
        await call('Input.dispatchMouseEvent',{type:'mouseMoved',x,y});await call('Input.dispatchMouseEvent',{type:'mousePressed',x,y,button:'left',clickCount:1});await pause(80);await call('Input.dispatchMouseEvent',{type:'mouseReleased',x,y,button:'left',clickCount:1});
        await pause(Number((process.argv.find(a=>a.startsWith('--clickwait='))||'--clickwait=900').slice(12)));const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,`${captureName}-click${n++}.png`),Buffer.from(r.data,'base64'));
        console.log('click',x,y,'->',JSON.stringify(invReqs),'gold',latest?.gold,'lv',latest?.level,'stones',latest?.stones);}
    }
    const watch=Number((process.argv.find(a=>a.startsWith('--watch='))||'--watch=0').slice(8)); // 패널 닫고 전투 연속 캡처(브레스·타격 연출 확인)
    if(watch){await evaluate("gameInstance.SendMessage('GameUI','Show','')");for(let n=0;n<watch;n++){await pause(400);const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,`${captureName}-watch${n}.png`),Buffer.from(r.data,'base64'));}}
    await evaluate("gameInstance.SendMessage('GameUI','Show','')");
  }
  if(process.argv.includes('--shots')) {
    // 시각 검토용: 전투 중 스킬/피해/드롭 피드백 연속 캡처 + 가방 열린 화면
    for(let n=0;n<8;n++){await pause(700);const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,`${captureName}-fx${n}.png`),Buffer.from(r.data,'base64'));}
    await evaluate("gameInstance.SendMessage('GameUI','Show','inv')");await pause(800);
    const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-bag.png'),Buffer.from(r.data,'base64'));
    await evaluate("gameInstance.SendMessage('GameUI','Show','')");await pause(300);
  }
  if(checkChat) {
    // HTML 채팅 독: Unity 패널을 열어도 유지, 실제 IME 조합(ㅎ→하→한, 글) + 백스페이스 + 조합 중 Enter 무시 + Enter 전송 → 서버 왕복 → 독에 표시
    const visible="(()=>{const c=document.getElementById('chat');return !c.hidden&&c.getBoundingClientRect().height>0;})()";
    assert(await evaluate(visible),'chat dock visible after boot');
    await evaluate("gameInstance.SendMessage('GameUI','Toggle','inv')");await pause(500);
    assert(await evaluate(visible),'chat dock survives Unity panel');
    await evaluate("document.getElementById('chat-input').focus()");
    const key=(type,k,code,extra={})=>call('Input.dispatchKeyEvent',{type,key:k,code,windowsVirtualKeyCode:{Enter:13,Backspace:8}[k]||0,...extra});
    for(const t of ['ㅎ','하','한']) await call('Input.imeSetComposition',{text:t,selectionStart:t.length,selectionEnd:t.length});
    await key('rawKeyDown','Enter','Enter',{windowsVirtualKeyCode:229}); // 조합 중 Enter: 전송 금지
    await call('Input.insertText',{text:'한'});
    for(const t of ['ㄱ','그','글']) await call('Input.imeSetComposition',{text:t,selectionStart:t.length,selectionEnd:t.length});
    await call('Input.insertText',{text:'글'});
    await call('Input.insertText',{text:'x'});await key('rawKeyDown','Backspace','Backspace');await key('keyUp','Backspace','Backspace');
    assert.equal(await evaluate("document.getElementById('chat-input').value"),'한글','IME composition and backspace in input');
    assert.equal(chats.length,0,'Enter during composition must not send');
    await pause(120); // 조합 확정 직후 50ms 가드(Safari) 밖
    await key('rawKeyDown','Enter','Enter');await key('keyUp','Enter','Enter');
    await until(()=>chats.includes('한글'),'chat round trip via server');
    await until(()=>evaluate("[...document.querySelectorAll('#chat-log div')].some(d=>d.textContent.endsWith(': 한글'))"),'chat line in dock');
    assert.equal(await evaluate("document.getElementById('chat-input').value"),'','input cleared after send');
    assert.equal(await evaluate("document.activeElement.id"),'chat-input','focus kept after send');
    await evaluate("gameInstance.SendMessage('GameUI','Toggle','inv')");await pause(600); // 패널 닫힘 → 스킬 HUD 0.2s 틱 반영
    const shot=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-chat.png'),Buffer.from(shot.data,'base64'));
    console.log('chat PASS');
  }
  if(checkSystems) {
    await until(()=>latest.statPoints>0,'natural level-up points',120000);
    async function request(seq,fields){await evaluate(`window.gameSocket.send(${JSON.stringify(JSON.stringify({...fields,seq}))})`);await until(()=>systemResponses.has(seq),'systems ACK');return systemResponses.get(seq);}
    const before=latest;
    const allocated=await request(900001,{type:'allocate_stat',item:'str',qty:1});
    assert.equal(allocated.ok,true);assert.equal(allocated.state.str,before.str+1);
    assert.equal(allocated.state.statPoints,before.statPoints-1+5*(allocated.state.level-before.level));
    const rejected=await request(900002,{type:'allocate_stat',item:'str',qty:496});assert.equal(rejected.ok,false);assert.equal(rejected.state.str,allocated.state.str);
    const dismantle=await request(900003,{type:'auto_disassemble',enabled:true,level:0});assert(dismantle.ok&&dismantle.state.autoDisassemble&&!dismantle.state.autoSell);
    const selling=await request(900004,{type:'auto_sell',enabled:true,level:0});assert(selling.ok&&selling.state.autoSell&&!selling.state.autoDisassemble);
    console.log('PASS real WebGL natural level-up -> allocation/rejection and exclusive automatic policies');
  }
  let restoredWear;
  if(checkEquipment) {
    assert(latest.level===100&&latest.inv.length>=116&&latest.invCap===200&&!latest.pendingDrop,'run against disposable equipment fixture with drop space');
    let equipmentSeq=910000;
    async function equip(slot,uid) {
      const seq=++equipmentSeq;
      await evaluate(`window.gameSocket.send(${JSON.stringify(JSON.stringify({type:'equip',slot,uid,seq}))})`);
      await until(()=>systemResponses.has(seq),'equipment ACK');
      const r=systemResponses.get(seq);assert.equal(r.ok,true,r.code);assert.equal(r.state.equip[slot],uid);
    }
    async function shot(tag,settle=400){if(settle)await pause(settle);const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-equipment-'+tag+'.png'),Buffer.from(r.data,'base64'));}
    const owned=latest.inv.slice(), weapon=(kind,tier)=>owned.find(i=>i.slot==='weapon'&&i.kind===kind&&i.tier===tier);
    await shot('before');
    if(!mobile) {
      await evaluate("gameInstance.SendMessage('GameUI','Show','inv')");await pause(600);
      const b=await evaluate("(()=>{const c=document.getElementById('unity-canvas'),r=c.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,rw:c.width,rh:c.height};})()");
      const z=Math.max(1,Math.min(Math.floor(b.rw/1280),Math.floor(b.rh/720))),w=b.rw/z,h=b.rh/z;
      assert(w>=1000&&h>=640,'desktop inventory layout');
      async function click(x,y){const p={x:b.x+x*z*b.w/b.rw,y:b.y+y*z*b.h/b.rh};await call('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...p});await pause(100);await call('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...p});await pause(300);}
      const left=w/2-300,top=h/2-310,helmet=latest.equip.helmet;
      await click(left+160,top+96); // second equipped row is helmet
      await click(left+356,top+274);
      await until(()=>latest.equip.helmet===0,'actual inventory mouse unequip');await shot('ui-unequip');
      await click(left+356,top+274);
      await until(()=>latest.equip.helmet===helmet,'actual inventory mouse re-equip');await shot('ui-equip');
      await evaluate("gameInstance.SendMessage('GameUI','Show','')");
      console.log('PASS actual Unity inventory mouse selection -> unequip/re-equip server ACK');
    }
    await equip('helmet',0);await equip('armor',0);await shot('bare');
    for(const item of owned.filter(i=>i.slot==='weapon'))await equip('weapon',item.uid);
    for(const slot of ['helmet','armor'])for(const item of owned.filter(i=>i.slot===slot)) {
      await equip('weapon',weapon(item.tier>=21?'staff':'sword',20).uid);await equip(slot,item.uid);
    }
    await equip('weapon',weapon('sword',20).uid);
    for(const slot of ['helmet','armor'])await equip(slot,owned.find(i=>i.slot===slot&&i.tier===20).uid);
    await shot('warrior');
    if(process.argv.includes('--shots'))for(let n=0;n<24;n++){await pause(40);await shot('warrior-motion'+n,0);}
    await equip('weapon',weapon('staff',20).uid);assert.equal(latest.equip.helmet,0);assert.equal(latest.equip.armor,0);
    for(const slot of ['helmet','armor'])await equip(slot,owned.find(i=>i.slot===slot&&i.tier===36).uid);
    await shot('mage');
    if(process.argv.includes('--shots'))for(let n=0;n<24;n++){await pause(40);await shot('mage-motion'+n,0);}
    restoredWear={...latest.equip};
    console.log('PASS real WebGL all 116 equipment items ACK, slot unequip, warrior/mage incompatibility clear, low/high rendered captures');
  }
  const before={gold:latest.gold,level:latest.level};
  // Pausing transport freezes gameplay; reload must restore persisted server progression.
  await evaluate("window.gameSocket.close()");
  await pause(1000);
  await call('Page.reload',{ignoreCache:true});
  await until(()=>evaluate("!!document.getElementById('ready')&&!document.getElementById('ready').hidden"),'session after reload');
  await evaluate("document.getElementById('play').click()");
  await until(()=>joined>=2,'rejoin',180000);
  assert(latest.gold>=before.gold&&latest.level>=before.level,'progress retained after reload');
  if(checkEquipment){assert.deepEqual(latest.equip,restoredWear,'equipment restored after actual browser reload');console.log('PASS WebGL equipment reload persistence');}
  if(checkSystems)assert(latest.str>=1&&latest.autoSell&&!latest.autoDisassemble,'allocated stats and automatic policy retained after reload');
  fs.mkdirSync(out,{recursive:true});
  if(checkPeer){
    peer=new WebSocket(base.replace(/^http/,'ws'));
    await new Promise((resolve,reject)=>{peer.once('error',reject);peer.once('open',()=>peer.send(JSON.stringify({type:'join',id:peerId,name:'퇴장검증',gender:'male'})));peer.on('message',data=>{const m=JSON.parse(data);if(m.type==='inv'&&m.req==='join'&&m.ok){peerInv=m.state;resolve();}});});
    await until(()=>peerPresent,'remote roster arrival');
    peer.send(JSON.stringify({type:'pos',x:12,face:-1}));await pause(1200);
    const shot=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-peer.png'),Buffer.from(shot.data,'base64'));
    if(checkEquipment) {
      assert.equal(peerRow.equip.helmet.tier,20);assert.equal(peerRow.equip.armor.tier,20);
      for(const slot of ['helmet','armor'])peer.send(JSON.stringify({type:'equip',slot,uid:0}));
      await until(()=>peerRow&&!peerRow.equip.helmet&&!peerRow.equip.armor,'real remote wear unequip');await pause(500);
      let r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-peer-bare.png'),Buffer.from(r.data,'base64'));
      for(const slot of ['helmet','armor'])peer.send(JSON.stringify({type:'equip',slot,uid:peerInv.equip[slot]}));
      await until(()=>peerRow?.equip.helmet?.tier===20&&peerRow?.equip.armor?.tier===20,'real remote wear restore');await pause(500);
      r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-peer-wear.png'),Buffer.from(r.data,'base64'));
      console.log('PASS real remote initial wear -> immediate unequip/restore roster and rendered captures');
    }
    peer.close();await until(()=>!peerPresent,'remote roster removal');await pause(500);
    console.log('PASS real peer join/position/leave; peer screenshot and final screenshot require visual review');
  }
  await pause(1500);
  const screenshot=await call('Page.captureScreenshot',{format:'png'});
  fs.writeFileSync(path.join(out,captureName+'.png'),Buffer.from(screenshot.data,'base64'));
  if(process.argv.includes('--stats')) {
    const bounds=await evaluate("(()=>{const c=document.getElementById('unity-canvas'),r=c.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,rw:c.width,rh:c.height};})()");
    const zoom=Math.max(1,Math.min(Math.floor(bounds.rw/1280),Math.floor(bounds.rh/720)));
    const width=bounds.rw/zoom,height=bounds.rh/zoom,compact=mobile||width<1000||height<640;
    const point=(x,y)=>({x:bounds.x+x*zoom*bounds.width/bounds.rw,y:bounds.y+y*zoom*bounds.height/bounds.rh});
    async function click(p) {
      if(mobile){await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[p]});await pause(100);await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});}
      else{await call('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...p});await call('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...p});}
      await pause(500);
    }
    const landscape=width>=720&&height<480,left=landscape?220:10,top=landscape?32:116;
    const columns=Math.max(1,Math.min(adminPw?7:6,Math.floor((width-left-10)/(landscape?80:120))));
    await click(compact?point(left+(4%columns+.5)*(width-left-10)/columns,top+Math.floor(4/columns)*48):point(width-125,25));
    const shot=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-stats.png'),Buffer.from(shot.data,'base64'));
    if(mobile){
      const start=point(width/2,height/2+100);
      for(let drag=0;drag<2;drag++){
        await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[start]});
        for(let n=1;n<=8;n++){await call('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:start.x,y:start.y-n*50}]});await pause(40);}
        await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await pause(400);
      }
      const shot=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-stats-scroll.png'),Buffer.from(shot.data,'base64'));
    }
    const pw=compact?Math.min(width-20,620):640,ph=compact?Math.min(height-20,620):640;
    await click(point(width/2+pw/2-(compact?34:29),height/2-(compact?10:0)-ph/2+(compact?34:27)));
    const closed=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-stats-closed.png'),Buffer.from(closed.data,'base64'));
    console.log('Stats open/scroll/close injected; screenshots require visual review.');
  }
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
    assert(bounds.width>=300&&bounds.height>(short?200:300),'usable mobile render surface');
    const point=(x,y)=>({x:bounds.x+x*bounds.width/bounds.renderWidth,y:bounds.y+y*bounds.height/bounds.renderHeight});
    const landscapeMenu=bounds.renderWidth>=720&&bounds.renderHeight<480,left=landscapeMenu?220:10,top=landscapeMenu?32:116;
    const columns=Math.max(1,Math.min(adminPw?7:6,Math.floor((bounds.renderWidth-left-10)/(landscapeMenu?80:120))));
    const touch=point(left+(bounds.renderWidth-left-10)/columns/2,top);
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
    if(checkEquipment){
      await call('Emulation.setDeviceMetricsOverride',{width:720,height:393,deviceScaleFactor:1,mobile:true});await pause(1500);
      const fitted=await evaluate("(()=>{const r=document.getElementById('unity-canvas').getBoundingClientRect();return r.x>=-1&&r.right<=innerWidth+1&&r.bottom<=innerHeight+1})()");
      assert(fitted,'720px landscape canvas fits viewport');
      const r=await call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,captureName+'-landscape720.png'),Buffer.from(r.data,'base64'));
    }
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
