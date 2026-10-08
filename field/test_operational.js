// Run inside a channel pod: kubectl exec -i POD -- env OPS_QA=1 node < field/test_operational.js
// Only a freshly generated QA account is seeded/deleted. Never prints credentials or saved user data.
'use strict';
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const {Pool} = require('pg');
const {createClient} = require('redis');
const WebSocket = require('ws');
const I = require('./items');
assert.equal(process.env.OPS_QA,'1','explicit operational QA opt-in required');
assert(process.env.DATABASE_URL && process.env.REDIS_URL,'SQL and Redis required');
const id='opsqa_'+crypto.randomBytes(8).toString('hex'), name='QA'+crypto.randomBytes(4).toString('hex');
const pw=crypto.randomBytes(24).toString('hex'), sockets=[];
const db=new Pool({connectionString:process.env.DATABASE_URL,max:1,connectionTimeoutMillis:5000,query_timeout:5000});
const redis=createClient({url:process.env.REDIS_URL,socket:{connectTimeout:5000,reconnectStrategy:false}});
let cookie, seq=0;
const main='http://127.0.0.1:8080', peer='http://channel-1:8080';
const pause=ms=>new Promise(r=>setTimeout(r,ms));
async function bounded(task){let timer;try{return await Promise.race([task,new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('service timeout')),5000);})]);}finally{clearTimeout(timer);}}
async function post(base,path,body){return fetch(base+path,{method:'POST',signal:AbortSignal.timeout(5000),headers:{'content-type':'application/json',...(cookie?{Cookie:cookie}:{})},body:JSON.stringify(body)});}
async function connect(base){
 const ws=new WebSocket(base.replace('http:','ws:'),{headers:{Cookie:cookie}}),messages=[];sockets.push(ws);
 ws.on('message',b=>messages.push(JSON.parse(b)));await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{ws.terminate();reject(Error('socket open timeout'));},5000);ws.once('open',()=>{clearTimeout(timer);resolve();});ws.once('error',e=>{clearTimeout(timer);reject(e);});});return {ws,messages};
}
async function wait(client,predicate){for(let n=0;n<250;n++){const index=client.messages.findIndex(predicate);if(index>=0)return client.messages.splice(index,1)[0];await pause(20);}throw Error('protocol wait timeout');}
async function request(client,type,body={}){const n=++seq;client.ws.send(JSON.stringify({type,seq:n,...body}));return wait(client,m=>m.type==='inv'&&m.seq===n);}
async function join(base,resume=false){const client=await connect(base);client.ws.send(JSON.stringify({type:'join',id,resume}));const inv=await wait(client,m=>m.type==='inv'&&m.req==='join');assert.equal(inv.ok,true);return {client,state:inv.state};}
async function switchTo(client,to){client.ws.send(JSON.stringify({type:'state',snapshot:{x:19,face:-1,hp:30}}));client.ws.send(JSON.stringify({type:'switch',to}));assert.equal((await wait(client,m=>m.type==='transfer')).to,to);}
(async()=>{
 await bounded(redis.connect());
 const channels=await (await fetch(main+'/channels',{signal:AbortSignal.timeout(5000)})).json();
 for(const index of [0,1]){const c=channels.channels.find(c=>c.index===index);assert(c&&!c.full&&!c.draining,'two available existing channels required');}
 assert.equal((await post(main,'/account/signup',{id,name,pw,gender:'male'})).status,200);
 const login=await post(main,'/account/login',{id,pw});assert.equal(login.status,200);cookie=login.headers.get('set-cookie').split(';')[0];
 assert.equal((await fetch(peer+'/account/me',{headers:{Cookie:cookie},signal:AbortSignal.timeout(5000)})).status,200);
 const fixture=I.normalize({level:100,gold:10000,weaponKind:'staff'});
 for(let n=1;n<60;n++)fixture.inv.push({uid:fixture.nextUid++,slot:'weapon',kind:'staff',tier:0,enh:0});
 fixture.pendingDrop={uid:fixture.nextUid++,slot:'weapon',kind:'staff',tier:20,enh:0};
 fixture.name=name;fixture.gender='male';
 await db.query('INSERT INTO saves(user_id,name,level,data) SELECT user_id,name,$2,$3 FROM accounts WHERE user_id=$1 AND role=$4',[id,100,JSON.stringify(fixture),'user']);
 const first=await join(main);assert.equal(first.state.pendingDrop.uid,61);assert.equal(first.state.statPoints,495);
 assert.equal((await fetch(main+'/account/roster',{headers:{Cookie:cookie},signal:AbortSignal.timeout(5000)})).status,403,'user cannot read admin roster');
 const duplicate=await connect(peer);duplicate.ws.send(JSON.stringify({type:'join',id}));assert.equal((await wait(duplicate,m=>m.type==='closed')).reason,'이미 접속 중인 계정입니다');
 first.client.ws.send(JSON.stringify({type:'bot',delta:1,level:1}));
 assert.equal((await request(first.client,'kill',{tier:21,receipt:'forged'})).code,'bad_receipt');
 await switchTo(first.client,1);
 const second=await join(peer,true);assert.equal((await wait(second.client,m=>m.type==='resume')).state.x,19);assert.equal(second.state.pendingDrop.uid,61);
 assert.equal(await bounded(redis.get('transfer:'+id)),null,'snapshot claim is one-time');
 const dismantled=await request(second.client,'auto_disassemble',{enabled:true,level:100});assert.equal(dismantled.drop.disassembled,17);assert.equal(dismantled.state.stones,17);assert.equal(dismantled.state.pendingDrop,null);
 const repeat=await request(second.client,'auto_disassemble',{enabled:true,level:100});assert.equal(repeat.state.stones,17);assert.equal(repeat.drop,undefined);
 const allocated=await request(second.client,'allocate_auto');assert.equal(allocated.state.intelligence,495);assert.equal(allocated.state.statPoints,0);
 second.client.ws.send(JSON.stringify({type:'spawn',monsterId:1,tier:21}));const spawned=await wait(second.client,m=>m.type==='spawn'&&m.monsterId===1);assert.equal(spawned.ok,true);await pause(350);
 const reward=await request(second.client,'kill',{tier:21,receipt:spawned.receipt});assert.equal(reward.ok,true);assert.equal(reward.state.gold,260000);assert.equal(reward.state.stones,34);assert.equal(reward.drop.disassembled,17);
 const replay=await request(second.client,'kill',{tier:21,receipt:spawned.receipt});assert.equal(replay.code,'bad_receipt');assert.equal(replay.state.gold,260000);assert.equal(replay.state.stones,34);
 const saved=(await db.query('SELECT data FROM saves WHERE user_id=$1',[id])).rows[0].data;assert.equal(saved.gold,260000);assert.equal(saved.stones,34);assert.equal(saved.intelligence,495);assert.equal(saved.pendingDrop,null);
 await switchTo(second.client,0);const returned=await join(main,true);assert.equal(returned.state.gold,260000);assert.equal(returned.state.stones,34);assert.equal(returned.state.intelligence,495);
 returned.client.ws.close();await bounded(new Promise(r=>returned.client.ws.once('close',r)));
 for(let n=0;n<100&&await bounded(redis.get('owner:'+id));n++)await pause(20);
 const reconnect=await join(peer);assert.equal(reconnect.state.stones,34);assert.equal(reconnect.state.intelligence,495);assert.equal(reconnect.state.statPoints,0);
 const fk=await db.query("SELECT convalidated FROM pg_constraint WHERE contype='f' AND conrelid='saves'::regclass AND confrelid='accounts'::regclass AND confdeltype='c' AND conkey=ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid='saves'::regclass AND attname='user_id')] AND confkey=ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid='accounts'::regclass AND attname='user_id')]");
 assert.ok(fk.rowCount>0,'deployed saves account cascade FK required');
 console.log('READONLY deployed cascade FK present; validated='+fk.rows[0].convalidated);
 assert.equal((await post(peer,'/account/remove',{id})).status,204,'own-account removal route');
 const removed=(await db.query('SELECT (SELECT count(*) FROM accounts WHERE user_id=$1)::int AS accounts,(SELECT count(*) FROM saves WHERE user_id=$1)::int AS saves,(SELECT count(*) FROM chats WHERE user_id=$1)::int AS chats',[id])).rows[0];
 for(const count of Object.values(removed))assert.equal(count,0);
 console.log('PASS deployed account/remove route: temporary own account/save/chat counts='+JSON.stringify(removed));
 assert.equal((await post(main,'/account/logout',{})).status,204);
 assert.equal(await bounded(redis.exists('session:'+cookie.split('=')[1])),0,'QA logout revoked session');
 console.log('PASS live Cloud SQL/Redis: cross-channel session, duplicate-owner rejection, user admin denied, pending transfer/recovery once, receipt replay rejected, stat allocation, SQL persistence, return transfer and reconnect');
})().catch(()=>{console.error('FAIL operational QA (sensitive diagnostic details suppressed)');process.exitCode=1;}).finally(async()=>{
 for(const ws of sockets)ws.terminate();await pause(150);
 const cleanupErrors=[];
 try {
  {
   await db.query('BEGIN');
   await db.query('DELETE FROM chats WHERE user_id=$1 AND EXISTS(SELECT 1 FROM saves WHERE user_id=$1 AND name=$2)',[id,name]);
   await db.query('DELETE FROM saves WHERE user_id=$1 AND name=$2',[id,name]);
   await db.query('DELETE FROM accounts WHERE user_id=$1 AND name=$2 AND role=$3',[id,name,'user']);
   await db.query('COMMIT');
  }
 }catch{await db.query('ROLLBACK').catch(()=>{});cleanupErrors.push('QA SQL deletion');}
 try {
  if(redis.isOpen){const token=cookie?.split('=')[1],keys=['owner:'+id,'transfer:'+id,...(token?['session:'+token]:[])];await bounded(redis.del(keys));assert.equal(await bounded(redis.exists(keys)),0);}
  else if(cookie)cleanupErrors.push('QA Redis unavailable for cleanup');
 }catch{cleanupErrors.push('QA Redis deletion/verification');}
 try {
  const row=(await db.query('SELECT (SELECT count(*) FROM accounts WHERE user_id=$1)::int AS accounts,(SELECT count(*) FROM saves WHERE user_id=$1)::int AS saves,(SELECT count(*) FROM chats WHERE user_id=$1)::int AS chats',[id])).rows[0];
  for(const count of Object.values(row))assert.equal(count,0);
  console.log('SQL cleanup counts='+JSON.stringify(row));
 }catch{cleanupErrors.push('QA SQL deletion verification');}
 if(cleanupErrors.length){console.error('QA cleanup requires verification: '+cleanupErrors.join(', '));process.exitCode=1;}
 else console.log('CLEANUP verified: generated QA account/save/chat and QA session/owner/transfer keys removed');
 await db.end();if(redis.isOpen)await bounded(redis.quit()).catch(()=>redis.disconnect());
});
