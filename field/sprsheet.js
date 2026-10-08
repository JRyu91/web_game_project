// PixelLab 스프라이트시트 로더 (export_version 1.0).
// 레이아웃 JSON( character / spritesheet.rows )을 읽어 애니 이름 → 프레임 사각형으로 바꾼다.
// 시트는 균일 격자다: 셀 w·h 고정, 한 행에 columns 칸, 행 인덱스가 곧 세로 위치.
// 아직 game.js 에 연결 안 됨 — drawMonster/drawPlayer 가 이걸 쓰도록 붙이는 게 다음 단계.
//
// 시트 배치 규칙(제안): field/anim/<key>.png + field/anim/<key>.json
//   key 예: mob_zombie, boss_dragon, main_male ...

/* ---------- 레이아웃 파싱 (순수 함수, 테스트 대상) ---------- */
function fromLayout(layout) {
  const ss = layout.spritesheet;
  const cw = ss.cell_size.width, ch = ss.cell_size.height;
  const cols = ss.columns;
  const anims = {};       // name -> [{sx,sy,sw,sh}]
  const rotations = {};   // dir  -> {sx,sy,sw,sh}

  for (const row of ss.rows) {
    if (row.frame_count > cols) {
      console.warn(`sprsheet: '${row.animation || 'rotations'}' frame_count ${row.frame_count} > columns ${cols} — 프레임이 겹칩니다`);
    }
    const rects = [];
    for (let i = 0; i < row.frame_count; i++) {
      rects.push({ sx: (i % cols) * cw, sy: row.row * ch, sw: cw, sh: ch });
    }
    if (row.type === 'rotations') {
      (row.directions || []).forEach((dir, i) => { if (rects[i]) rotations[dir] = rects[i]; });
    } else if (row.animation) {
      anims[row.animation] = rects;      // 1방향이라 direction 무시 (횡스크롤은 flip 으로 처리)
    }
  }

  return {
    cw, ch,
    pivot: ss.pivot || 'cell-center',
    box: (layout.character || layout.object).size,  // 셀 안 실제 그림 원본 박스. object export 는 'object' 키.
    sheetPath: ss.path,
    anims, rotations,
    animNames: Object.keys(anims),
    frameCount: name => (anims[name] || []).length,
  };
}

/* ---------- 재생 클립 ---------- */
// fps 로 프레임을 넘긴다. loop=false 면 마지막 프레임에서 멈추고 done=true.
class Clip {
  constructor(sheet, name, opts = {}) {
    this.frames = sheet.anims[name]
      || (sheet.rotations[name] ? [sheet.rotations[name]] : [])
      || [];
    this.name = name;
    this.fps = opts.fps || 12;
    this.loop = opts.loop !== false;
    this.reset();
  }
  reset() {
    this.t = 0; this.i = 0;
    // 0~1프레임짜리 비루프 클립(사망/피격 애셋 누락 등)은 즉시 done 처리해야
    // AnimFSM 이 attack/hurt 에서 못 빠져나오거나 dead 가 안 걷혀 엔티티가 안 사라지는 걸 막는다.
    this.done = !this.loop && this.frames.length < 2;
    return this;
  }
  step(dt) {
    if (this.done || this.frames.length < 2) return;
    this.t += dt;
    const adv = Math.floor(this.t * this.fps);
    if (adv <= 0) return;
    this.t -= adv / this.fps;
    this.i += adv;
    if (this.i >= this.frames.length) {
      if (this.loop) { this.i %= this.frames.length; }
      else { this.i = this.frames.length - 1; this.done = true; }
    }
  }
  rect() { return this.frames[this.i] || null; }
}

/* ---------- 브라우저 로딩 ---------- */
// sprites.js 와 같은 규약: window.ASSET_SRC 가 있으면 data URI, 없으면 파일 경로.
// 핸들을 즉시 돌려주고, PNG·JSON 이 다 오면 handle.ready 가 true 가 된다.
function load(key, pngPath, jsonPath) {
  const handle = { key, ready: false, sheet: null, img: null };
  if (typeof window === 'undefined') return handle;   // node

  const src = (window.ASSET_SRC || {});
  fetch(src[key + '.json'] || jsonPath)
    .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
    .then(layout => {
      handle.sheet = fromLayout(layout);
      const im = new Image();
      im.onload = () => { handle.img = im; handle.ready = !!handle.sheet; };
      // PNG 404 등은 이 onerror 없이는 조용히 ready=false 로 영원히 멈춘다 — 감사 리스크 B8.
      im.onerror = () => console.warn('sprsheet: PNG 로드 실패', key, im.src);
      im.src = src[key + '.png'] || pngPath || handle.sheet.sheetPath;
    })
    .catch(err => console.warn('sprsheet: JSON 로드 실패', key, err));

  return handle;
}

