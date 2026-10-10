// Seed owned common gear only in an isolated server fixture; use real handlers and inventory rules.
const assert=require('node:assert/strict'),{spawn}=require('node:child_process'),WS=require('ws');
const base='127.0.0.1:18394', child=spawn(process.execPath,['-e',"const I=require('./items'),N=I.normalize;I.normalize=s=>N(s?.inv?s:{...s,level:10,helmetTier:1,armorTier:1});require('./server')"],{cwd:__dirname,env:{...process.env,PORT:'18394',DATABASE_URL:'',REDIS_URL:'',LOCAL_ADMIN:''},stdio:'ignore'});
const sockets=[],sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function open(id){const ws=new WS('ws://'+base),msgs=[];sockets.push(ws);ws.on('message',v=>msgs.push(JSON.parse(v)));await new Promise((r,j)=>{ws.once('open',r);ws.once('error',j)});
 const wait=async pred=>{for(let n=0;n<150;n++){const i=msgs.findIndex(pred);if(i>=0)return msgs.splice(i,1)[0];await sleep(20)}throw Error('response timeout')};let seq=0;
 const req=async(type,body={})=>{const n=++seq;ws.send(JSON.stringify({type,seq:n,...body}));return wait(m=>m.type==='inv'&&m.req===type&&m.seq===n)};
 ws.send(JSON.stringify({type:'join',id,name:id}));const joined=await wait(m=>m.type==='inv'&&m.req==='join');assert.equal(joined.ok,true);return {ws,msgs,req,joined};}
(async()=>{try{let ready=false;for(let n=0;n<100;n++){try{if((await fetch('http://'+base+'/healthz')).ok){ready=true;break}}catch{}await sleep(30)}assert.ok(ready);
 const a=await open('wear_owner'),b=await open('wear_peer');const current=()=>b.msgs.filter(m=>m.roster).at(-1)?.roster.find(p=>p.id==='wear_owner');
 const roster=async check=>{for(let n=0;n<100;n++){if(check(current()))return current();await sleep(20)}throw Error('appearance roster timeout')};
 assert.equal((await roster(p=>p?.equip?.helmet?.tier===1)).equip.armor.tier,1);
 a.ws.send(JSON.stringify({type:'state',snapshot:{equip:{weapon:{kind:'staff',tier:20},helmet:{tier:36},armor:{tier:36}}}}));await a.req('inv');await sleep(40);assert.equal(current().equip.helmet.tier,1);assert.equal(current().equip.weapon.kind,'sword');
 for(const slot of ['helmet','armor']){assert.equal((await a.req('equip',{slot,uid:0})).ok,true);await roster(p=>p?.equip?.[slot]===null);assert.equal((await a.req('equip',{slot,uid:a.joined.state.equip[slot]})).ok,true);await roster(p=>p?.equip?.[slot]?.tier===1)}
 a.ws.close();await new Promise(r=>a.ws.once('close',r));const restored=await open('wear_owner');assert.equal(restored.joined.state.inv.find(i=>i.uid===restored.joined.state.equip.helmet).tier,1);await roster(p=>p?.equip?.helmet?.tier===1&&p.equip.armor?.tier===1);
 console.log('PASS equipment real WS: initial owned wear, forged visual rejection, immediate slot unequip/re-equip roster, reconnect restoration');
}finally{for(const ws of sockets)ws.terminate();child.kill('SIGTERM')}})().catch(e=>{console.error(e);process.exitCode=1});
