'use strict';
const assert = require('assert/strict');
const I = require('./items');
const fresh = () => I.normalize({level:100,gold:10000000});
const s = fresh();
assert.equal(I.view(s).invCap,60);
assert.equal(I.expand(s).ok,true); assert.equal(I.view(s).invCap,61); assert.equal(s.gold,9999800);
assert.equal(I.view(s).expandCost,208);
assert.equal(I.learn(s,'qi_sword').ok,true);assert.equal(I.learn(s,'qi_sword').code,'owned');
assert.equal(I.learn(s,'fake').code,'bad_item');
assert.equal(I.buy(s,'potion',0).code,'bad_qty');assert.equal(I.buy(s,'potion',NaN).code,'bad_qty');assert.equal(I.equip(s,'helmet',NaN).code,'bad_uid');
const low = I.normalize({});assert.equal(I.learn(low,'qi_staff').code,'level');
assert.equal(I.autoSell(s,true,20).ok,true);assert.equal(I.autoSell(s,true,20.5).code,'bad_filter');
s.inv.push({uid:s.nextUid++,slot:'weapon',kind:'staff',tier:5,enh:0});
s.inv.push({uid:s.nextUid++,slot:'helmet',kind:'',tier:5,enh:0});
const staff=s.inv[1],helmet=s.inv[2];
assert.equal(I.equip(s,'helmet',helmet.uid).ok,true);
assert.equal(I.equip(s,'weapon',staff.uid).ok,true); assert.equal(s.equip.helmet,0);
assert.equal(I.equip(s,'helmet',helmet.uid).code,'class');
assert.equal(I.disassemble(s,staff.uid).code,'equipped');
const stones=s.stones,gold=s.gold;
const r=I.disassemble(s,undefined,[helmet.uid,helmet.uid,staff.uid]);
assert.deepEqual(r.disassemble.removed,[helmet.uid]);assert.equal(s.stones-stones,5);assert.equal(s.gold,gold);
assert.equal(I.disassemble(s,helmet.uid).code,'no_item');
const persisted=I.normalize(JSON.parse(JSON.stringify(s)));assert.deepEqual(I.view(persisted),I.view(s));
const tampered=JSON.parse(JSON.stringify(s));tampered.skills=['fake'];assert.throws(()=>I.normalize(tampered));
s.gold=100000000;
for(let n=1;n<140;n++) assert.equal(I.expand(s).ok,true);
assert.equal(I.view(s).invCap,200);assert.equal(I.expand(s).code,'max');
const drop=fresh();I.autoSell(drop,true,100);const count=drop.inv.length;
const k=I.kill(drop,1,()=>0);assert.equal(k.ok,true);assert.equal(drop.inv.length,count);assert.ok(k.drop.sold>0);
console.log('PASS skill ownership, persistence, expansion, dismantle protection, class change, auto sale');

const full=fresh();for(let n=1;n<60;n++) full.inv.push({uid:full.nextUid++,slot:'weapon',kind:'sword',tier:0,enh:0});
const overflow=I.kill(full,21,()=>0);assert.equal(overflow.ok,true);assert.equal(overflow.drop.sold,0);assert.equal(full.inv.length,60);assert.ok(full.pendingDrop);
const pendingUid=full.pendingDrop.uid, beforeFull=JSON.stringify(full);assert.equal(I.kill(full,1).code,'bag_full');assert.equal(JSON.stringify(full),beforeFull);
const savedFull=I.normalize(JSON.parse(JSON.stringify(full)));assert.equal(savedFull.pendingDrop.uid,pendingUid);
assert.equal(I.sell(savedFull,savedFull.inv[1].uid).ok,true);assert.equal(savedFull.pendingDrop,null);assert.ok(savedFull.inv.some(i=>i.uid===pendingUid));
const summon=fresh();I.map(summon,'B');summon.killCountT19=600;assert.equal(I.summon(summon,19).ok,true);assert.equal(summon.killCountT19,600);assert.equal(I.summon(summon,19).code,'pending');
assert.equal(I.summonAck(summon,19,false,summon.pendingSummon.token).ok,true);assert.equal(summon.killCountT19,600);assert.equal(I.summon(summon,19).ok,true);assert.equal(I.summonAck(summon,19,true,summon.pendingSummon?.token).ok,true);assert.equal(summon.killCountT19,0);assert.equal(I.summonAck(summon,19,true,summon.pendingSummon?.token).code,'no_pending');
console.log('PASS pending drop persistence/recovery and summon ACK failure preservation');

