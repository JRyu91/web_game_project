// Fullscreen API boundary regression; VM mocks only DOM/platform calls, not the handler.
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
async function scenario(kind) {
 const nodes=new Map(), listeners=new Map(); let requests=0, exits=0;
 const classes=new Set();
 const node=id=>{if(!nodes.has(id))nodes.set(id,{hidden:false,textContent:'',style:{setProperty(){}},classList:{toggle(k){if(classes.has(k)){classes.delete(k);return false;}classes.add(k);return true;},contains:k=>classes.has(k),add:k=>classes.add(k),remove:k=>classes.delete(k)},addEventListener(){},elements:{name:{},pw:{}},options:[]});return nodes.get(id);};
 const document={getElementById:node,documentElement:{style:{setProperty(){}}},addEventListener:(k,f)=>listeners.set(k,f),fullscreenElement:null,exitFullscreen:async()=>{exits++;document.fullscreenElement=null;}};
 if(kind!=='unsupported')node('game').requestFullscreen=async()=>{requests++;if(kind==='rejected')throw Error('permission denied');document.fullscreenElement=node('game');};
 const window={addEventListener(){},innerWidth:393,innerHeight:740,visualViewport:{width:393,height:740,addEventListener(){}}};
 const context={document,window,location:{origin:'https://example.invalid',protocol:'https:',reload(){}},navigator:{},fetch:async()=>({ok:false,status:401,json:async()=>({err:'unauthenticated'})}),console,setTimeout,clearTimeout,URL};
 vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,'launcher.js'),'utf8'),context);
 await node('fullscreen').onclick();
 if(kind==='native') {
  assert.equal(requests,1);assert.equal(document.fullscreenElement,node('game'));assert(!classes.has('expanded'));
  await node('fullscreen').onclick();assert.equal(exits,1);
 } else {
  assert(classes.has('expanded'),kind+' should expand without Fullscreen API');
  assert.equal(node('fullscreen').textContent,'화면 복원');
  await node('fullscreen').onclick();assert(!classes.has('expanded'));assert.equal(node('fullscreen').textContent,'전체 화면');
 }
 console.log('PASS fullscreen '+kind);
}
(async()=>{for(const kind of ['unsupported','rejected','native'])await scenario(kind);})().catch(e=>{console.error(e);process.exitCode=1;});
