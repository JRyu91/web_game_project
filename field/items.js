//권위 서버 쪽 아이템 규칙: 처치 보상(골드·경험치·강화석·장비 드랍), 인벤토리, 장착, 상점, 강화.
//순수 함수만 둔다(소켓·DB 모름). server.js 가 save 객체를 넘기면 그 자리에서 고치고 결과를 돌려준다.
//수치 출처: 장비 이름·레벨·방어력 = unity_client Data/GameData.Generated.cs, 몬스터 exp·gold·강화확률·강화석 = unity_review/balance/stats/r5_set.json,
//드랍·강화 비용·파괴 규칙 = unity_review/balance/stats/sim.py(r5 시뮬이 쓴 그대로), 보스 = unity_review/spec_boss_spawn.md.

const SWORD = ["낡은단검","청동단검","청동검","강철검","기사검","십자검","대검","미스릴검","용아검","화염검","뇌명검","서리검","명왕검","파사검","뇌신검","광휘검","파멸검","절멸검","종말의낫","최후의검","오메가"];
const STAFF = ["나무지팡이","도토리지팡이","참나무지팡이","무쇠지팡이","룬지팡이","수정지팡이","자수정지팡이","은빛지팡이","얼음지팡이","화염지팡이","폭풍지팡이","서리지팡이","심연지팡이","몽환지팡이","천공지팡이","성좌지팡이","창세지팡이","공허지팡이","창조의아침","드래곤스태프","적룡스태프"];
//무기 tier = 배열 인덱스, 레벨 = tier*5. 투구·갑옷 tier 도 GameData 배열 인덱스 그대로(클라 SaveData.helmetTier 와 같은 뜻).
const HELMET = [[0,"해진천","common",3],[5,"무명","common",4],[10,"가죽","common",5],[15,"무두질가죽","common",7],[20,"리벳가죽","common",9],[25,"은","warrior",12],[30,"뿔","warrior",16],[35,"미스릴","warrior",21],[40,"용린","warrior",28],[45,"화염","warrior",37],[50,"뇌명","warrior",48],[55,"서리","warrior",63],[60,"명왕","warrior",84],[65,"파사","warrior",111],[70,"뇌신","warrior",146],[75,"광휘","warrior",193],[80,"파멸","warrior",255],[85,"절멸","warrior",336],[90,"종언","warrior",444],[95,"근원","warrior",586],[100,"오메가","warrior",773],[25,"수정","mage",12],[30,"자수정","mage",16],[35,"은빛","mage",21],[40,"얼음","mage",28],[45,"화염","mage",37],[50,"폭풍","mage",48],[55,"서리","mage",63],[60,"심연","mage",84],[65,"몽환","mage",111],[70,"천공","mage",146],[75,"성좌","mage",193],[80,"창세","mage",255],[85,"공허","mage",336],[90,"창조","mage",444],[95,"드래곤","mage",586],[100,"적룡","mage",773]]; //[레벨, 이름, cls, def]
const ARMOR = [[0,"해진천","common",4],[5,"무명","common",5],[10,"가죽","common",7],[15,"무두질가죽","common",10],[20,"리벳가죽","common",13],[25,"은","warrior",17],[30,"뿔","warrior",22],[35,"미스릴","warrior",29],[40,"용린","warrior",39],[45,"화염","warrior",52],[50,"뇌명","warrior",69],[55,"서리","warrior",92],[60,"명왕","warrior",123],[65,"파사","warrior",163],[70,"뇌신","warrior",217],[75,"광휘","warrior",289],[80,"파멸","warrior",384],[85,"절멸","warrior",510],[90,"종언","warrior",678],[95,"근원","warrior",902],[100,"오메가","warrior",1200],[25,"수정","mage",17],[30,"자수정","mage",22],[35,"은빛","mage",29],[40,"얼음","mage",39],[45,"화염","mage",52],[50,"폭풍","mage",69],[55,"서리","mage",92],[60,"심연","mage",123],[65,"몽환","mage",163],[70,"천공","mage",217],[75,"성좌","mage",289],[80,"창세","mage",384],[85,"공허","mage",510],[90,"창조","mage",678],[95,"드래곤","mage",902],[100,"적룡","mage",1200]];
//[tier, rank, minLv, exp, gold] — exp·gold 는 r5 표. 일반(1~18) exp 표에는 ×0.77 이 이미 들어있어서 확정 보정 ×0.69 로 다시 맞춘다(final.md).
const MONS = [[1,"basic",1,3,9],[2,"basic",5,5,12],[3,"basic",10,8,18],[4,"basic",14,8,24],[5,"basic",19,12,36],[6,"basic",24,28,48],[7,"rare",29,54,69],[8,"rare",33,79,96],[9,"rare",38,114,132],[10,"rare",43,186,186],[11,"rare",48,263,261],[12,"rare",52,366,363],[13,"unique",57,621,510],[14,"unique",62,922,714],[15,"unique",67,1199,999],[16,"unique",71,1517,1401],[17,"unique",76,1882,1959],[18,"unique",81,2450,2745],[19,"midboss",60,33000,13000],[20,"boss",85,94000,36000],[21,"hidden",95,500000,250000]];
const A_ZONE_BOOST = t => t >= 4 && t <= 6 ? 2.5 : 1; // 261009: Lv20~35 A존 정체(42h) 해소 — 경험치·골드 같이 ×2.5 라 레벨당 골드는 그대로
const monExp = t => t <= 18 ? Math.max(1, Math.round(MONS[t - 1][3] * 0.69 / 0.77 * A_ZONE_BOOST(t))) : MONS[t - 1][3];

