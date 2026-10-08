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

// Automatic dismantling only touches incoming/retained drops, never existing gear.
{
 const a=fresh(), original=JSON.stringify(a.inv);
 for(const args of [[1,0],[true,-1],[true,101],[true,0.5],[true,'20']]) {
  const before=JSON.stringify(a); assert.equal(I.autoDisassemble(a,...args).code,'bad_filter'); assert.equal(JSON.stringify(a),before);
 }
 I.autoSell(a,true,100); I.autoDisassemble(a,true,100);
 assert.equal(a.autoSell,false); assert.equal(JSON.stringify(a.inv),original);
 const before=a.stones, k=I.kill(a,1,()=>0);
 assert.equal(k.drop.disassembled,1); assert.equal(k.drop.sold,0);
 assert.equal(a.stones,before+k.drop.stones+1); assert.equal(JSON.stringify(a.inv),original);
 assert.equal(I.disassemble(a,a.equip.weapon).code,'equipped');
 I.autoSell(a,true,100); assert.equal(a.autoDisassemble,false);
 assert.equal(I.view(a).autoDisassembleLevel,100);
 assert.ok(I.OWNED.includes('autoDisassemble') && I.OWNED.includes('autoDisassembleLevel'));
 const b=fresh(); for(let n=1;n<60;n++) b.inv.push({uid:b.nextUid++,slot:'weapon',kind:'sword',tier:0,enh:0});
 I.kill(b,21,()=>0); assert.ok(b.pendingDrop);
 const snapshot=JSON.stringify(b), gained=I.disassembleYield(b.pendingDrop);
 const draft=JSON.parse(snapshot), result=I.autoDisassemble(draft,true,100);
 assert.equal(result.drop.disassembled,gained); assert.equal(draft.pendingDrop,null); assert.equal(draft.inv.length,60);
 // Same clone/discard transaction as the server's save_failed rollback.
 assert.equal(JSON.stringify(b),snapshot);
 const retry=I.autoDisassemble(b,true,100); assert.equal(retry.drop.disassembled,gained);
 const after=b.stones; assert.equal(I.autoDisassemble(b,true,100).drop,undefined); assert.equal(b.stones,after);
 const fullBefore=b.inv.length, fullDrop=I.kill(b,21,()=>0);assert.equal(fullDrop.ok,true);assert.equal(fullDrop.drop.disassembled,17);assert.equal(b.inv.length,fullBefore);assert.equal(b.pendingDrop,null);
 const excluded=fresh();I.autoDisassemble(excluded,true,0);const high=I.kill(excluded,21,()=>0);assert.equal(high.drop.disassembled,0);assert.ok(excluded.inv.some(i=>i.uid===high.drop.item.uid));
 assert.deepEqual(I.view(I.normalize(JSON.parse(JSON.stringify(b)))),I.view(b));
 const bad=JSON.parse(JSON.stringify(b)); bad.autoSell=true; assert.throws(()=>I.normalize(bad),/saved settings/);
}
console.log('PASS auto dismantle validation, exclusive policies, incoming-only gear, pending overflow, rollback/retry and once-only reward');

