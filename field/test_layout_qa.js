// PM runs with its existing headless Chrome: node field/test_layout_qa.js [base URL]
// Exercises actual launcher CSS, not Unity rendering or iPhone Safari.
const assert=require('node:assert/strict');
const WebSocket=require('ws');
const base=process.argv[2]||'http://localhost:18080';
let socket,seq=0;const pending=new Map(),errors=[];
function call(method,params={}){return new Promise((resolve,reject)=>{const id=++seq;const timeout=setTimeout(()=>{pending.delete(id);reject(Error(method+' timeout'));},10000);pending.set(id,{resolve:v=>{clearTimeout(timeout);resolve(v);},reject:e=>{clearTimeout(timeout);reject(e);}});socket.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true});if(r.exceptionDetails)throw Error(r.exceptionDetails.text);return r.result.value;}
(async()=>{
 const page=await(await fetch('http://localhost:9223/json/new?about:blank',{method:'PUT'})).json();socket=new WebSocket(page.webSocketDebuggerUrl);
 socket.on('message',data=>{const r=JSON.parse(data);if(r.method==='Runtime.exceptionThrown')errors.push(r.params.exceptionDetails.text);if(r.method==='Page.javascriptDialogOpening'){errors.push(r.params.message);call('Page.handleJavaScriptDialog',{accept:true}).catch(()=>{});}if(r.id){const p=pending.get(r.id);if(!p)return;pending.delete(r.id);r.error?p.reject(Error(r.error.message)):p.resolve(r.result);}});
 await new Promise(r=>socket.once('open',r));await call('Page.enable');await call('Runtime.enable');
 for(const [width,height,mobile] of [[1280,800,false],[393,852,true],[852,393,true]]){
  await call('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile});
  await call('Page.navigate',{url:base});
  for(let n=0;n<100;n++){if(await evaluate("document.readyState==='complete' && !!document.getElementById('game') && document.styleSheets.length>0"))break;await new Promise(r=>setTimeout(r,50));}
  await evaluate("document.getElementById('entry').hidden=true;document.getElementById('game').hidden=false;document.body.classList.add('playing');document.getElementById('loading').textContent='';");
  for(const expanded of [false,true]){
   await evaluate(`document.getElementById('game').classList.toggle('expanded',${expanded});`);
   const dimensions=await evaluate(`(()=>{const rect=id=>{const r=document.getElementById(id).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,bottom:r.bottom,right:r.right};};return {canvas:rect('unity-canvas'),toolbar:rect('toolbar'),game:rect('game'),touch:getComputedStyle(document.getElementById('unity-canvas')).touchAction,width:innerWidth,height:innerHeight,overflow:document.documentElement.scrollWidth>innerWidth};})()`);
   assert.equal(dimensions.touch,'none');assert.equal(dimensions.overflow,false,'horizontal page overflow');
   assert(dimensions.canvas.height>100,'usable render height');
   assert(dimensions.canvas.x>=-1&&dimensions.canvas.right<=dimensions.width+1,'canvas horizontal fit');
   assert(dimensions.canvas.y>=dimensions.toolbar.bottom-1,'toolbar overlaps canvas');
   assert(dimensions.canvas.bottom<=dimensions.height+1,'canvas exceeds available height');
  }
  console.log(`PASS launcher CSS ${width}x${height} normal/expanded, touch-action and viewport bounds`);
  await evaluate("document.getElementById('game').classList.remove('expanded');");
  for(const unsupported of [true,false]){
   await evaluate(`Object.defineProperty(document.getElementById('game'),'requestFullscreen',{configurable:true,value:${unsupported?'undefined':"()=>Promise.reject(Error('browser denied fullscreen'))"}});document.getElementById('fullscreen').click();`);
   await new Promise(r=>setTimeout(r,100));
   assert(await evaluate("document.getElementById('game').classList.contains('expanded')"),'real click should safely expand');
   await evaluate("document.getElementById('fullscreen').click();");await new Promise(r=>setTimeout(r,100));
   assert.equal(await evaluate("document.getElementById('game').classList.contains('expanded')"),false);
  }
  assert.deepEqual(errors,[],'fullscreen fallback must not trigger uncaught exceptions or Unity error dialogs');
  console.log('PASS actual browser fullscreen unavailable/rejected click, no exception/dialog');
 }
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(async()=>{if(socket?.readyState===1){await call('Page.close').catch(()=>{});socket.close();}});