const ENH_SUCC = [100,100,95,90,85,80,80,79,79,78,78,77,77,77,77,77,66,66,66,66,66,49,49,49,49,49]; //인덱스 = 목표 단계(+1~+25)
const ENH_MAX = 25;
const DESTROY_FROM = 15, DESTROY_P = 0.3; //현재 +15 이상(= +16 도전부터) 실패 시 30% 파괴
const GEAR_DROP = { basic: 0.12, rare: 0.36, unique: 1.00 };          //sim DROP × drop_mult 2, 261008 형 결정 ×1.2(상한 1)
const STONE_DROP = { basic: 0.039, rare: 0.0778, unique: 0.1246 };   //개수 = ceil(드랍레벨/5+1), 261008 형 결정 ×2
const BOSS_GEAR = { 19: [85, 90], 20: [90, 95, 100], 21: [100] };
const GOLD_MULT = 2; // 261008 형 결정: 골드 드랍 ×2(보스 포함)
const INV_CAP = 60, INV_MAX = 200;
const SKILLS = ['qi', 'rain', 'volc', 'king', 'end'].flatMap((prefix, n) => ['sword', 'staff'].map(weapon => ({ key: prefix + '_' + weapon, weapon, level: (n + 1) * 20, name: [['참격파','마력탄'],['반월참','화염구'],['대지가르기','연쇄낙뢰'],['검왕강림','블리자드'],['천지개벽','메테오']][n][weapon === 'staff' ? 1 : 0] })));
//각 습득 레벨의 사냥터에서 r5 30분 사냥한 총 골드(무기별 시뮬 결과).
const SKILL_PRICES = require('./skillbook_prices.json');
const capacity = s => INV_CAP + s.bagExpansions;
const expandCost = s => Math.round(200 * 1.04 ** s.bagExpansions);
const disassembleYield = (it, st) => Math.ceil((itemLevel(it) / 5 + 1) * 0.8 * (st ? STONES_PER_LEVEL / XM(st.level) : 1)); // st = 분해하는 플레이어(레벨당 강화석 보정)
const LEVEL_MAX = 100;
const MAP_LEVELS = {A:1,B:35,C:70};
const COMBAT = Object.freeze({weaponDamageMultiplier:1, statDamagePerPoint:0.02, dexDefensePerPoint:1, luckCritPerPoint:0.001, critCap:0.3, critDamage:1.5, swordShortRange:26, swordRange:44, staffRange:218.4, skillCooldowns:[10,20,30,60,120], naturalStoneChances:[STONE_DROP.basic,STONE_DROP.rare,STONE_DROP.unique]});
const STAT_KEYS = ['str','dex','intelligence','luk'];
const statPoints = s => 5 * (s.level - 1) - STAT_KEYS.reduce((sum,k)=>sum+s[k],0);
function normalizeStats(s) {
  for (const key of STAT_KEYS) {
    if (s[key] === undefined) s[key] = 0;
    if (!Number.isInteger(s[key]) || s[key] < 0 || s[key] > 495) throw new Error('invalid saved stats');
  }
  if (statPoints(s) < 0) throw new Error('invalid saved stats');
}

