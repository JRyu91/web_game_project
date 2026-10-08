// Independent pure-rule regression. No network, production DB, or browser.
const assert = require('node:assert/strict');
const I = require('./items');
let failures = 0;
function check(name, run) { try { run(); console.log('PASS '+name); } catch(e) { failures++; console.error('FAIL '+name+': '+e.message); } }
const fresh = () => I.normalize({});
check('selling equipped weapon leaves state intact', () => {
 const s=fresh(), before=JSON.stringify(s);
 assert.equal(I.sell(s,s.equip.weapon).code,'equipped'); assert.equal(JSON.stringify(s),before);
});
check('bulk sale pays once for duplicate uid and protects equipped item', () => {
 const s=fresh(); s.inv.push({uid:s.nextUid++,slot:'weapon',kind:'sword',tier:0,enh:0});
 const uid=s.inv.at(-1).uid, gold=s.gold, price=I.sellPrice(s.inv.at(-1));
 assert.equal(I.sell(s,null,[uid,uid,s.equip.weapon]).ok,true); assert.equal(s.gold,gold+price);
 assert.equal(s.inv.length,1); assert.equal(I.sell(s,uid).ok,false); assert.equal(s.gold,gold+price);
});
check('invalid purchase quantity cannot consume gold', () => {
 for (const qty of [0,-1,NaN,Infinity,101,1.5]) {
  const s=fresh(); s.gold=100000; const before=JSON.stringify(s);
  assert.equal(I.buy(s,'potion',qty).ok,false,'qty '+qty); assert.equal(JSON.stringify(s),before);
 }
});
check('enhancement resource failure is atomic', () => {
 const s=fresh(), before=JSON.stringify(s); assert.equal(I.enhance(s,s.equip.weapon).ok,false);
 assert.equal(JSON.stringify(s),before);
});
check('destroying equipped weapon yields valid same-class fallback', () => {
 const s=fresh(); s.inv[0].kind='staff'; s.inv[0].enh=15; s.gold=10000000; s.stones=100000;
 let rolls=[0.999,0]; const r=I.enhance(s,s.equip.weapon,()=>rolls.shift());
 assert.equal(r.enh.result,'destroy'); I.normalize(s);
 assert.equal(s.weaponKind,'staff'); assert.equal(s.weaponTier,0); assert.equal(s.weaponEnh,0);
});
check('corrupt stored inventory throws without deleting it', () => {
 const s=fresh(); s.inv[0].tier=999; const before=JSON.stringify(s);
 assert.throws(()=>I.normalize(s)); assert.equal(JSON.stringify(s),before);
});
check('failed low-count summon preserves count', () => {
 const s=fresh(); s.killCountT19=499; const before=JSON.stringify(s);
 assert.equal(I.summon(s,19).ok,false); assert.equal(JSON.stringify(s),before);
});
check('JSON roundtrip retains inventory and currency', () => {
 const s=fresh(); s.gold=400; s.stones=6;
 const restored=I.normalize(JSON.parse(JSON.stringify(s))); assert.deepEqual(I.view(restored),I.view(s));
});
check('mass dismantle protects equipment and pays duplicates once', () => {
 const s=fresh(); s.inv.push({uid:s.nextUid++,slot:'weapon',kind:'staff',tier:3,enh:10});
 const item=s.inv.at(-1), stones=s.stones, gold=s.gold;
 assert.equal(I.disassemble(s,null,[item.uid,item.uid,s.equip.weapon]).ok,true);
 assert.equal(s.stones,stones+I.disassembleYield(item)); assert.equal(s.gold,gold); assert.equal(s.inv.length,1);
 assert.equal(I.disassemble(s,item.uid).ok,false); assert.equal(s.stones,stones+I.disassembleYield(item));
});
check('dismantle rejects malformed batches without partial removal', () => {
 const s=fresh();s.inv.push({uid:s.nextUid++,slot:'weapon',kind:'staff',tier:3,enh:0});
 const before=JSON.stringify(s);assert.equal(I.disassemble(s,null,[s.inv.at(-1).uid,'bad']).ok,false);
 assert.equal(JSON.stringify(s),before);
});
check('bag maximum cannot consume gold on repeated expansion', () => {
 const s=fresh();s.bagExpansions=140;s.gold=1000000;const before=JSON.stringify(s);
 for(let n=0;n<3;n++)assert.equal(I.expand(s).code,'max');assert.equal(JSON.stringify(s),before);
});
check('automatic sale filter rejects malformed settings atomically', () => {
 const s=fresh(), before=JSON.stringify(s);
 for(const args of [['true',1],[true,NaN],[true,-1],[false,101],[true,1.5]]){
  assert.equal(I.autoSell(s,...args).ok,false);assert.equal(JSON.stringify(s),before);
 }
});
check('automatic sale only sells drops within configured level', () => {
 const s=I.normalize({level:100});I.autoSell(s,true,0);const count=s.inv.length;
 const low=I.kill(s,1,()=>0);assert(low.drop.sold>0);assert.equal(s.inv.length,count);
 I.map(s,'B');const high=I.kill(s,7,()=>0);assert.equal(high.drop.sold,0);assert.equal(s.inv.length,count+1);
});
check('new settings survive reconnect serialization', () => {
 const s=fresh();s.bagExpansions=12;s.skills=['qi_sword'];I.autoSell(s,true,20);
 assert.deepEqual(I.view(I.normalize(JSON.parse(JSON.stringify(s)))),I.view(s));
});
const fullBag=()=>{const s=I.normalize({level:100,gold:100000});while(s.inv.length<60)s.inv.push({uid:s.nextUid++,slot:'weapon',kind:'sword',tier:0,enh:0});return s;};
check('full bag retains rare drop without forced sale or repeat rewards', () => {
 const s=fullBag(), beforeGold=s.gold;
 const r=I.kill(s,21,()=>0);assert.equal(r.ok,true);assert.equal(r.drop.sold,0);
 assert.equal(s.gold,beforeGold+r.drop.gold);assert.equal(s.inv.length,60);assert.equal(s.pendingDrop.tier,20);
 const before=JSON.stringify(s);assert.equal(I.kill(s,1,()=>0).code,'bag_full');assert.equal(JSON.stringify(s),before);
 const restored=I.normalize(JSON.parse(JSON.stringify(s)));assert.deepEqual(restored.pendingDrop,s.pendingDrop);
 const corrupt=JSON.parse(JSON.stringify(s));corrupt.pendingDrop.uid=corrupt.inv[0].uid;assert.throws(()=>I.normalize(corrupt));
});
check('sale, dismantle, and expansion collect pending drop exactly once', () => {
 for(const operation of ['sell','disassemble','expand']) {
  const s=fullBag();I.kill(s,21,()=>0);const pending=s.pendingDrop.uid;
  const result=operation==='expand'?I.expand(s):I[operation](s,s.inv[1].uid);
  assert.equal(result.ok,true);assert.equal(s.pendingDrop,null);assert.equal(s.inv.filter(i=>i.uid===pending).length,1);
  I.normalize(s);assert.equal(s.inv.filter(i=>i.uid===pending).length,1);
 }
});
check('summon reservation preserves counts on failure and consumes once on ACK', () => {
 const s=fresh();s.level=100;I.map(s,'B');s.killCountT19=500;
 assert.equal(I.summon(s,19).ok,true);assert.equal(s.killCountT19,500);
 assert.equal(I.summon(s,19).code,'pending');assert.equal(I.summonAck(s,20,true,s.pendingSummon.token).ok,false);
 assert.equal(I.summonAck(s,19,false,s.pendingSummon.token).ok,true);assert.equal(s.killCountT19,500);
 assert.equal(I.summon(s,19).ok,true);assert.equal(I.summonAck(s,19,true,s.pendingSummon.token).ok,true);assert.equal(s.killCountT19,0);
 assert.equal(I.summonAck(s,19,true).ok,false);assert.equal(s.killCountT19,0);
});
check('expired summon reservation rejects late old ACK', () => {
 const s=fresh();s.level=100;I.map(s,'B');s.killCountT19=500;assert.equal(I.summon(s,19).ok,true);
 const old=s.pendingSummon.token;s.pendingSummon.at-=31000;assert.equal(I.summon(s,19).ok,true);
 assert.notEqual(s.pendingSummon.token,old);const before=JSON.stringify(s);
 assert.equal(I.summonAck(s,19,true,old).ok,false);assert.equal(JSON.stringify(s),before);
 assert.equal(I.summonAck(s,19,false,s.pendingSummon.token).ok,true);assert.equal(s.killCountT19,500);
});
check('map unlock boundaries and reconnect state', () => {
 for(const [level,zone,expected] of [[34,'B',false],[35,'B',true],[69,'C',false],[70,'C',true]]){
  const s=I.normalize({level}),before=JSON.stringify(s);assert.equal(I.map(s,zone).ok,expected);
  if(!expected)assert.equal(JSON.stringify(s),before);else assert.equal(I.normalize(JSON.parse(JSON.stringify(s))).zone,zone);
 }
 const s=fresh(),before=JSON.stringify(s);assert.equal(I.map(s,'D').ok,false);assert.equal(JSON.stringify(s),before);
});
check('wrong-map normal and boss claims do not change state', () => {
 const s=I.normalize({level:100});for(const tier of [7,13,19,20]){const before=JSON.stringify(s);assert.equal(I.kill(s,tier,()=>0).code,'wrong_zone');assert.equal(JSON.stringify(s),before);}
 I.map(s,'B');for(const tier of [1,13,20]){const before=JSON.stringify(s);assert.equal(I.kill(s,tier,()=>0).code,'wrong_zone');assert.equal(JSON.stringify(s),before);}
});
check('every book charges published simulation price once and persists ownership', () => {
 const prices=require('./skillbook_prices.json');
 for(const book of I.view(I.normalize({level:100})).skillbooks){
  assert(Number.isSafeInteger(prices[book.key])&&prices[book.key]>0);
  assert.equal(book.price,prices[book.key]);
  const s=I.normalize({level:book.level,gold:book.price});
  assert.equal(I.learn(s,book.key).ok,true);assert.equal(s.gold,0);
  const before=JSON.stringify(s);assert.equal(I.learn(s,book.key).code,'owned');assert.equal(JSON.stringify(s),before);
  assert(I.normalize(JSON.parse(before)).skills.includes(book.key));
  for(const [level,gold,code] of [[book.level-1,book.price,'level'],[book.level,book.price-1,'no_gold']]){
   const rejected=I.normalize({level,gold}),unchanged=JSON.stringify(rejected);
   assert.equal(I.learn(rejected,book.key).code,code);assert.equal(JSON.stringify(rejected),unchanged);
  }
 }
});
check('transfer clips invalid values and retains owned skill timers and death state',()=>{
 const s=I.normalize({level:70});I.map(s,'C');s.skills=['qi_sword','rain_staff'];
 const snapshot=I.transport(s,{x:Infinity,hp:Infinity,level:999,zone:'A',dead:true,respawnRemaining:Infinity,potionCd:999,attackCd:-1,skillTimers:[{key:'qi_sword',remaining:999},{key:'rain_staff',remaining:7},{key:'fake',remaining:2}]});
 assert.equal(snapshot.x,40);assert.equal(snapshot.level,70);assert.equal(snapshot.maxHp,1180);
 assert.equal(snapshot.hp,1180);assert.equal(snapshot.dead,true);assert.equal(snapshot.respawnRemaining,0);
 assert.deepEqual(snapshot.skillTimers,[{key:'qi_sword',remaining:10},{key:'rain_staff',remaining:7}]);
 assert.equal(snapshot.potionCd,5);assert.equal(snapshot.attackCd,0);assert.equal(snapshot.zone,undefined);
 assert.equal(s.zone,'C');assert.equal(I.transport(s,[]),null);
});
process.exitCode=failures?1:0;