// 한 프레임을 발밑(footX,footY) 기준으로 그린다. drawSprite 와 같은 좌표 규약.
// pivot 이 cell-center 라 셀 중앙이 원점 — 발끝은 셀 하단에서 (ch-box.h)/2 만큼 위.
function drawClip(ctx, handle, clip, footX, footY, drawH, opts = {}) {
  if (!handle || !handle.ready || !handle.img) return;
  const r = clip.rect();
  if (!r) return;
  const { cw, ch, box } = handle.sheet;
  const scale = drawH / box.height;
  const dW = cw * scale, dH = ch * scale;
  const botPad = (ch - box.height) / 2 * scale;   // 셀 하단 여백
  const x = footX - dW / 2;
  const y = footY - dH + botPad;
  ctx.save();
  ctx.imageSmoothingEnabled = false;
  if (opts.alpha != null) ctx.globalAlpha = opts.alpha;
  if (opts.flip) { ctx.translate(footX * 2, 0); ctx.scale(-1, 1); }
  ctx.drawImage(handle.img, r.sx, r.sy, r.sw, r.sh, x, y, dW, dH);
  if (opts.flash) {
    ctx.globalCompositeOperation = 'source-atop';
    ctx.fillStyle = `rgba(255,255,255,${opts.flash})`;
    ctx.fillRect(x, y, dW, dH);
  }
  ctx.restore();
}

if (typeof window !== 'undefined') {
  window.SprSheet = { fromLayout, load, drawClip, Clip };
}
if (typeof module !== 'undefined' && module.exports) {
  module.exports = { fromLayout, Clip };
}

/* ---------- 셀프체크: node field/sprsheet.js ---------- */
if (typeof require !== 'undefined' && require.main === module) {
  const assert = require('assert');
  // 실제 export (t21 수령동지) 축약 픽스처
  const fixture = {
    character: { size: { width: 112, height: 128 } },
    spritesheet: {
      path: 't21.png', cell_size: { width: 180, height: 180 },
      columns: 11, pivot: 'cell-center',
      rows: [
        { row: 0, type: 'rotations', frame_count: 8,
          directions: ['south','south-east','east','north-east','north','north-west','west','south-west'] },
        { row: 1, type: 'animation', frame_count: 7, animation: 'hurt', direction: 'south-east' },
        { row: 2, type: 'animation', frame_count: 9, animation: 'walk', direction: 'south-east' },
        { row: 3, type: 'animation', frame_count: 3, animation: 'death', direction: 'south-east' },
      ],
    },
  };
  const s = fromLayout(fixture);

  assert.deepStrictEqual(s.rotations['east'], { sx: 360, sy: 0, sw: 180, sh: 180 });
  assert.deepStrictEqual(s.anims.walk[0], { sx: 0, sy: 360, sw: 180, sh: 180 });
  assert.deepStrictEqual(s.anims.walk[8], { sx: 8 * 180, sy: 360, sw: 180, sh: 180 });
  assert.strictEqual(s.frameCount('hurt'), 7);
  assert.deepStrictEqual(s.animNames, ['hurt', 'walk', 'death']);

  const loop = new Clip(s, 'walk', { fps: 10, loop: true });
  assert.strictEqual(loop.i, 0);
  loop.step(0.35); assert.strictEqual(loop.i, 3);
  loop.step(0.7);  assert.strictEqual(loop.i, 1);      // (3+7) % 9
  assert.strictEqual(loop.done, false);

  const once = new Clip(s, 'death', { fps: 10, loop: false });
  once.step(0.2); assert.strictEqual(once.i, 2);
  once.step(5);   assert.strictEqual(once.i, 2);
  assert.strictEqual(once.done, true);

  const still = new Clip(s, 'south', {});
  still.step(10); assert.strictEqual(still.i, 0);

  // 0~1프레임 비루프 클립은 생성 즉시 done (FSM 무한 래치 방지)
  assert.strictEqual(new Clip(s, 'nope', { loop: false }).done, true);
  assert.strictEqual(new Clip(s, 'south', { loop: false }).done, true);
  assert.strictEqual(new Clip(s, 'walk', { loop: false }).done, false);

  console.log('sprsheet 셀프체크 통과');
}