// 261009 만렙 24h 목표(보스 포함 시뮬 기준 ×1.09 보정): 레벨 구간별 필요 경험치 배율 XM(레벨, 로그 선형 보간). 처치당 골드·강화석은 /XM 으로 보정해 레벨당 골드 75%·강화석 80% 유지(unity_review/balance 수학 검토)
const XM_K = [[1, 0.5232], [20, 0.4687], [35, 0.327], [50, 0.545], [70, 0.3924], [85, 0.6758], [100, 0.3924]];
const XM = lv => { for (let i = 1; i < XM_K.length; i++) if (lv <= XM_K[i][0]) { const [a, x] = XM_K[i - 1], [b, y] = XM_K[i]; return Math.exp(Math.log(x) + (Math.log(y) - Math.log(x)) * (lv - a) / (b - a)); } return XM_K[XM_K.length - 1][1]; };
const GOLD_PER_LEVEL = 0.75, STONES_PER_LEVEL = 0.82; // 강화석은 ceil 반올림 편향 보정 포함 → 실측 약 80%
const xpToLevel = lv => Math.round(30 * lv * lv * (lv > 70 ? 1.05 ** (lv - 70) : 1) * XM(lv));
const table = slot => slot === 'helmet' ? HELMET : ARMOR;
const itemLevel = it => it.slot === 'weapon' ? it.tier * 5 : table(it.slot)[it.tier][0];
const itemName = it => it.slot === 'weapon' ? (it.kind === 'staff' ? STAFF : SWORD)[it.tier] : table(it.slot)[it.tier][1];
const stepMult = tgt => tgt <= 4 ? 2 : tgt <= 7 ? 6 : tgt <= 10 ? 14 : tgt <= 14 ? 30 : 60;
const enhCost = it => {
  const L = itemLevel(it), m = stepMult(it.enh + 1);
  return { stones: Math.ceil(m * (L / 5 + 1)), gold: Math.round(40 * (L + 5) * m) };
};
//ponytail: 판매가·포션가 말고 상점 가격은 기획 문서에 없다. 판매가는 임시값 10×(L+5)×(1+강화), 기획 나오면 여기만 바꾼다.
const sellPrice = it => Math.round(10 * (itemLevel(it) + 5) * (1 + it.enh));
const potionPrice = lv => Math.round(4 * (lv + 5) ** 1.5);
const gearClass = s => s.inv.find(i => i.uid === s.equip.weapon)?.kind === 'staff' ? 'mage' : 'warrior';

function addItem(s, slot, kind, tier) {
  const it = { uid: s.nextUid++, slot, kind: slot === 'weapon' ? kind : '', tier, enh: 0 };
  s.inv.push(it);
  return it;
}

//세이브를 서버 소유 형태로 맞춘다. inv 가 없으면 구버전(클라가 쓰던 weaponTier 등)에서 한 번 옮겨온다.
//ponytail: 옮겨올 때의 값은 클라가 /save 로 쓴 걸 그대로 믿는다(첫 접속 1회). 신규 계정은 기본값이라 문제없다.
function normalize(s) {
  if (s.zone === undefined) s.zone = 'A';
  if (!Object.hasOwn(MAP_LEVELS,s.zone)) throw new Error('invalid saved zone');
  if (s.summonSerial === undefined) s.summonSerial = 0;
  if (!Number.isSafeInteger(s.summonSerial) || s.summonSerial < 0) throw new Error('invalid saved summon serial');
  if (s.bagExpansions === undefined) s.bagExpansions = 0;
  if (s.skills === undefined) s.skills = [];
  if (s.autoSell === undefined) s.autoSell = false;
  if (s.autoSellLevel === undefined) s.autoSellLevel = 0;
  if (s.autoDisassemble === undefined) s.autoDisassemble = false;
  if (s.autoDisassembleLevel === undefined) s.autoDisassembleLevel = 0;
  for (const key of ['killCountT19','killCountT20']) {
    if (s[key] === undefined) s[key] = 0;
    if (!Number.isSafeInteger(s[key]) || s[key] < 0) throw new Error('invalid saved kill counter');
  }
  if (s.pendingSummon && (![19,20].includes(s.pendingSummon.tier) || !Number.isSafeInteger(s.pendingSummon.at) || s.pendingSummon.at < 0 || !Number.isSafeInteger(s.pendingSummon.token) || s.pendingSummon.token !== s.summonSerial)) throw new Error('invalid saved summon');
  if (!Number.isInteger(s.bagExpansions) || s.bagExpansions < 0 || s.bagExpansions > INV_MAX - INV_CAP ||
      !Array.isArray(s.skills) || new Set(s.skills).size !== s.skills.length || s.skills.some(k => !SKILLS.some(x => x.key === k)) ||
      typeof s.autoSell !== 'boolean' || !Number.isInteger(s.autoSellLevel) || s.autoSellLevel < 0 || s.autoSellLevel > 100 ||
      typeof s.autoDisassemble !== 'boolean' || !Number.isInteger(s.autoDisassembleLevel) || s.autoDisassembleLevel < 0 || s.autoDisassembleLevel > 100 ||
      (s.autoSell && s.autoDisassemble)) throw new Error('invalid saved settings');
  if (Array.isArray(s.inv)) {
    const valid = s.inv.length <= capacity(s) && s.equip && ['weapon', 'helmet', 'armor'].every(k => Number.isInteger(s.equip[k])) &&
      [...s.inv, ...(s.pendingDrop ? [s.pendingDrop] : [])].every(i => i && Number.isSafeInteger(i.uid) && i.uid > 0 && ['weapon', 'helmet', 'armor'].includes(i.slot) &&
        Number.isInteger(i.tier) && i.tier >= 0 && i.tier < (i.slot === 'weapon' ? 21 : table(i.slot).length) &&
        (i.slot !== 'weapon' || ['sword', 'staff'].includes(i.kind)) && Number.isInteger(i.enh) && i.enh >= 0 && i.enh <= ENH_MAX) &&
      new Set([...s.inv, ...(s.pendingDrop ? [s.pendingDrop] : [])].map(i => i.uid)).size === s.inv.length + (s.pendingDrop ? 1 : 0) && s.inv.some(i => i.uid === s.equip.weapon && i.slot === 'weapon') &&
      ['helmet', 'armor'].every(k => !s.equip[k] || s.inv.some(i => i.uid === s.equip[k] && i.slot === k)) &&
      Number.isSafeInteger(s.nextUid) && s.nextUid > Math.max(...[...s.inv, ...(s.pendingDrop ? [s.pendingDrop] : [])].map(i => i.uid)) &&
      Number.isInteger(s.level) && s.level >= 1 && s.level <= LEVEL_MAX &&
      ['gold', 'exp', 'stones', 'potions'].every(k => Number.isSafeInteger(s[k]) && s[k] >= 0);
    if (!valid) throw new Error('invalid saved inventory');
    normalizeStats(s);
    return sync(s);
  }
  // Legacy saves can contain fractional XP; migrate once into the integer server schema.
  // Reject non-finite/unsafe values instead of overwriting unrecoverable progression.
  const legacyInt = (v, fallback = 0) => {
    const n = v === undefined || v === null ? fallback : Number(v);
    if (!Number.isFinite(n) || Math.abs(n) > Number.MAX_SAFE_INTEGER) throw new Error('invalid legacy number');
    return Math.max(0, Math.floor(n));
  };
  s.level = Math.min(LEVEL_MAX, Math.max(1, legacyInt(s.level, 1)));
  s.exp = legacyInt(s.exp);
  s.gold = legacyInt(s.gold);
  s.potions = legacyInt(s.potionCount, 3);
  s.stones = 0;
  s.inv = []; s.nextUid = 1; s.equip = { weapon: 0, helmet: 0, armor: 0 };
  const clamp = (v, n) => Math.min(n - 1, legacyInt(v));
  const w = addItem(s, 'weapon', s.weaponKind === 'staff' ? 'staff' : 'sword', clamp(s.weaponTier, 21));
  w.enh = clamp(s.weaponEnh, ENH_MAX + 1); s.equip.weapon = w.uid;
  for (const slot of ['helmet', 'armor']) {
    const rawTier = s[slot + 'Tier'];
    const t = rawTier === undefined || rawTier === null || Number(rawTier) < 0 ? -1 : legacyInt(rawTier);
    if (t >= 0 && t < table(slot).length) {
      const it = addItem(s, slot, '', t); it.enh = clamp(s[slot + 'Enh'], ENH_MAX + 1); s.equip[slot] = it.uid;
    }
  }
  normalizeStats(s);
  return sync(s);
}

