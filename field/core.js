//게임 규칙과 수치만 있는 파일. DOM 을 전혀 안 쓴다.
//그래서 브라우저랑 채널 서버(node)가 이 파일 하나를 그대로 같이 쓴다.

const LEVEL_MAX = 100;
const CHANNEL_CAP = 10;              //채널 하나 = 파드 하나 정원

const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

/* ---------- 성장 곡선 ---------- */
const XP_BASE = 8;             //scratchpad/sweep.js 로 12개 조합 돌려서 잡은 값. 30 레벨 도달이 약 4.5분
const XP_POW = 1.35;
const XP_KNEE = 30;            //이 레벨부터 벽이 선다
const XP_HARD = 1.08;          //30 이후 레벨당 추가 배수. 100 레벨까지 약 16시간
//구간을 둘로 쪼개서 다른 식을 쓰면 경계에서 값이 튄다. 멱함수 하나에 30 이후만 가중치를 곱해서 이어붙였다.
const xpToLevel = lv =>
  Math.floor(XP_BASE * Math.pow(lv, XP_POW) * Math.pow(XP_HARD, Math.max(0, lv - XP_KNEE)));

//몬스터 경험치는 요구치의 1/5 속도로만 따라 오른다. 벽(XP_HARD)을 일부러 빼야 30 이후가 어려워진다.
const EXP_CATCHUP = 0.2;
const mobExpMult = lv => Math.pow(lv, XP_POW * EXP_CATCHUP);

/* ---------- 스킬 ---------- */
const SKILLS = [
  { key: 'qi',      lv: 20,  name: '어검술',   desc: '검기를 날려 앞을 관통한다',        cd: 5,  mult: 2.4, kind: 'line', base: 170, per: 5 },
  { key: 'rain',    lv: 40,  name: '만검강림', desc: '무기를 비처럼 떨어뜨린다',          cd: 10, mult: 3.6, kind: 'area', base: 200, per: 6 },
  { key: 'volcano', lv: 60,  name: '화산검',   desc: '검을 꽂아 화산을 터뜨린다',        cd: 15, mult: 6.0, kind: 'area', base: 240, per: 7 },
  { key: 'cleave',  lv: 80,  name: '천지참',   desc: '맵 전체를 가르는 참격',            cd: 20, mult: 9.0, kind: 'map' },
  { key: 'meteor',  lv: 100, name: '유성우',   desc: '맵 전체에 운석을 떨어뜨린다',      cd: 60, mult: 22,  kind: 'map' },
];

const skillsAt = lv => SKILLS.filter(s => lv >= s.lv);
const skillRange = (s, lv) => s.kind === 'map' ? Infinity : s.base + Math.max(0, lv - s.lv) * s.per; //kind 가 map 이면 사거리 무한
const skillDamage = (s, atk) => Math.max(1, Math.round(atk * s.mult));

/* ---------- 몬스터 ---------- */
//minLv 는 개인이 아니라 "채널 안 최고 레벨" 기준이다. 고렙 한 명이 들어오면 저렙도 레어를 같이 본다.
const MONSTERS = {
  slime:     { name: '슬라임',    tier: 'basic',   minLv: 1,  weight: 30, hp: 24,    atk: 4,   exp: 10,    gold: 5,    speed: 22, w: 27, h: 21 },
  rat:       { name: '쥐',        tier: 'basic',   minLv: 1,  weight: 26, hp: 32,    atk: 6,   exp: 13,    gold: 7,    speed: 38, w: 27, h: 18 },
  boar:      { name: '멧돼지',    tier: 'basic',   minLv: 3,  weight: 18, hp: 60,    atk: 11,  exp: 22,    gold: 12,   speed: 44, w: 33, h: 30 },
  bear:      { name: '곰',        tier: 'basic',   minLv: 6,  weight: 12, hp: 120,   atk: 20,  exp: 40,    gold: 22,   speed: 30, w: 29, h: 57 },
  unicorn:   { name: '유니콘',    tier: 'rare',    minLv: 10, weight: 5,  hp: 320,   atk: 34,  exp: 120,   gold: 80,   speed: 52, w: 32, h: 52 },
  sasquatch: { name: '새스쿼치',  tier: 'rare',    minLv: 10, weight: 4,  hp: 460,   atk: 44,  exp: 160,   gold: 105,  speed: 34, w: 31, h: 65 },
  zombie:    { name: '좀비',      tier: 'rare',    minLv: 10, weight: 5,  hp: 380,   atk: 52,  exp: 145,   gold: 95,   speed: 18, w: 24, h: 51 },
  veteran:   { name: '퇴역군인',  tier: 'unique',  minLv: 20, weight: 3,  hp: 1500,  atk: 90,  exp: 600,   gold: 420,  speed: 40, w: 28, h: 51 },
  senator:   { name: '국회의원',  tier: 'midboss', minLv: 1,  weight: 0,  hp: 6000,  atk: 130, exp: 2600,  gold: 1800, speed: 26, w: 26, h: 57 },
  pooh:      { name: '곰돌이 푸', tier: 'boss',    minLv: 1,  weight: 0,  hp: 24000, atk: 190, exp: 11000, gold: 7000, speed: 22, w: 38, h: 63 },
};

