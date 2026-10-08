// 엔티티 애니메이션 상태머신.
// 게임플레이 상태(멈춤/이동/공격/피격/사망)를 스프라이트 클립으로 매핑한다.
// sprsheet.Clip 을 물고 돌린다. game.js 가 매 프레임 update(dt) → rect() 하면 됨.
// 아직 game.js 미연결.
//
// 규칙:
//  - dead 는 래치. 한번 die() 하면 사망 클립 1회 재생 후 마지막 프레임 유지.
//  - hurt 는 idle/walk/attack 을 끊고 1회 재생, 끝나면 이동 여부에 따라 idle/walk 복귀.
//  - attack 1회 재생 후 복귀. 재생 중 attack() 재요청은 무시(연타 방지), hurt/die 는 끊음.
//  - idle vs walk 는 moving 플래그로 결정. 몬스터는 idle 자리에 walk 클립을 넣어 "제자리에 안 서게".
//
// clips: { idle, walk, attack, hurt, dead } → 시트의 애니 이름.
//   attack 은 배열 가능(무기 스윙 3종 등) — 다음 공격마다 순환/랜덤.

// sprsheet 가 먼저 로드돼야 한다. 아티팩트 빌드에서 스크립트 하나가 빠져도
// 이 파일 로드 자체는 터지지 않게 방어적으로 받는다(Clip 은 인스턴스화 시점에만 필요).
const _spr = (typeof require !== 'undefined') ? require('./sprsheet') : (window.SprSheet || {});
const Clip = _spr.Clip;

const FPS = { idle: 8, walk: 10, attack: 14, hurt: 12, dead: 10 };

class AnimFSM {
  constructor(sheet, clips, opts = {}) {
    this.sheet = sheet;
    this.names = clips;                          // {idle,walk,attack,hurt,dead}
    this.fps = Object.assign({}, FPS, opts.fps);
    this.attackPick = opts.attackPick || 'cycle';  // 'cycle' | 'random'
    this._atkIdx = 0;
    this.moving = false;
    this.state = 'idle';
    this._clip = this._make('idle');
  }

  _resolve(state) {
    let name = this.names[state];
    if (Array.isArray(name)) {
      if (this.attackPick === 'random') name = name[(Math.random() * name.length) | 0];
      else { name = name[this._atkIdx % name.length]; this._atkIdx++; }
    }
    return name;
  }

  _make(state) {
    const loop = (state === 'idle' || state === 'walk');
    return new Clip(this.sheet, this._resolve(state), { fps: this.fps[state], loop });
  }

  _enter(state) {
    if (this.state === state && (state === 'idle' || state === 'walk')) return;
    this.state = state;
    this._clip = this._make(state);
  }

  /* ---- 이벤트 ---- */
  setMoving(v) {
    this.moving = !!v;
    if (this.state === 'idle' && this.moving) this._enter('walk');
    else if (this.state === 'walk' && !this.moving) this._enter('idle');
  }
  attack() {
    if (this.state === 'dead' || this.state === 'hurt' || this.state === 'attack') return;
    this._enter('attack');
  }
  hurt() {
    if (this.state === 'dead') return;
    this._enter('hurt');
  }
  die() {
    if (this.state === 'dead') return;
    this._enter('dead');
  }

  /* ---- 매 프레임 ---- */
  update(dt) {
    this._clip.step(dt);
    if (this._clip.done && (this.state === 'attack' || this.state === 'hurt')) {
      this._enter(this.moving ? 'walk' : 'idle');
    }
    // dead: done 이어도 유지 (clip.step 이 마지막 프레임에서 no-op)
  }

  clip() { return this._clip; }
  rect() { return this._clip.rect(); }
  isDead() { return this.state === 'dead'; }
  deadDone() { return this.state === 'dead' && this._clip.done; }
}

if (typeof window !== 'undefined') window.AnimFSM = AnimFSM;
if (typeof module !== 'undefined' && module.exports) module.exports = { AnimFSM };

/* ---------- 셀프체크: node field/animfsm.js ---------- */
if (typeof require !== 'undefined' && require.main === module) {
  const assert = require('assert');
  const { fromLayout } = require('./sprsheet');
  const sheet = fromLayout({
    character: { size: { width: 96, height: 96 } },
    spritesheet: {
      path: 'x.png', cell_size: { width: 96, height: 96 }, columns: 12, pivot: 'cell-center',
      rows: [
        { row: 0, type: 'animation', frame_count: 4, animation: 'walk' },
        { row: 1, type: 'animation', frame_count: 4, animation: 'atkA' },
        { row: 2, type: 'animation', frame_count: 4, animation: 'atkB' },
        { row: 3, type: 'animation', frame_count: 3, animation: 'hurt' },
        { row: 4, type: 'animation', frame_count: 3, animation: 'die' },
      ],
    },
  });
  const clips = { idle: 'walk', walk: 'walk', attack: ['atkA', 'atkB'], hurt: 'hurt', dead: 'die' };
  const fps = { idle: 10, walk: 10, attack: 10, hurt: 10, dead: 10 };

  let m = new AnimFSM(sheet, clips, { fps });
  assert.strictEqual(m.state, 'idle');
  assert.strictEqual(m.clip().name, 'walk');           // 몬스터: idle 자리 walk

  m.setMoving(true);
  m.attack();
  assert.strictEqual(m.state, 'attack');
  assert.strictEqual(m.clip().name, 'atkA');
  m.update(1.0);                                        // 4f @10fps → done
  assert.strictEqual(m.state, 'walk');                  // moving 이라 walk 복귀
  m.attack(); assert.strictEqual(m.clip().name, 'atkB'); // cycle 순환

  m.hurt();
  assert.strictEqual(m.state, 'hurt');
  m.update(1.0);
  assert.strictEqual(m.state, 'walk');

  m.attack(); const c1 = m.clip();
  m.attack(); assert.strictEqual(m.clip(), c1);          // 연타 무시

  m.die();
  assert.strictEqual(m.state, 'dead');
  m.update(5.0);
  assert.strictEqual(m.deadDone(), true);
  m.attack(); m.hurt(); m.setMoving(true);
  assert.strictEqual(m.state, 'dead');                   // 사망 래치

  console.log('animfsm 셀프체크 통과');
}