//구버전 클라 필드(SaveData.cs)를 서버 값으로 다시 채운다. 지금 클라가 이 필드로 장비를 그리니 계속 맞춰 둔다.
function sync(s) {
  if (s.pendingDrop && s.inv.length < capacity(s)) { s.inv.push(s.pendingDrop); s.pendingDrop = null; }
  const get = slot => s.inv.find(i => i.uid === s.equip[slot]);
  const w = get('weapon');
  for (const slot of ['helmet', 'armor']) {
    const it = get(slot);
    if (it && table(slot)[it.tier][2] !== 'common' && table(slot)[it.tier][2] !== gearClass(s)) s.equip[slot] = 0;
  }
  const h = get('helmet'), a = get('armor');
  s.weaponKind = w.kind; s.weaponTier = w.tier; s.weaponEnh = w.enh;
  s.helmetTier = h ? h.tier : -1; s.helmetEnh = h ? h.enh : 0;
  s.armorTier = a ? a.tier : -1; s.armorEnh = a ? a.enh : 0;
  s.gearClass = gearClass(s); s.potionCount = s.potions;
  return s;
}

//클라에 보내는 상태 뭉치. 이름은 클라가 표를 안 뒤져도 되게 붙여 준다.
function view(s) {
  return {
    zone: s.zone, mapMinLevels: [1,35,70],
    str:s.str, dex:s.dex, intelligence:s.intelligence, luk:s.luk, statPoints:statPoints(s), combat:COMBAT,
    level: s.level, exp: s.exp, gold: s.gold, stones: s.stones, potions: s.potions,
    potionPrice: potionPrice(s.level), invCap: capacity(s), expandAvailable: capacity(s) < INV_MAX, expandCost: capacity(s) < INV_MAX ? expandCost(s) : 0,
    skills: [...s.skills], autoSell: s.autoSell, autoSellLevel: s.autoSellLevel, autoDisassemble: s.autoDisassemble, autoDisassembleLevel: s.autoDisassembleLevel,
    pendingSummon: s.pendingSummon ? {tier:s.pendingSummon.tier, token:s.pendingSummon.token} : null,
    pendingDrop: s.pendingDrop ? { ...s.pendingDrop, name: itemName(s.pendingDrop), level: itemLevel(s.pendingDrop), sell: sellPrice(s.pendingDrop), cost: enhCost(s.pendingDrop) } : null,
    skillbooks: SKILLS.map((x, n) => ({ ...x, price: SKILL_PRICES[x.key] || 0, available: Number.isSafeInteger(SKILL_PRICES[x.key]) && SKILL_PRICES[x.key] > 0, owned: s.skills.includes(x.key) })),
    killCountT19: s.killCountT19 || 0, killCountT20: s.killCountT20 || 0,
    equip: { weapon: s.equip.weapon, helmet: s.equip.helmet, armor: s.equip.armor },
    inv: s.inv.map(i => ({ ...i, name: itemName(i), level: itemLevel(i), sell: sellPrice(i), cost: i.enh < ENH_MAX ? enhCost(i) : null })),
  };
}