// Retroactive points are derived from authoritative level, never stored client totals.
{
 for(const level of [1,20,100]) {const a=I.normalize({level});assert.equal(I.view(a).statPoints,5*(level-1));assert.equal(a.str+a.dex+a.intelligence+a.luk,0);}
 const a=I.normalize({level:20});const start=JSON.stringify(a);
 for(const args of [['hp',1],['str',0],['dex',-1],['int',1.5],['luk','1'],['str',96]]) {assert.equal(I.allocateStat(a,...args).ok,false);assert.equal(JSON.stringify(a),start);}
 assert.equal(I.allocateStat(a,'str',10).ok,true);assert.equal(I.allocateStat(a,'int',20).ok,true);assert.equal(I.view(a).statPoints,65);
 assert.equal(I.allocateAuto(a).ok,true);assert.equal(a.str,75);assert.equal(I.view(a).statPoints,0);assert.equal(I.allocateAuto(a).ok,false);
 const mage=I.normalize({level:10,weaponKind:'staff'});assert.equal(I.allocateAuto(mage).ok,true);assert.equal(mage.intelligence,45);
 assert.deepEqual(I.view(I.normalize(JSON.parse(JSON.stringify(a)))),I.view(a));
 for(const value of [-1,0.5,'1',496]) {const bad=JSON.parse(start);bad.str=value;assert.throws(()=>I.normalize(bad),/saved stats/);}
 const over=JSON.parse(start);over.str=96;assert.throws(()=>I.normalize(over),/saved stats/);
 const fake=JSON.parse(start);fake.statPoints=999999;assert.equal(I.view(I.normalize(fake)).statPoints,95);
 const draft=JSON.parse(start);I.allocateStat(draft,'dex',5);assert.equal(JSON.stringify(I.normalize(JSON.parse(start))),start,'discarding failed save clone preserves stats');
 for(const key of ['str','dex','intelligence','luk','statPoints','combat'])assert.ok(I.OWNED.includes(key));
 const low=I.normalize({}); low.exp=I.xpToLevel(1);I.kill(low,1,()=>1);assert.equal(I.view(low).statPoints,5);
}
console.log('PASS retroactive growth migration, allocation validation/auto weapon, derived points, reconnect and rollback');

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
    const forgedSave=await fetch('http://127.0.0.1:18390/save/receipt_test',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({level:100,str:495,statPoints:999,combat:{weaponDamageMultiplier:999}})});assert.equal(forgedSave.status,204);
    const stats=await request({type:'inv'},'inv');assert.equal(stats.state.level,1);assert.equal(stats.state.str,0);assert.equal(stats.state.statPoints,0);assert.equal(stats.state.combat.weaponDamageMultiplier,1);
    assert.equal((await request({type:'allocate_stat',item:'str',qty:1},'inv')).code,'no_points');

    const policy=await request({type:'auto_disassemble',enabled:true,level:100},'inv');assert.equal(policy.ok,true);assert.equal(policy.state.autoDisassemble,true);
    const invalidPolicy=await request({type:'auto_disassemble',enabled:true,level:101},'inv');assert.equal(invalidPolicy.code,'bad_filter');assert.equal(invalidPolicy.state.autoDisassembleLevel,100);
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
    const joinedPeer=latestRoster.find(p=>p.id==='receipt_peer');assert.equal(joinedPeer.x,40);assert.equal(joinedPeer.face,1);
    peer.send(JSON.stringify({type:'pos',x:999,face:-1}));
    peer.send(JSON.stringify({type:'state',snapshot:{x:1}}));
    await request({type:'inv'},'inv',peer);
    await waitFor(()=>latestRoster.find(p=>p.id==='receipt_peer')?.face===-1);
    assert.equal(latestRoster.find(p=>p.id==='receipt_peer').x,78.5);
    const chats=[]; const onChat=raw=>{const m=JSON.parse(raw);if(m.type==='chat')chats.push(m.text);};ws.on('message',onChat);
    for(const text of [{malformed:true},'x'.repeat(121),'  ',...Array.from({length:8},(_,n)=>'throttle-'+n)])peer.send(JSON.stringify({type:'chat',text}));
    await request({type:'inv'},'inv',peer);await new Promise(r=>setTimeout(r,30));
    assert.equal(chats.length,4);assert.deepEqual(chats,['throttle-0','throttle-1','throttle-2','throttle-3']);
    ws.off('message',onChat);
    // A non-admin cannot add bots, even with syntactically valid input.
    peer.send(JSON.stringify({type:'bot',delta:1,level:1}));await request({type:'inv'},'inv',peer);
    assert.equal(latestRoster.filter(p=>p.bot).length,0);

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
