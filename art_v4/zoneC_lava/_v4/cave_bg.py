"""Zone C 동굴 배경 (v4): back_wall / far(용암강) / mid(암석 기둥) / ceiling / lavafalls / embers.

모두 1x 네이티브 픽셀, 팔레트 램프로 양자화(그레이딩 없이 최종색). 가로 주기 W 노이즈 + x 랩 → 좌우 루프 이음매 없음.
조명 모델: 아래(용암강/폭포 웅덩이)에서 올라오는 빛 → 아래가 밝고 위로 갈수록 어두움, 폭포 주변 가로 부스트.
"""
from PIL import Image
import numpy as np, math

H, GROUND_TOP = 720, 588
FALLS = [300, 1270, 2620]        # 용암폭포 x (맵 좌표). ceiling/mid/lavafalls/embers는 같은 시차(.35)
FLOW_P = 64                      # 폭포 흐름 텍스처 세로 주기 → 8프레임 x 8px

def vnoise(W, Hh, cx, cy, rng, wrap_y=False):
    g = rng.random((cy + (0 if wrap_y else 1), cx))
    xs = np.arange(W) * cx / W; x0 = xs.astype(int); tx = xs - x0; tx = tx*tx*(3-2*tx)
    rows = g[:, x0] * (1-tx) + g[:, (x0+1) % cx] * tx
    ys = np.arange(Hh) * cy / Hh; y0 = ys.astype(int); ty = ys - y0; ty = (ty*ty*(3-2*ty))[:, None]
    y1 = (y0+1) % cy if wrap_y else y0+1
    return rows[y0] * (1-ty) + rows[y1] * ty

def fbm(W, Hh, cx, octs, rng):
    out, amp, tot = np.zeros((Hh, W)), 1., 0.
    for _ in range(octs):
        out += amp * vnoise(W, Hh, cx, max(1, round(cx*Hh/W)), rng); tot += amp; amp *= .5; cx *= 2
    return out / tot

def ramp(I, pal):
    pal = np.array(pal, np.uint8); idx = np.clip((I * len(pal)).astype(int), 0, len(pal)-1)
    return pal[idx]

def rgba(rgb, mask):
    a = np.where(mask, 255, 0).astype(np.uint8)
    return Image.fromarray(np.dstack([rgb, a]), 'RGBA')

def fall_boost(W, sigma):
    x = np.arange(W); b = np.zeros(W)
    for fx in FALLS:
        d = (x - fx + W/2) % W - W/2; b += np.exp(-(d/sigma)**2)
    return b