const fail = code => ({ ok: false, code });

//Spawn registration and rewards share the same zone/level boundary.
function spawnAllowed(s, tier) {
  if (!Number.isInteger(tier) || tier < 1 || tier > 21) return fail('bad_tier');
  const minLv = MONS[tier - 1][2], zone = s.zone;
  if (tier < 19 && (tier < 7 ? 'A' : tier < 13 ? 'B' : 'C') !== zone) return fail('wrong_zone');
  if ((tier === 19 && zone === 'A') || (tier === 20 && zone !== 'C')) return fail('wrong_zone');
  if (tier < 19 && minLv > s.level + 8) return fail('too_strong');
  return {ok:true};
}

//경험치를 더하고 레벨업 규칙(xpToLevel, 만렙 100)을 적용한다. 올린 레벨 수를 돌려준다.
function addExp(s, n) {
  let up = 0; s.exp += n;
  while (s.level < LEVEL_MAX && s.exp >= xpToLevel(s.level)) { s.exp -= xpToLevel(s.level); s.level++; up++; }
  if (s.level >= LEVEL_MAX) s.exp = 0;
  return up;
}

//처치 보상. 몬스터 판정은 클라가 하고 서버는 "그 처치가 말이 되는지"만 거른다(server.js 의 속도 제한 + 여기 레벨 제한).
function kill(s, tier, rng = Math.random) {
  if (s.pendingDrop) return fail('bag_full');
  const allowed = spawnAllowed(s, tier);
  if (!allowed.ok) return allowed;
  tier = Number(tier);
  const [, rank, minLv, , baseGold] = MONS[tier - 1];
  const g = Math.round(baseGold * GOLD_MULT * A_ZONE_BOOST(tier) * GOLD_PER_LEVEL / (tier >= 19 ? 1 : XM(s.level))); // 정수 골드. 보스는 경험치가 XM 을 안 타니 골드도 ×0.75 만
  const boss = tier >= 19;
  const drop = { tier, gold: boss ? g : Math.floor(Math.round(g * 0.8) + rng() * (Math.round(g * 1.4) - Math.round(g * 0.8) + 1)), exp: monExp(tier), stones: 0, item: null, sold: 0, disassembled: 0, levelUp: 0 };
  s.gold += drop.gold;
  if (tier >= 7 && tier <= 18) s.killCountT19 = (s.killCountT19 || 0) + 1; //B+C 일반 처치(spec_boss_spawn 소환 버튼)
  if (tier >= 13 && tier <= 18) s.killCountT20 = (s.killCountT20 || 0) + 1; //C 만
  drop.levelUp = addExp(s, drop.exp);
  const dropLv = Math.floor(minLv / 5) * 5;
  if (!boss && rng() < STONE_DROP[rank] * STONES_PER_LEVEL / XM(s.level)) { drop.stones = Math.ceil(dropLv / 5 + 1); s.stones += drop.stones; }
  if (boss || rng() < GEAR_DROP[rank]) {
    const lv = boss ? BOSS_GEAR[tier][Math.floor(rng() * BOSS_GEAR[tier].length)] : dropLv;
    const r = Math.floor(rng() * 4); //0·1 무기 50%, 2 투구 25%, 3 갑옷 25%
    let it;
    if (r <= 1) {
      const cur = s.inv.find(i => i.uid === s.equip.weapon);
      const kind = boss ? cur.kind : (rng() < 0.5 ? 'sword' : 'staff'); //보스는 지금 쓰는 계열로(spec_boss_spawn t21)
      it = addItem(s, 'weapon', kind, lv / 5);
    } else {
      const slot = r === 2 ? 'helmet' : 'armor', cls = gearClass(s);
      const tier2 = table(slot).findLastIndex(x => x[0] <= lv && (x[2] === 'common' || x[2] === cls));
      it = addItem(s, slot, '', tier2);
    }
    drop.item = { ...it, name: itemName(it), level: itemLevel(it) };
    if (applyDropPolicy(s, it, drop)) s.inv.pop();
    else if (s.inv.length > capacity(s)) { s.inv.pop(); s.pendingDrop = it; }
  }
  sync(s);
  return { ok: true, drop };
}