const SUMMON_KILLS = { senator: 100, pooh: 300 };   //채널 전체가 같이 채운다. 이게 협력하는 이유다

const MONSTER_KEYS = Object.keys(MONSTERS);

function spawnTable(channelLevel) {
  return MONSTER_KEYS.filter(k => MONSTERS[k].weight > 0 && MONSTERS[k].minLv <= channelLevel); //weight 0 인 소환 보스는 자연 스폰에서 빠진다
}

function pickSpawn(channelLevel, rng = Math.random) {
  const pool = spawnTable(channelLevel);
  const total = pool.reduce((s, k) => s + MONSTERS[k].weight, 0);
  let r = rng() * total;
  for (const k of pool) if ((r -= MONSTERS[k].weight) < 0) return k;
  return pool[0];
}

const monsterExp  = (type, lv) => Math.max(1, Math.round(MONSTERS[type].exp  * mobExpMult(lv)));
const monsterGold = (type, lv) => Math.max(1, Math.round(MONSTERS[type].gold * mobExpMult(lv)));

/* ---------- 장비 ---------- */
const SLOTS = {
  weapon: { key: 'weapon', name: '무기', stat: 'atk', label: '공격력' },
  helmet: { key: 'helmet', name: '투구', stat: 'def', label: '방어력' },
  armor:  { key: 'armor',  name: '갑옷', stat: 'def', label: '방어력' },
};
const SLOT_KEYS = Object.keys(SLOTS);

const GRADE_MAX = 10;
const GRADE_NAMES = ['조악한', '낡은', '평범한', '쓸만한', '정교한', '희귀한', '영웅의', '전설의', '신화의', '초월의'];
const GRADE_TINTS = ['#8b949e', '#a1a1aa', '#7dd3a0', '#4da3ff', '#7c8cff', '#c86bff', '#ff7ab8', '#ffb02e', '#ff6b3d', '#ff2e63'];

const SLOT_BASE = { weapon: 5, helmet: 3, armor: 4 };
const GRADE_GROWTH = 1.55;           //등급당 1.55배. 10등급이 1등급의 약 46배가 된다
const itemPower = (slot, grade) =>
  Math.round(SLOT_BASE[slot] * Math.pow(GRADE_GROWTH, clamp(grade, 1, GRADE_MAX) - 1));

const TIER_GRADE = { basic: [1, 3], rare: [3, 6], unique: [5, 8], midboss: [6, 9], boss: [8, 10] }; //여기에 유저 레벨 20 마다 바닥이 한 칸씩 더 오른다
const TIER_DROP  = { basic: 0.10, rare: 0.30, unique: 0.75, midboss: 1, boss: 1 };

function rollItem(monsterType, playerLv, rng = Math.random) {
  const tier = MONSTERS[monsterType].tier;
  if (rng() > TIER_DROP[tier]) return null;
  const [lo, hi] = TIER_GRADE[tier];
  const bonus = Math.floor(playerLv / 20);
  const grade = clamp(lo + Math.floor(rng() * (hi - lo + 1)) + bonus, 1, GRADE_MAX);
  const slot = SLOT_KEYS[Math.floor(rng() * SLOT_KEYS.length)];
  return {
    id: newId('it'),
    slot, grade,
    power: itemPower(slot, grade),
    name: `${GRADE_NAMES[grade - 1]} ${SLOTS[slot].name}`,
  };
}

/* ---------- 전투 ---------- */
const PLAYER = {
  baseHp: 60, hpPerLv: 14,
  baseAtk: 6, atkPerLv: 1.8,
  atkSpeed: 1.5,        //초당 공격 횟수
  range: 52,            //자동 공격 사거리(px)
  moveSpeed: 118,       //px/s
  idleAfter: 5,         //이만큼(초) 입력이 없으면 자동 사냥으로 넘어간다
  regenDelay: 3,        //피격 후 이만큼 지나면 회복 시작
  regenRate: 0.02,      //초당 최대 체력의 2%
  respawn: 3,           //사망 후 부활까지(초)
  hitCooldown: 1,       //같은 몬스터한테 연속으로 맞는 최소 간격(초)
};

function playerStats(lv, equip = {}) {
  const p = s => (equip[s] ? itemPower(s, equip[s].grade) : 0);
  const atk = Math.round(PLAYER.baseAtk + lv * PLAYER.atkPerLv + p('weapon'));
  return {
    maxHp: Math.round(PLAYER.baseHp + lv * PLAYER.hpPerLv),
    atk,
    def: p('helmet') + p('armor'),
    dps: Math.round(atk * PLAYER.atkSpeed),
  };
}

