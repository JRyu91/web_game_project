// Uses the project's disposable Chrome CDP session on 9223; opens only a local review file.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const WebSocket = require('ws');
const dir = path.resolve(__dirname, '../unity_review/stage3/team_v6/f_round/code/final');
const pending = new Map(), errors = [];
let socket, seq = 0;
function call(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++seq;
    const timeout = setTimeout(() => { pending.delete(id); reject(Error('CDP timeout ' + method)); }, 15000);
    pending.set(id, { resolve: v => { clearTimeout(timeout); resolve(v); }, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
}
(async () => {
  const tab = await (await fetch('http://localhost:9223/json/new?about:blank', { method: 'PUT' })).json();
  socket = new WebSocket(tab.webSocketDebuggerUrl);
  socket.on('message', raw => {
    const r = JSON.parse(raw);
    if (r.id) { const p = pending.get(r.id); pending.delete(r.id); r.error ? p.reject(Error(r.error.message)) : p.resolve(r.result); }
    if (r.method === 'Runtime.exceptionThrown') errors.push(r.params.exceptionDetails.exception?.description || r.params.exceptionDetails.text);
  });
  await new Promise(resolve => socket.once('open', resolve));
  await call('Runtime.enable'); await call('Page.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: 1240, height: 1100, deviceScaleFactor: 1, mobile: false });
  await call('Page.navigate', { url: 'file://' + path.join(dir, 'male-equipment-preview.html') });
  assert.equal(await evaluate(`new Promise((resolve,reject)=>{let n=0;const timer=setInterval(()=>{if(cards.length===6){clearInterval(timer);resolve(cards.length)}else if(++n>100){clearInterval(timer);reject(Error('page load timeout'))}},100)})`), 6);
  assert.equal(await evaluate('DATA.examples.length'), 6, 'Final preview needs six Unity-validated combinations');
  assert.equal(await evaluate('DATA.captures.length'), 6);
  await evaluate('Promise.all([...document.querySelectorAll("img")].map(img=>img.decode()))');
  const coverage = await evaluate(`(()=>{paused=true;const original=cards.map(c=>({h:c.helmets,a:c.armors,cls:c.cls,clip:c.clip}));let count=0;
    for(const c of cards)for(let clip=0;clip<DATA.clips.length;clip++){c.clip=clip;for(let f=0;f<DATA.clips[clip].frames.length;f++){c.f=f;draw(c);if(!c.canvas.getContext('2d').getImageData(0,0,148,148).data.some((a,i)=>i%4===3&&a))throw Error('empty frame');count++}}
    cards.forEach((c,i)=>{Object.assign(c,{helmets:original[i].h,armors:original[i].a,cls:original[i].cls,clip:original[i].clip,f:0,t:0});draw(c)});return count})()`);
  assert.equal(coverage, 210);
  assert(await evaluate(`cards.every(c=>G.helmets[c.helmets].level!==G.armors[c.armors].level&&(G.helmets[c.helmets].cls==='common'||G.helmets[c.helmets].cls===c.cls)&&(G.armors[c.armors].cls==='common'||G.armors[c.armors].cls===c.cls))`));
  assert(await evaluate(`(()=>{const c=cards[0],cls=c.cls,ss=document.querySelectorAll('.card')[0].querySelectorAll('select');for(let i=0;i<2;i++){ss[i].value=-1;ss[i].dispatchEvent(new Event('change'))}return c.helmets===-1&&c.armors===-1&&c.cls===cls})()`));
  assert(await evaluate(`(()=>{const c=cards[0],ss=document.querySelectorAll('.card')[0].querySelectorAll('select');
    ss[1].value=available('armors').find(i=>G.armors[i].cls==='mage');ss[1].dispatchEvent(new Event('change'));
    for(let i=0;i<2;i++){ss[i].value=-1;ss[i].dispatchEvent(new Event('change'))}return c.helmets===-1&&c.armors===-1&&c.cls==='mage'})()`));
  assert(await evaluate(`(()=>{const saved=requestAnimationFrame;requestAnimationFrame=()=>0;try{
    const c=cards[0];c.clip=DATA.clips.findIndex(c=>c.key==='main_m/dead');c.f=DATA.clips[c.clip].frames.length-1;c.t=0;paused=false;last=1000;
    tick(1200);tick(1400);tick(1600);if(c.f!==DATA.clips[c.clip].frames.length-1)return false;
    tick(1700);return c.f===0;
    }finally{paused=true;last=0;requestAnimationFrame=saved}})()`));
  await evaluate('fill(true);paused=true');
  assert(await evaluate(`(()=>{const before=cards.map(c=>c.f);document.getElementById('step').click();return cards.every((c,i)=>c.f===(before[i]+1)%DATA.clips[c.clip].frames.length)})()`));
  await evaluate('fill(true);paused=true');
  const shot = await call('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(dir, 'male-preview-desktop.png'), Buffer.from(shot.data, 'base64'));
  await call('Emulation.setDeviceMetricsOverride', { width: 393, height: 852, deviceScaleFactor: 1, mobile: true });
  assert(await evaluate('document.documentElement.scrollWidth<=innerWidth'));
  await evaluate('document.getElementById("size").click()');
  assert(await evaluate('document.documentElement.scrollWidth<=innerWidth'));
  await evaluate('document.getElementById("shuffle").click()');
  assert.equal(await evaluate('cards.length'), 6);
  assert.deepEqual(errors, []);
  console.log('PASS male preview: 6 real atlas examples, all 35 poses x6 nonempty, independent unequip/mage class, nonloop replay pause, stepping, shuffle, mobile/4x overflow, JS errors0; pixel alignment/trail quality needs visual review');
  socket.close();
})().catch(e => { console.error(e); socket?.close(); process.exitCode = 1; });