//slot 을 비우려면 uid 0 (무기는 못 비운다).
function equip(s, slot, uid) {
  if (!Number.isSafeInteger(uid) || uid < 0) return fail('bad_uid');
  if (!['weapon', 'helmet', 'armor'].includes(slot)) return fail('bad_slot');
  if (!uid) {
    if (slot === 'weapon') return fail('bad_slot');
    s.equip[slot] = 0;
    sync(s);
    return { ok: true };
  }
  const it = s.inv.find(i => i.uid === uid);
  if (!it) return fail('no_item');
  if (it.slot !== slot) return fail('bad_slot');
  if (itemLevel(it) > s.level) return fail('level');
  if (slot !== 'weapon' && table(slot)[it.tier][2] !== 'common' && table(slot)[it.tier][2] !== gearClass(s)) return fail('class');
  s.equip[slot] = uid;
  sync(s);
  return { ok: true };
}

//강화. 결과: success(+1) / keep(+4 이하 실패, 그대로) / down(-1) / destroy(아이템 삭제).
function enhance(s, uid, rng = Math.random) {
  const it = s.inv.find(i => i.uid === Number(uid));
  if (!it) return fail('no_item');
  if (it.enh >= ENH_MAX) return fail('max');
  const c = enhCost(it);
  if (s.stones < c.stones) return fail('no_stones');
  if (s.gold < c.gold) return fail('no_gold');
  s.stones -= c.stones; s.gold -= c.gold;
  const from = it.enh;
  let result;
  if (rng() * 100 < ENH_SUCC[from + 1]) { it.enh++; result = 'success'; }
  else if (from < 4) result = 'keep';
  else if (from >= DESTROY_FROM && rng() < DESTROY_P) {
    result = 'destroy';
    s.inv.splice(s.inv.indexOf(it), 1);
    //장착 중이던 무기가 깨지면 같은 계열 기본 무기를 새로 쥐여 준다(sim 의 "기본 장비로 복귀"). 방어구는 빈칸.
    if (s.equip.weapon === it.uid) s.equip.weapon = addItem(s, 'weapon', it.kind, 0).uid;
    for (const k of ['helmet', 'armor']) if (s.equip[k] === it.uid) s.equip[k] = 0;
  } else { it.enh--; result = 'down'; }
  sync(s);
  return { ok: true, enh: { uid: it.uid, from, to: result === 'destroy' ? -1 : it.enh, result, cost: c } };
}

function buy(s, item, qty) {
  qty = qty === undefined ? 1 : Number(qty);
  if (!Number.isInteger(qty) || qty < 1 || qty > 100) return fail('bad_qty');
  if (item !== 'potion') return fail('bad_item'); //강화석은 상점에 없다 — 밸런스상 병목 자원(final.md)
  const price = potionPrice(s.level) * qty;
  if (s.gold < price) return fail('no_gold');
  s.gold -= price; s.potions += qty;
  sync(s);
  return { ok: true };
}

//단일 uid(기존) 또는 uids 배열(일괄). 배열은 중복 제거, 없는 것·장착 중은 건너뛰고 skipped 로 돌려준다.
//ponytail: 아이템 잠금 기능이 아직 없어서 건너뛰는 건 장착 중·없는 uid 뿐. 잠금 생기면 여기 한 줄 추가.
function sell(s, uid, uids) {
  if (!Array.isArray(uids)) {
    const it = s.inv.find(i => i.uid === Number(uid));
    if (!it) return fail('no_item');
    if (Object.values(s.equip).includes(it.uid)) return fail('equipped');
    s.inv.splice(s.inv.indexOf(it), 1);
    s.gold += sellPrice(it);
    sync(s);
    return { ok: true, sell: { sold: [it.uid], gold: sellPrice(it), skipped: [] } };
  }
  if (!uids.length) return fail('empty');
  if (uids.length > INV_MAX) return fail('bad_qty');
  const r = { sold: [], gold: 0, skipped: [] };
  for (const u of new Set(uids.map(Number))) {
    const it = s.inv.find(i => i.uid === u);
    if (!it) { r.skipped.push({ uid: u, code: 'no_item' }); continue; }
    if (Object.values(s.equip).includes(u)) { r.skipped.push({ uid: u, code: 'equipped' }); continue; }
    s.inv.splice(s.inv.indexOf(it), 1);
    r.sold.push(u); r.gold += sellPrice(it);
  }
  s.gold += r.gold;
  sync(s);
  return r.sold.length ? { ok: true, sell: r } : { ok: false, code: 'none_sold', sell: r };
}