//방어력은 빼기가 아니라 비율 경감이다. 빼기로 하면 방어력이 몬스터 공격력을 넘는 순간 무적이 돼서 곡선이 통째로 무너진다.
const mitigate = (raw, def) => Math.max(1, Math.round(raw * 100 / (100 + def)));

/* ---------- 기여도 ---------- */
function contribution(damageByPlayer) {
  const total = Object.values(damageByPlayer).reduce((s, v) => s + v, 0);
  const out = {};
  for (const [id, dmg] of Object.entries(damageByPlayer)) out[id] = total ? dmg / total : 0;
  return out;
}

function splitReward(total, shares) {
  const out = {};
  for (const [id, share] of Object.entries(shares)) {
    out[id] = Math.max(1, Math.round(total * Math.max(share, 0.05)));   //한 대라도 때렸으면 최소 5% 는 보장. 막타 독식 방지
  }
  return out;
}

/* ---------- 레벨업 ---------- */
//남는 경험치를 이월해서 한 번에 여러 레벨이 오를 수 있다.
function gainExp(lv, exp, amount) {
  let level = lv, xp = exp + amount, gained = 0;
  while (level < LEVEL_MAX && xp >= xpToLevel(level)) {
    xp -= xpToLevel(level);
    level++; gained++;
  }
  if (level >= LEVEL_MAX) xp = 0;
  return { level, exp: xp, gained };
}

//맨손이면 공격 모션이 안 보여서 1등급 무기를 쥐어주고 시작한다.
const starterWeapon = () => ({
  id: 'starter', slot: 'weapon', grade: 1,
  power: itemPower('weapon', 1), name: '구리 검',
});

/* ---------- 채널 이관 ---------- */
//이 프로젝트의 알맹이. 채널이 닫힐 때 다른 채널로 넘겨야 하는 값만 담는다.
//세이브(newSave)랑 다른 점은 좌표랑 현재 체력이 들어간다는 것 — 지금 이 순간 화면이 안 끊기려면 그게 필요하다.
const TRANSFER_V = 1;

function transferState(p) {
  return {
    v: TRANSFER_V,
    id: p.id, name: p.name,
    level: p.level, exp: p.exp,
    x: Math.round(p.x), face: p.face,
    hp: Math.round(p.hp), maxHp: p.maxHp,
    equip: p.equip,
    cds: p.cds || {},          //쿨타임까지 넘겨야 채널 이동으로 쿨 초기화하는 악용이 막힌다
    at: Date.now(),
  };
}

function applyTransfer(p, t) {
  if (!t || t.v !== TRANSFER_V) return false;
  Object.assign(p, {
    id: t.id, name: t.name, level: t.level, exp: t.exp,
    x: t.x, face: t.face || 1, hp: t.hp, maxHp: t.maxHp,
    equip: t.equip, cds: t.cds || {},
  });
  return true;
}

/* ---------- 채널 지표 ---------- */
//KEDA metrics-api 스케일러가 /scale 에서 읽어가는 값. 채널 하나 기준 인원·정원 도달 여부.
function channelMetrics(players) {
  const active = players.length;
  return {
    active_players: active,
    full: active >= CHANNEL_CAP,
  };
}

//여러 채널·클라이언트가 같은 id 를 만들어버리면 기여도랑 이관이 섞인다.
let _seq = 0;
const newId = prefix => `${prefix}_${Date.now().toString(36)}_${(_seq++).toString(36)}_${Math.random().toString(36).slice(2, 7)}`;

/* ---------- 세이브 ---------- */
//Cloud SQL 붙이면 이 모양 그대로 한 행(row)으로 들어간다.
function newSave(name = '모험가') {
  return {
    v: 1, name,
    level: 1, exp: 0, gold: 0,
    equip: { weapon: starterWeapon(), helmet: null, armor: null },
    bag: [],
    kills: 0,              //개인 누적 처치
    stats: { deaths: 0, bossKills: 0, playSec: 0 },
    savedAt: Date.now(),
  };
}

const API = {
  LEVEL_MAX, CHANNEL_CAP, PLAYER, MONSTERS, MONSTER_KEYS, SUMMON_KILLS,
  SLOTS, SLOT_KEYS, GRADE_MAX, GRADE_NAMES, GRADE_TINTS,
  xpToLevel, mobExpMult, SKILLS, skillsAt, skillRange, skillDamage, spawnTable, pickSpawn, monsterExp, monsterGold,
  itemPower, rollItem, starterWeapon, playerStats, mitigate, contribution, splitReward,
  transferState, applyTransfer, channelMetrics, newId,
  gainExp, newSave, clamp,
};

if (typeof module !== 'undefined' && module.exports) module.exports = API;
if (typeof window !== 'undefined') window.Core = API;