# ── 용암폭포 스프라이트 (루프 프레임 공용) ──
FW = 170
def fall_sprite(h, phase, seed=7):
    rng = np.random.default_rng(seed)
    tex = vnoise(64, FLOW_P, 16, 4, rng, wrap_y=True) * .65 + vnoise(64, FLOW_P, 32, 8, rng, wrap_y=True) * .35
    im = np.zeros((h, FW, 4), np.uint8); c0 = FW // 2
    pool_y = h - 7
    ys = np.arange(h)
    cen = c0 + 3*np.sin(ys*.05) + 2*np.sin(ys*.017 + 1)
    hw = 13 + 13*np.sqrt(np.clip((ys-6)/(h-6), 0, 1))
    xs = np.arange(FW)
    for y in range(h):
        dx = np.abs(xs - cen[y]); d = dx / hw[y]; n = tex[(y - phase) % FLOW_P, xs % 64]
        # 주변 암석에 비치는 헤일로 (계단형 알파)
        dd = dx - hw[y]; halo = np.where(dd < 6, 90, np.where(dd < 16, 55, np.where(dd < 34, 26, 0)))
        halo = (halo * min(1, (y+10)/60)).astype(int)
        im[y, :, :3] = (255, 100, 36); im[y, :, 3] = np.where(d > 1, halo, 0)
        col = np.select([c[:, None] for c in (d < .28 + .18*n, d < .66 + .1*n, d < .88, d <= 1)],
                        [np.where((n > .55)[:, None], [255, 240, 170], [255, 208, 96]),
                         np.where((n > .42)[:, None], [255, 160, 56], [244, 120, 40]),
                         np.full((FW, 3), [212, 76, 30]),
                         np.where((n > .38)[:, None], [120, 34, 22], [70, 22, 18])], 0).astype(np.uint8)
        crust = (d > .55) & (d <= .88) & (n < .2)
        col[crust] = (150, 44, 24)
        m = d <= 1
        im[y, m, :3] = col[m]; im[y, m, 3] = 255
    # 천장 균열 (고정, 폭포 출구에서 위/옆으로 뻗는 발광 균열)
    r2 = np.random.default_rng(seed + 1)
    for k in range(5):
        x, y = c0 + r2.integers(-10, 11), 10
        dxs = (-1, 1)[k % 2]
        for _ in range(r2.integers(18, 40)):
            if 0 <= x < FW - 1 and y >= 0:
                im[y, x:x+2] = (255, 150, 50, 255)
                if y + 1 < h: im[y+1, x:x+2] = (140, 44, 22, 255)
            x += dxs * r2.integers(0, 3); y -= r2.integers(0, 2)
    # 바닥 웅덩이 + 튀김
    for y in range(pool_y - 6, h):
        for x in range(FW):
            r = math.hypot((x - c0) / 62, (y - pool_y) / 6)
            if r <= 1:
                im[y, x] = ((255, 226, 130) if r < .45 else (255, 150, 50) if r < .75 else
                            (200, 68, 28) if r < .92 else (96, 28, 20)) + (255,)
    for k in range(12):
        t = (phase / FLOW_P + k / 12) % 1; side = 1 if k % 2 else -1; vx = 10 + (k * 7) % 22
        x = int(c0 + side * (hw[-1] * .6 + vx * t)); y = int(pool_y - 4 - (26 + k % 3 * 8) * (t*2 - t*t*2*1.0) * 1)
        if 0 <= x < FW-1 and 0 <= y < h-1:
            im[y:y+2, x:x+2] = (255, 190, 80, 255) if t < .5 else (240, 100, 36, 255)
    return Image.fromarray(im, 'RGBA')