// 이관은 화면 상태만 받는다. 성장/장비/존은 세이브 권위값으로 다시 채운다.
function transport(s, raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const finite = (v, min, max, fallback = min) => typeof v === 'number' && Number.isFinite(v) ? Math.min(max,Math.max(min,v)) : fallback;
  const maxHp = 60 + s.level * 16;
  const weapon = s.inv.find(i=>i.uid===s.equip.weapon);
  const timers = Array.isArray(raw.skillTimers) ? raw.skillTimers : [];
  return {
    x:finite(raw.x,1.5,78.5,40), face:raw.face === -1 ? -1 : 1,
    hp:Math.floor(finite(raw.hp,raw.dead === true ? 0 : 1,maxHp,maxHp)), maxHp, level:s.level, exp:s.exp,
    equip:{weapon:{kind:weapon.kind,tier:weapon.tier,enh:weapon.enh}},
    dead:raw.dead === true,
    skillTimers:s.skills.map(key=>({key,remaining:finite(timers.find(t=>t && t.key===key)?.remaining,0,COMBAT.skillCooldowns[Math.floor(SKILLS.findIndex(x=>x.key===key)/2)])})),
    potionCd:finite(raw.potionCd,0,5),attackCd:finite(raw.attackCd,0,2/3),hitDelay:finite(raw.hitDelay,0,2),
    regenAcc:finite(raw.regenAcc,0,1),respawnRemaining:finite(raw.respawnRemaining,0,3),
  };
}

function map(s, zone) {
  if (!Object.hasOwn(MAP_LEVELS,zone)) return fail('bad_zone');
  if (s.level < MAP_LEVELS[zone]) return fail('level');
  if (s.pendingSummon && Date.now()-s.pendingSummon.at < 30000) return fail('pending');
  s.zone=zone;
  return {ok:true};
}

function learn(s, key) {
  const n = SKILLS.findIndex(x => x.key === key);
  if (n < 0) return fail('bad_item');
  const skill = SKILLS[n], price = SKILL_PRICES[key];
  if (s.skills.includes(key)) return fail('owned');
  if (s.level < skill.level) return fail('level');
  if (!Number.isSafeInteger(price) || price <= 0) return fail('price_unset');
  if (s.gold < price) return fail('no_gold');
  s.gold -= price; s.skills.push(key);
  return { ok: true };
}

function expand(s) {
  if (capacity(s) >= INV_MAX) return fail('max');
  const price = expandCost(s);
  if (s.gold < price) return fail('no_gold');
  s.gold -= price; s.bagExpansions++; sync(s);
  return { ok: true };
}

function allocateStat(s, key, qty) {
  const k = key === 'int' ? 'intelligence' : key;
  if (!STAT_KEYS.includes(k)) return fail('bad_stat');
  if (!Number.isInteger(qty) || qty < 1) return fail('bad_qty');
  if (qty > statPoints(s)) return fail('no_points');
  s[k] += qty;
  return {ok:true};
}
const STAT_RESET_COST = 1000;
function resetStats(s) {
  if (STAT_KEYS.every(k => s[k] === 0)) return fail('no_stats');
  if (s.gold < STAT_RESET_COST) return fail('no_gold');
  s.gold -= STAT_RESET_COST; for (const k of STAT_KEYS) s[k] = 0;
  return {ok:true};
}
function allocateAuto(s) {
  return allocateStat(s, gearClass(s) === 'mage' ? 'int' : 'str', statPoints(s));
}

function autoSell(s, enabled, level) {
  if (typeof enabled !== 'boolean' || !Number.isInteger(level) || level < 0 || level > 100) return fail('bad_filter');
  s.autoSell = enabled; s.autoSellLevel = level;
  if (enabled) s.autoDisassemble = false;
  return processPendingDrop(s);
}

// Only newly acquired or retained overflow drops enter the automatic policy.
function applyDropPolicy(s, it, drop) {
  if (s.autoDisassemble && itemLevel(it) <= s.autoDisassembleLevel) {
    drop.disassembled = disassembleYield(it, s); s.stones += drop.disassembled; return true;
  }
  if (s.autoSell && itemLevel(it) <= s.autoSellLevel) {
    drop.sold = sellPrice(it); s.gold += drop.sold; return true;
  }
  return false;
}

function processPendingDrop(s) {
  const it = s.pendingDrop;
  if (!it) return { ok: true };
  const drop = { item: { ...it, name: itemName(it), level: itemLevel(it) }, sold: 0, disassembled: 0 };
  if (!applyDropPolicy(s, it, drop)) return { ok: true };
  s.pendingDrop = null;
  return { ok: true, drop };
}

function autoDisassemble(s, enabled, level) {
  if (typeof enabled !== 'boolean' || !Number.isInteger(level) || level < 0 || level > 100) return fail('bad_filter');
  s.autoDisassemble = enabled; s.autoDisassembleLevel = level;
  if (enabled) s.autoSell = false;
  return processPendingDrop(s);
}

function disassemble(s, uid, uids, level) {
  if (level !== undefined && !Array.isArray(uids) && (uid === undefined || uid === 0)) {
    if (!Number.isInteger(level) || level < 0 || level > 100) return fail('bad_filter');
    uids = s.inv.filter(i => itemLevel(i) <= level).map(i => i.uid);
  }
  const many = Array.isArray(uids), wanted = many ? uids : [uid];
  if (!wanted.length) return fail('empty');
  if (wanted.length > INV_MAX || wanted.some(x => !Number.isSafeInteger(x) || x < 1)) return fail('bad_uid');
  const r = { removed: [], stones: 0, skipped: [] };
  for (const id of new Set(wanted)) {
    const it = s.inv.find(i => i.uid === id);
    const code = !it ? 'no_item' : Object.values(s.equip).includes(id) ? 'equipped' : '';
    if (code) { if (!many) return fail(code); r.skipped.push({uid:id, code}); continue; }
    s.inv.splice(s.inv.indexOf(it), 1); r.removed.push(id); r.stones += disassembleYield(it, s);
  }
  s.stones += r.stones; sync(s);
  return { ok: r.removed.length > 0, code: r.removed.length ? '' : 'none_removed', disassemble: r };
}

