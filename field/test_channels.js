// REDIS_URL=redis://localhost:16379 node field/test_channels.js (isolated Redis only).
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const path = require('node:path');
const WebSocket = require('ws');
const {createClient} = require('redis');
const crypto = require('node:crypto');
const ports = [18280,18281], children=[], sockets=[];
const id='channelqa_'+crypto.randomBytes(5).toString('hex'), pw=crypto.randomBytes(16).toString('hex');
let redis;
function start(port,index){return new Promise((resolve,reject)=>{
 const child=spawn(process.execPath,[path.join(__dirname,'server.js')],{env:{...process.env,PORT:String(port),DATABASE_URL:'',CHANNEL_ID:'check-'+index,CHANNEL_BASES:ports.map(p=>'http://localhost:'+p).join(',')},stdio:['ignore','pipe','pipe']});children.push(child);
 child.stdout.on('data',b=>String(b).includes('대기 중')&&resolve());child.once('error',reject);child.once('exit',c=>reject(Error('server exit '+c)));
});}
function connect(port,cookie){return new Promise((resolve,reject)=>{
 const ws=new WebSocket('ws://localhost:'+port,{headers:{Cookie:cookie}});sockets.push(ws);const messages=[];
 ws.on('message',b=>messages.push(JSON.parse(b)));ws.once('error',reject);ws.once('open',()=>resolve({ws,messages}));
});}
async function message(client,test){const end=Date.now()+5000;while(Date.now()<end){const m=client.messages.find(test);if(m)return m;await new Promise(r=>setTimeout(r,20));}throw Error('message timeout');}
(async()=>{
 assert(process.env.REDIS_URL,'isolated Redis URL required');
 redis=createClient({url:process.env.REDIS_URL});await redis.connect();
 await Promise.all(ports.map(start));
 const first='http://localhost:'+ports[0];
 const post=async(url,body)=>fetch(url,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)});
 assert.equal((await post(first+'/account/signup',{id,pw,name:id.slice(-8),gender:'male'})).status,200);
 const login=await post(first+'/account/login',{id,pw});assert.equal(login.status,200);
 const cookie=login.headers.get('set-cookie').split(';')[0];
 const cross=await fetch('http://localhost:'+ports[1]+'/account/me',{headers:{Cookie:cookie}});assert.equal(cross.status,200);assert.equal((await cross.json()).ok.id,id);
 const a=await connect(ports[0],cookie);a.ws.send(JSON.stringify({type:'join',id}));assert((await message(a,m=>m.type==='inv'&&m.req==='join')).ok);
 const b=await connect(ports[1],cookie);b.ws.send(JSON.stringify({type:'join',id}));assert.equal((await message(b,m=>m.type==='closed')).reason,'이미 접속 중인 계정입니다');
 a.ws.send(JSON.stringify({type:'state',snapshot:{x:19,face:-1,hp:30,maxHp:60,level:1}}));
 a.ws.send(JSON.stringify({type:'switch',to:1}));assert.equal((await message(a,m=>m.type==='transfer')).to,1);
 const c=await connect(ports[1],cookie);c.ws.send(JSON.stringify({type:'join',id,resume:true}));
 assert.equal((await message(c,m=>m.type==='resume')).state.x,19);assert((await message(c,m=>m.type==='inv')).ok);
 assert.equal(await redis.get('transfer:'+id),null,'handoff claim is one-time');
 console.log('PASS: shared cookie session, cross-channel duplicate rejected, owner released on transfer, atomic snapshot claim (local memory saves; no SQL).');
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(async()=>{for(const ws of sockets)ws.terminate();for(const c of children)c.kill();await redis?.quit();});
