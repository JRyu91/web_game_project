// 지면 타일 렌더러.
// PixelLab sidescroller 타일셋(16 Wang 타일) 중 지면은 평지라 Wang 매칭이 필요 없다.
// 딱 2장만 쓴다:
//   wang_12 (UULL) = 윗면: 위 절반 지표(잔디/재), 아래 절반 흙/암반  → 지면 최상단 1줄
//   wang_0  (LLLL) = 전부 흙/암반                                   → 그 아래 채움
// 존별 tiles_A(숲)/tiles_B(폐허)/tiles_C(용암). game.js drawGround 가 이걸 쓰고,
// 안 되면 기존 단색 띠로 폴백.

const TOP = 'wang_12', FILL = 'wang_0', TS = 32;
const zones = {};   // zone('A'|'B'|'C') -> {img, top, fill, ready}

function pickRect(meta, name) {
  const t = meta.tileset_data.tiles.find(x => x.name === name);
  if (!t) throw new Error('타일 없음: ' + name);
  const b = t.bounding_box;
  return { sx: b.x, sy: b.y, sw: b.width, sh: b.height };
}

function load(zone) {
  if (zones[zone]) return zones[zone];
  const z = zones[zone] = { ready: false };
  if (typeof window === 'undefined') return z;      // node
  const src = window.ASSET_SRC || {};
  const key = 'tiles_' + zone;
  fetch(src[key + '.json'] || `anim/raw/${key}.json`)
    .then(r => r.json())
    .then(meta => {
      z.top = pickRect(meta, TOP);
      z.fill = pickRect(meta, FILL);
      const im = new Image();
      im.onload = () => { z.img = im; z.ready = true; };
      im.onerror = () => console.warn('ground png 로드 실패', zone);
      im.src = src[key + '.png'] || `anim/raw/${key}.png`;
    })
    .catch(e => console.warn('ground load 실패', zone, e));
  return z;
}

// surfaceY = 지면 윗면의 화면 y. tilePx = 화면상 타일 한 변(px).
// 타일은 월드 좌표 고정 — cam 만큼 밀어 그린다(기존 drawGround 잔돌과 같은 규약).
// 그려졌으면 true, 아직 로딩 중이면 false(호출측이 폴백).
function draw(ctx, zone, cam, surfaceY, screenW, screenH, tilePx) {
  const z = zones[zone] || load(zone);
  if (!z.ready) return false;
  const step = tilePx || 48;
  const off = -(((cam % step) + step) % step);
  ctx.save();
  ctx.imageSmoothingEnabled = false;
  for (let x = off - step; x < screenW + step; x += step) {
    ctx.drawImage(z.img, z.top.sx, z.top.sy, z.top.sw, z.top.sh, x, surfaceY, step, step);
    for (let y = surfaceY + step; y < screenH; y += step) {
      ctx.drawImage(z.img, z.fill.sx, z.fill.sy, z.fill.sw, z.fill.sh, x, y, step, step);
    }
  }
  ctx.restore();
  return true;
}

if (typeof window !== 'undefined') window.Ground = { load, draw, pickRect };
if (typeof module !== 'undefined' && module.exports) module.exports = { pickRect };

/* ---------- 셀프체크: node field/anim/ground.js ---------- */
if (typeof require !== 'undefined' && require.main === module) {
  const assert = require('assert'), fs = require('fs'), path = require('path');
  const raw = path.join(__dirname, 'raw');
  for (const z of ['A', 'B', 'C']) {
    const meta = JSON.parse(fs.readFileSync(path.join(raw, `tiles_${z}.json`)));
    const top = pickRect(meta, TOP), fill = pickRect(meta, FILL);
    assert.deepStrictEqual(top, { sx: 96, sy: 0, sw: 32, sh: 32 }, z + ' top');
    assert.deepStrictEqual(fill, { sx: 64, sy: 32, sw: 32, sh: 32 }, z + ' fill');
    assert.strictEqual(meta.tileset_data.tiles.length, 16, z + ' tile count');
  }
  assert.strictEqual(draw({}, 'A', 0, 100, 800, 600, 48), false);
  console.log('ground 셀프체크 통과');
}