def cave_bg(L, W, prng):
    rng = np.random.default_rng(prng.randrange(1 << 30))
    Y = np.arange(H)[:, None]; X = np.arange(W)[None, :]
    boost = fall_boost(W, 240)[None, :]

    # ── back_wall: 층리 + 균열 + 아래서 올라오는 따뜻한 빛 ──
    warp = fbm(W, H, 8, 2, rng)
    detail = fbm(W, H, 64, 3, rng)
    yy = Y + 110 * (warp - .5) + 14 * (detail - .5)
    band = np.floor(yy / 26).astype(int); f = yy / 26 - band
    bright = rng.random(200) * .22
    ridge = (np.abs(fbm(W, H, 56, 2, rng) - .5) < .01) & (fbm(W, H, 12, 2, rng) > .6)
    v = .42 + bright[band % 200] + .3 * (detail - .5) + .14 * (f > .8) - .16 * (f < .08) - .22 * ridge
    Lg = np.clip((Y - 90) / 500, 0, 1) ** 1.5
    I = v * (.52 + .75 * Lg + .35 * boost * Lg ** .5)
    wall = ramp(I, [(8, 6, 9), (14, 9, 13), (21, 13, 17), (30, 17, 20), (42, 21, 22), (58, 27, 24), (80, 35, 27), (104, 44, 30)])
    L['back_wall'].paste(rgba(wall, np.ones((H, W), bool)))

    # ── far: 벽 밑 용암강 (바닥 조명의 근거). 지면(588) 바로 위에서만 보인다 ──
    shelf = np.array([548 + 6*math.sin(x*2*math.pi*5/W) + 4*math.sin(x*2*math.pi*23/W) for x in range(W)])
    gap = vnoise(W, 1, 40, 1, rng)[0]
    rock = Y >= shelf[None, :]
    far = np.zeros((H, W, 3), np.uint8); far[:] = (24, 13, 16)
    far[(Y - shelf[None, :] < 2) & rock] = (110, 42, 26)
    lit = (gap > .55)[None, :]
    for y0, c in ((571, (120, 40, 24)), (572, (210, 84, 32)), (573, (240, 120, 44)), (574, (170, 56, 28))):
        far[y0][lit[0]] = c
    L['far'].paste(rgba(far, rock & (Y < 600)))

    # ── ceiling: 두꺼운 암반 + 크기가 다양한 종유석, 아래 가장자리만 용암빛 림 ──
    base = 92 + 24 * np.sin(np.arange(W) * 2*math.pi*3/W + 1) + 14 * np.sin(np.arange(W) * 2*math.pi*11/W) \
           + 30 * (vnoise(W, 1, 50, 1, rng)[0] - .5)
    bot = base.copy()
    def near_fall(x, r): return any(abs((x - fx + W/2) % W - W/2) < r for fx in FALLS)
    for _ in range(W // 22):
        cx = int(rng.integers(W))
        if near_fall(cx, 40): continue
        ln = rng.choice([rng.integers(8, 30), rng.integers(8, 30), rng.integers(30, 80), rng.integers(80, 170)])
        wd = max(3, int(ln * rng.uniform(.14, .26)) + 2)
        for d in range(-wd, wd + 1):
            x = (cx + d) % W; bot[x] = max(bot[x], base[x] + ln * (1 - abs(d)/wd) ** 1.5)
    for fx in FALLS:   # 폭포 출구: 천장이 살짝 내려온 바위턱
        for d in range(-50, 51): x = (fx + d) % W; bot[x] = max(bot[x], base[x] + 22 * (1 - (d/50)**2))
    ceil = Y < bot[None, :].astype(int)
    edge = np.zeros((H, W), int) + 99
    m = ceil.copy()
    for k in range(6):   # 아래/좌/우 방향 침식으로 가장자리 거리
        inner = m & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1)
        edge[m & ~inner & (edge == 99)] = k; m = inner
    cdet = fbm(W, H, 48, 3, rng)
    cband = ((Y + 40 * (warp - .5)) / 18) % 1
    Ic = .16 + .22 * (cdet - .5) - .08 * (cband < .12)
    lb = np.clip(.45 + .8 * boost, 0, 1.3)
    Ic = Ic + np.where(edge <= 3, (4 - edge) / 4 * .32 * lb, 0)
    crgb = ramp(Ic, [(7, 5, 8), (12, 8, 11), (18, 11, 14), (27, 15, 17), (44, 21, 21), (72, 31, 25), (110, 44, 28)])
    crgb[(edge == 0) & (lb <= .95)] = (46, 22, 22)   # 모든 종유석 아래 가장자리에 희미한 반사광
    crgb[(edge <= 1) & (lb > .95)] = (88, 36, 26)
    L['ceiling'].paste(rgba(crgb, ceil))

    # ── mid: 음영 있는 암석 기둥(천장~바닥)과 석순 ──
    mask = np.zeros((H, W), bool); U = np.zeros((H, W))
    shapes = [('pillar', 700), ('pillar', 1820), ('pillar', 2900), ('mite', 90), ('mite', 520), ('mite', 1030),
              ('mite', 1480), ('mite', 2150), ('mite', 2470), ('mite', 3080), ('mite', 1640)]
    for kind, cx in shapes:
        cx += int(rng.integers(-30, 31))
        fx = min(FALLS, key=lambda f: abs((f - cx + W/2) % W - W/2)); sgn = 1 if ((fx - cx + W/2) % W - W/2) > 0 else -1
        jit = vnoise(1, H, 1, 30, rng)[:, 0] * 8 - 4
        if kind == 'pillar':
            y0 = 0; wt, wm, wb = rng.integers(60, 84), rng.integers(28, 40), rng.integers(60, 90)
        else:
            h = int(rng.integers(90, 250)); y0 = GROUND_TOP + 6 - h; wb = int(rng.integers(24, 50))
        for y in range(max(0, y0), GROUND_TOP + 8):
            if kind == 'pillar':
                t = y / GROUND_TOP; hw = wm + ((wt - wm) * (1 - 2*t)**2 if t < .5 else (wb - wm) * (2*t - 1)**2)
            else:
                t = (GROUND_TOP + 6 - y) / h; hw = wb * max(0, 1 - t) ** 1.2 + 1
            hw += jit[y]; c = cx + 3 * math.sin(y * .03 + cx)
            if hw < 1: continue
            for x in range(int(c - hw), int(c + hw) + 1):
                mask[y, x % W] = True; U[y, x % W] = sgn * (x - c) / hw
    mdet = fbm(W, H, 80, 3, rng)
    mband = ((Y + 30 * (detail - .5)) / 14) % 1
    Lm = np.clip((Y - 80) / 510, 0, 1)
    side = np.clip(U, -1, 1)
    Im = .2 + .26 * Lm + .2 * np.clip(side, 0, 1) ** 2 * (.4 + Lm) + .16 * (mdet - .5) - .07 * (mband < .14) - .06 * np.clip(-side, 0, 1)
    mrgb = ramp(Im, [(10, 6, 9), (16, 10, 13), (24, 14, 17), (34, 18, 20), (48, 23, 22), (68, 30, 25), (94, 40, 28)])
    rim = mask & (side > .84) & (Lm > .3)
    mrgb[rim] = np.where(((side[rim] > .93) & (Lm[np.nonzero(rim)[0], 0] > .6))[:, None], [176, 74, 38], [100, 42, 28])
    # 바깥 1px 윤곽(어두운) → 벽에서 분리
    outline = mask & ~(np.roll(mask, 1, 1) & np.roll(mask, -1, 1))
    mrgb[outline & ~rim] = (9, 6, 8)
    L['mid'].paste(rgba(mrgb, mask))

    # ── lavafalls: 천장 균열에서 나와 바닥 웅덩이로 ──
    for fx in FALLS:
        top = int(bot[fx % W]) - 12; h = GROUND_TOP + 2 - top
        spr = fall_sprite(h, 0)
        a = np.array(spr); sub = ceil[top:top+h, :]; xx = (np.arange(FW) + fx - FW // 2) % W
        a[..., 3][sub[:, xx] & (a[..., 3] < 255)] = 0   # 헤일로는 천장 암반 위에 번지지 않게
        spr = Image.fromarray(a, 'RGBA')
        x = (fx - FW // 2) % W   # x 랩 붙이기
        L['lavafalls'].alpha_composite(spr.crop((0, 0, min(FW, W - x), h)), (x, top))
        if x + FW > W: L['lavafalls'].alpha_composite(spr.crop((W - x, 0, FW, h)), (0, top))

    # ── embers: 용암원 근처에서만, 2x2 주황/빨강 + 희미한 광 ──
    E = np.zeros((H, W, 4), np.uint8)
    cols = [(255, 120, 40), (240, 80, 30), (255, 150, 56)]
    pts = []
    for fx in FALLS:
        for _ in range(16):
            pts.append((fx + rng.normal(0, 55), GROUND_TOP - 14 - rng.exponential(80)))
    for _ in range(14):   # 용암강 위에서 피어오르는 소수
        pts.append((rng.uniform(0, W), 560 - rng.exponential(30)))
    for x, y in pts:
        x, y = int(x) % W, int(y)
        if y < 200: continue
        c = cols[int(rng.integers(3))]
        for ox, oy in ((-1, 0), (2, 0), (0, -1), (1, -1), (0, 2), (1, 2)):
            E[y + oy, (x + ox) % W] = (200, 56, 22, 70)
        for ox in (0, 1):
            for oy in (0, 1): E[y + oy, (x + ox) % W] = c + (255,)
    L['embers'].paste(Image.fromarray(E, 'RGBA'))
    return bot