//보스 소환 예약. 실제 스폰 성공 ACK에만 카운트를 소비한다.
const SUMMON_NEED = { 19: 500, 20: 1000 };
function summon(s, tier) {
  const need = SUMMON_NEED[Number(tier)];
  if (!need) return fail('bad_tier');
  if ((Number(tier) === 19 && s.zone === 'A') || (Number(tier) === 20 && s.zone !== 'C')) return fail('wrong_zone');
  const k = 'killCountT' + tier;
  if ((s[k] || 0) < need) return fail('not_enough');
  if (s.pendingSummon && Date.now() - s.pendingSummon.at < 30000) return fail('pending');
  s.pendingSummon = { tier: Number(tier), at: Date.now(), token: ++s.summonSerial };
  return { ok: true };
}

function summonAck(s, tier, spawned, token) {
  if (!s.pendingSummon || s.pendingSummon.tier !== tier || s.pendingSummon.token !== token || typeof spawned !== 'boolean') return fail('no_pending');
  if (spawned) s['killCountT' + tier] = 0;
  s.pendingSummon = null;
  return { ok: true };
}

function usePotion(s) {
  if (s.potions < 1) return fail('no_potion');
  s.potions--;
  sync(s);
  return { ok: true };
}

//관리자 도구. 호출 전에 server.js 가 관리자 세션을 확인한다. 모든 수량은 정수·상한 검사. 소환(admin_spawn)은 검증만 하고 실제 등록은 일반 spawn 경로(영수증)를 탄다.
const ADMIN_GOLD_MAX = 1e12, ADMIN_ITEM_MAX = 99999;
function admin(s, msg) {
  const q = msg.qty;
  switch (msg.type) {
    case 'admin_gold':
      if (!Number.isSafeInteger(q) || q < 1 || q > 1e9 || s.gold + q > ADMIN_GOLD_MAX) return fail('bad_qty');
      s.gold += q; return { ok: true };
    case 'admin_exp': //q = 현재 레벨 필요 경험치의 % (100 = 1레벨)
      if (!Number.isSafeInteger(q) || q < 1 || q > 1000) return fail('bad_qty');
      if (s.level >= LEVEL_MAX) return fail('max');
      return { ok: true, drop: { levelUp: addExp(s, Math.ceil(xpToLevel(s.level) * q / 100)) } };
    case 'admin_level': //q = 올릴 레벨 수(내리기는 능력치 배분이 깨져서 막음)
      if (!Number.isSafeInteger(q) || q < 1 || q > LEVEL_MAX) return fail('bad_qty');
      if (s.level >= LEVEL_MAX) return fail('max');
      s.level = Math.min(LEVEL_MAX, s.level + q); s.exp = 0; return { ok: true };
    case 'admin_item': {
      const k = msg.item === 'stone' ? 'stones' : msg.item === 'potion' ? 'potions' : '';
      if (!k) return fail('bad_item');
      if (!Number.isSafeInteger(q) || q < 1 || s[k] + q > ADMIN_ITEM_MAX) return fail('bad_qty');
      s[k] += q; return sync(s) && { ok: true };
    }
    case 'admin_spawn': return spawnAllowed(s, msg.tier);
  }
  return fail('bad_req');
}

//서버만 바꾸는 세이브 키. POST /save 로 클라가 보내도 이 값들은 서버 쪽 것으로 덮어쓴다.
const OWNED = ['str', 'dex', 'intelligence', 'luk', 'statPoints', 'combat', 'zone', 'level', 'exp', 'gold', 'stones', 'potions', 'inv', 'nextUid', 'equip', 'skills', 'bagExpansions', 'autoSell', 'autoSellLevel', 'autoDisassemble', 'autoDisassembleLevel', 'pendingDrop', 'pendingSummon', 'summonSerial',
  'weaponKind', 'weaponTier', 'weaponEnh', 'helmetTier', 'helmetEnh', 'armorTier', 'armorEnh', 'gearClass', 'potionCount', 'killCountT19', 'killCountT20'];

module.exports = { admin, addExp, allocateStat, allocateAuto, resetStats, STAT_RESET_COST, statPoints, COMBAT, spawnAllowed, transport, map, summonAck, learn, expand, autoSell, autoDisassemble, disassemble, disassembleYield, normalize, view, kill, equip, enhance, buy, sell, usePotion, summon, OWNED, ENH_SUCC, DESTROY_FROM, DESTROY_P, enhCost, potionPrice, sellPrice, xpToLevel, monExp };