const maps=I.normalize({level:34});assert.equal(I.map(maps,'B').code,'level');maps.level=35;assert.equal(I.map(maps,'B').ok,true);assert.equal(I.kill(maps,1).code,'wrong_zone');assert.equal(I.kill(maps,13).code,'wrong_zone');assert.equal(I.map(maps,'C').code,'level');maps.level=70;assert.equal(I.map(maps,'C').ok,true);assert.equal(I.normalize(JSON.parse(JSON.stringify(maps))).zone,'C');console.log('PASS map35/70 entry and saved-zone protection');

const transported=I.transport(s,{x:999,hp:1e9,maxHp:1e9,level:999,skillTimers:[{key:'qi_sword',remaining:999}],potionCd:999,attackCd:-9,unknown:'discard'});assert.equal(transported.x,78.5);assert.equal(transported.level,100);assert.equal(transported.hp,1660);assert.equal(transported.skillTimers[0].remaining,10);assert.equal(transported.potionCd,5);assert.equal(transported.attackCd,0);assert.equal(transported.unknown,undefined);console.log('PASS transport bounds and authoritative progression/gear');

const prices=require('./skillbook_prices.json');
for(const book of I.view(fresh()).skillbooks){
  assert.equal(book.price,prices[book.key]);assert.equal(book.available,true);
  const buyer=I.normalize({level:book.level,gold:book.price-1});
  assert.equal(I.learn(buyer,book.key).code,'no_gold');assert.equal(buyer.gold,book.price-1);
  buyer.gold++;assert.equal(I.learn(buyer,book.key).ok,true);assert.equal(buyer.gold,0);
  assert.equal(I.learn(buyer,book.key).code,'owned');
}
console.log('PASS all ten simulation prices, insufficient/exact balance and duplicate purchase');

for(const counter of ['killCountT19','killCountT20']) {
  const broken=fresh();broken[counter]='600';assert.throws(()=>I.normalize(broken),/kill counter/);
}
const oversized=fresh();for(let n=1;n<=60;n++)oversized.inv.push({uid:oversized.nextUid++,slot:'weapon',kind:'sword',tier:0,enh:0});
assert.throws(()=>I.normalize(oversized),/saved inventory/);
console.log('PASS saved counter and capacity boundary validation');

const fractionalLegacy = I.normalize({level:20.9, exp:123.75, gold:890.25, potionCount:3.9, weaponTier:2.9, weaponEnh:1.9, helmetTier:1.9, armorTier:-1});
assert.equal(fractionalLegacy.level,20);assert.equal(fractionalLegacy.exp,123);assert.equal(fractionalLegacy.gold,890);
assert.equal(fractionalLegacy.potions,3);assert.equal(fractionalLegacy.weaponTier,2);assert.equal(fractionalLegacy.weaponEnh,1);assert.equal(fractionalLegacy.helmetTier,1);
assert.deepEqual(I.normalize(JSON.parse(JSON.stringify(fractionalLegacy))),fractionalLegacy);
for(const bad of [Infinity,NaN,Number.MAX_SAFE_INTEGER+1])assert.throws(()=>I.normalize({exp:bad}),/legacy number/);
const badModern=JSON.parse(JSON.stringify(fractionalLegacy));badModern.exp=123.75;assert.throws(()=>I.normalize(badModern),/saved inventory/);
console.log('PASS fractional legacy migration/reconnect, unsafe legacy rejection and strict modern save validation');

// Real protocol check: issued encounter receipts are consumed only by a matching reward.
async function receipts() {
  const {spawn}=require('child_process'),WebSocket=require('ws');
  const child=spawn(process.execPath,['server.js'],{cwd:__dirname,env:{...process.env,PORT:'18390',DATABASE_URL:'',REDIS_URL:''},stdio:'ignore'});
  let ws, peer;
  try {
    let ready=false;
    for(let n=0;n<100;n++){try{if((await fetch('http://127.0.0.1:18390/healthz')).ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,30));}
    assert.equal(ready,true,'isolated server ready');
    ws=new WebSocket('ws://127.0.0.1:18390');
    await new Promise((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});
    let latestRoster=[];
    ws.on('message',raw=>{const m=JSON.parse(raw);if(m.roster)latestRoster=m.roster;});
    function request(message,type,socket=ws){return new Promise((resolve,reject)=>{
      const timeout=setTimeout(()=>{socket.off('message',receive);reject(new Error('response timeout '+type));},3000);
      const receive=raw=>{const m=JSON.parse(raw);if(m.type!==type || (type==='inv'&&m.req!==message.type))return;clearTimeout(timeout);socket.off('message',receive);resolve(m);};
      socket.on('message',receive);socket.send(JSON.stringify(message));
    });}
    async function waitFor(check){for(let n=0;n<100&&!check();n++)await new Promise(r=>setTimeout(r,20));assert.ok(check(),'roster condition');}
    assert.equal((await request({type:'join',id:'receipt_test',name:'Receipt'},'inv')).ok,true);
    assert.equal((await request({type:'kill',tier:1,receipt:'made-up'},'inv')).code,'bad_receipt');
    assert.equal((await request({type:'spawn',tier:7,monsterId:1},'spawn')).code,'wrong_zone');
    const first=await request({type:'spawn',tier:1,monsterId:1},'spawn');assert.equal(first.ok,true);assert.ok(first.receipt);
    assert.equal((await request({type:'spawn',tier:1,monsterId:1},'spawn')).code,'bad_spawn');
    assert.equal((await request({type:'kill',tier:2,receipt:first.receipt},'inv')).code,'bad_receipt');
    assert.equal((await request({type:'kill',tier:1,receipt:first.receipt},'inv')).code,'too_fast');
    await new Promise(r=>setTimeout(r,320));
    const killed=await request({type:'kill',tier:1,receipt:first.receipt},'inv');assert.equal(killed.ok,true);
    const duplicate=await request({type:'kill',tier:1,receipt:first.receipt},'inv');assert.equal(duplicate.code,'bad_receipt');assert.equal(duplicate.state.gold,killed.state.gold);
    const second=await request({type:'spawn',tier:1,monsterId:2},'spawn');assert.equal(second.ok,true);
    assert.equal((await request({type:'map',zone:'A'},'inv')).ok,true);
    assert.equal((await request({type:'kill',tier:1,receipt:second.receipt},'inv')).code,'bad_receipt');
    // Clear resets the active ledger, not the spawn-rate budget.
    await new Promise(r=>setTimeout(r,2300));
    const alive=[];
    for(let id=3;id<8;id++){const issued=await request({type:'spawn',tier:1,monsterId:id},'spawn');assert.equal(issued.ok,true);alive.push(issued.receipt);}
    assert.equal((await request({type:'spawn',tier:1,monsterId:8},'spawn')).code,'spawn_full');
    peer=new WebSocket('ws://127.0.0.1:18390');
    await new Promise((resolve,reject)=>{peer.once('open',resolve);peer.once('error',reject);});
    assert.equal((await request({type:'join',id:'receipt_peer',name:'Peer'},'inv',peer)).ok,true);
    await waitFor(()=>latestRoster.filter(p=>!p.bot&&p.zone==='A').length===2);
    const peerAlive=[];
    for(let id=1;id<=5;id++){const issued=await request({type:'spawn',tier:1,monsterId:id},'spawn',peer);assert.equal(issued.ok,true);peerAlive.push(issued.receipt);}
    assert.equal(alive.length+peerAlive.length,2*5,'two real players own ten total encounters');
    assert.equal((await request({type:'spawn',tier:1,monsterId:6},'spawn',peer)).code,'spawn_full');
    assert.equal((await request({type:'spawn',tier:1,monsterId:8},'spawn')).code,'spawn_full','a second player does not give the first player duplicate slots');
    peer.send(JSON.stringify({type:'bot',delta:1}));
    peer.send(JSON.stringify({type:'state',snapshot:{x:1}}));
    await new Promise(r=>setTimeout(r,30));
    assert.equal(latestRoster.filter(p=>!p.bot&&p.zone==='A').length,2,'fake bot request cannot inflate real population');
    peer.close(); await new Promise(resolve=>peer.once('close',resolve));
    await waitFor(()=>latestRoster.filter(p=>!p.bot&&p.zone==='A').length===1);
    assert.equal((await request({type:'spawn',tier:1,monsterId:8},'spawn')).code,'spawn_full','departure leaves at most five owned encounters');
    for(const receipt of alive)ws.send(JSON.stringify({type:'despawn',receipt}));
    assert.equal((await request({type:'spawn',tier:1,monsterId:9},'spawn')).code,'spawn_rate');
    console.log('PASS real issued receipt, tier/zone/age boundaries, replay protection, map invalidation and five owned encounters per real player, same-map 2x5 total, departure and real roster metadata');
  } finally { if(peer)peer.terminate();if(ws)ws.terminate();child.kill('SIGKILL');await new Promise(r=>child.once('exit',r)); }
}
receipts().catch(e=>{console.error(e);process.exitCode=1;});
