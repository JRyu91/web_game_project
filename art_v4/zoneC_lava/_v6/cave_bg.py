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

def strip_border(a):
    """배경색이 남아 있으면 테두리에서 플러드필로 제거 (알파 0 처리)."""
    from collections import deque
    h, w = a.shape[:2]; bg = a[0, 0, :3]; seen = np.zeros((h, w), bool)
    q = deque([(y, x) for y in (0, h-1) for x in range(w)] + [(y, x) for x in (0, w-1) for y in range(h)])
    while q:
        y, x = q.popleft()
        if seen[y, x]: continue
        seen[y, x] = True
        if a[y, x, 3] > 128 and np.abs(a[y, x, :3] - bg).sum() > 30: continue
        a[y, x, 3] = 0
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and not seen[ny, nx]: q.append((ny, nx))
    return a

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
    # 단층: x 구간마다 층리를 계단식으로 어긋나게 (주기 W로 닫힌 구간)
    nf = 14; fshift = rng.integers(-22, 23, nf)
    fault = fshift[(np.arange(W) * nf // W)][None, :]
    thick = 18 + 22 * fbm(W, H, 6, 2, rng)                       # 층 두께 변화
    yy = Y + 130 * (warp - .5) + 18 * (detail - .5) + fault
    ph = yy / thick                                               # 두께 가변 위상
    band = np.floor(ph).astype(int); f = ph - band
    bright = rng.random(400) * .14
    pinch = fbm(W, H, 20, 2, rng)                                  # 층 경계가 사라지는 곳
    ridge = (np.abs(fbm(W, H, 56, 2, rng) - .5) < .01) & (fbm(W, H, 12, 2, rng) > .6)
    seam = ((f < .07) & (pinch > .5)) * 1.0
    v = .42 + bright[(band + (yy // 60).astype(int)) % 400] + .34 * (detail - .5) + .06 * (f > .85) * (pinch > .45) - .05 * seam
    # 바위 덩어리: 음영된 타원 블롭(위-오른쪽 밝게 X, 아래가 밝다 = 아래 조명)
    for _ in range(W // 70):
        bx, by = int(rng.integers(W)), int(rng.integers(150, 540))
        rx, ry = int(rng.integers(14, 40)), int(rng.integers(10, 26))
        xs = (np.arange(bx - rx, bx + rx + 1)) % W; ys = np.arange(max(0, by - ry), min(H, by + ry + 1))
        dx = (np.arange(-rx, rx + 1) / rx)[None, :]; dy = ((ys - by) / ry)[:, None]
        r = dx**2 + dy**2 + .25 * (detail[ys][:, xs] - .5)
        inside = r < 1
        sub = v[np.ix_(ys, xs)]
        sub = np.where(inside, .46 + .12 * dy + .1 * (1 - r) + .2 * (detail[ys][:, xs] - .5), sub)
        sub = np.where((r >= 1) & (r < 1.12) & (dy > -.2), sub - .12, sub)   # 아래쪽 접지 그림자
        v[np.ix_(ys, xs)] = sub
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
    # 용암 웅덩이/강: 높이가 굽이치고, 끊겨서 웅덩이로, 가장자리 불규칙
    surf = 566 + 9 * (vnoise(W, 1, 12, 1, rng)[0] - .5) * 2 + 3 * np.sin(np.arange(W) * 2*math.pi*37/W)
    depth = np.clip((gap - .42) * 30, 0, 9) + 2 * (vnoise(W, 1, 160, 1, rng)[0] - .5)   # 0이면 끊김
    en = vnoise(W, H, 300, 60, rng)
    d = (Y - surf[None, :])                        # 표면으로부터 거리
    liq = (d >= 0) & (d < depth[None, :] + 1.5 * (en - .5) * 2) & (depth[None, :] > .5)
    core = liq & (d >= 1) & (d < depth[None, :] * .5)
    far[liq] = (196, 70, 30); far[core] = (246, 132, 48)
    far[liq & (d < 1)] = (255, 196, 96)            # 밝은 표면
    # 웅덩이 위 글로(계단형 암석 반사, 불투명 색만) → 불규칙 가장자리
    near = (~liq) & rock & (d < 0) & (d > -7 + 3 * en) & (depth[None, :] > 1.5)
    far[near] = (84, 34, 26)
    edge = (~liq) & rock & (d >= 0) & (d < depth[None, :] + 3) & (depth[None, :] > .5)
    far[edge] = (120, 44, 26)
    L['far'].paste(rgba(far, rock & (Y < 600)))

    # ── ceiling: 두꺼운 암반 + 크기가 다양한 종유석, 아래 가장자리만 용암빛 림 ──
    base = 118 + 22 * np.sin(np.arange(W) * 2*math.pi*3/W + 1) + 12 * np.sin(np.arange(W) * 2*math.pi*11/W) \
           + 34 * (vnoise(W, 1, 70, 1, rng)[0] - .5)
    bot = base.copy()
    def near_fall(x, r): return any(abs((x - fx + W/2) % W - W/2) < r for fx in FALLS)
    for _ in range(W // 40):   # 굵고 뭉툭한 종유석 (삼각형 대신 둥근 어깨 + 뭉툭한 끝)
        cx = int(rng.integers(W))
        if near_fall(cx, 50): continue
        ln = rng.choice([rng.integers(14, 40), rng.integers(14, 40), rng.integers(40, 90), rng.integers(90, 150)])
        wd = max(8, int(ln * rng.uniform(.3, .45)) + 6)
        for d in range(-wd, wd + 1):
            t = abs(d) / wd
            x = (cx + d) % W; bot[x] = max(bot[x], base[x] + ln * (1 - t ** 1.6) ** 1.3)
    bot += 3 * (vnoise(W, 1, 400, 1, rng)[0] - .5) * 2      # 울퉁불퉁한 윤곽
    for fx in FALLS:   # 폭포 출구: 천장이 살짝 내려온 바위턱
        for d in range(-50, 51): x = (fx + d) % W; bot[x] = max(bot[x], base[x] + 22 * (1 - (d/50)**2))
    ceil = Y < bot[None, :].astype(int)
    du = bot[None, :] - Y                                    # 아래 가장자리까지 거리
    slope = np.abs(np.roll(bot, 1) - np.roll(bot, -1))[None, :] / 2   # 종유석 옆면
    cdet = fbm(W, H, 48, 3, rng)
    clump = fbm(W, H, 14, 2, rng)
    cband = ((Y + 40 * (warp - .5)) / 22) % 1
    Ic = .34 + .55 * (cdet - .5) + .16 * (clump - .5) - .07 * (cband < .12) + .05 * (cband > .8)
    lb = np.clip(.5 + .8 * boost, 0, 1.3)
    under = np.clip(1 - du / 26, 0, 1)                       # 아래에서 비치는 빛 (끝/밑면)
    Ic = Ic + under ** 1.3 * .38 * lb - .06 * np.clip(slope - 1, 0, 3) * (du > 3)
    crgb = ramp(Ic, [(7, 5, 8), (12, 8, 11), (18, 11, 14), (27, 15, 17), (40, 20, 20), (60, 27, 23), (88, 37, 26), (128, 52, 30)])
    crgb[ceil & (du < 1.5) & (lb > .95)] = (150, 60, 32)     # 폭포 근처 끝단 강한 림
    L['ceiling'].paste(rgba(crgb, ceil))

    # ── mid: PixelLab 생성 암석 기둥/석순 (팔레트 재매핑, 밑단은 헤이즈로 디더 페이드) ──
    MPAL = np.array([(10, 6, 9), (16, 10, 13), (24, 14, 17), (34, 18, 20), (48, 23, 22), (68, 30, 25), (94, 40, 28), (124, 52, 32)], float)
    def prep(name, flip):
        a = np.array(Image.open(name).convert('RGBA')).astype(float)
        a = strip_border(a)
        if flip: a = a[:, ::-1]
        r, g, b, al = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
        lava = (r > 200) & (g > 70) & (b < 140) & (r - b > 110)
        lum = (.3*r + .55*g + .15*b) / 255
        idx = np.clip((lum / max(lum[al > 0].max(), 1e-3)) ** .7 * len(MPAL) + 2, 0, len(MPAL) - 1).astype(int)
        out = a.copy(); out[..., :3] = MPAL[idx]   # 발광 구슬 제거: 배우 밴드에 '손에 든 불씨'처럼 보임
        out[..., 3] = np.where(al > 128, 255, 0)
        return out.astype(np.uint8)
    B = np.array([[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]) / 16
    HAZE = np.array((24, 13, 16))
    sprites = [('pl_column.png', 1.0), ('pl_mite.png', 1.0), ('pl_mite.png', .75)]
    places = [(700, 0, 0), (1820, 0, 1), (2900, 0, 0), (90, 1, 0), (520, 2, 1), (1030, 1, 1), (1480, 2, 0),
              (2150, 1, 0), (2470, 2, 1), (3080, 1, 1), (1640, 1, 0)]
    M = np.zeros((H, W, 4), np.uint8)
    for cx, k, fl in places:
        name, sc = sprites[k]
        spr = prep(name, fl)
        if sc != 1:
            spr = np.array(Image.fromarray(spr).resize((int(spr.shape[1]*sc), int(spr.shape[0]*sc)), Image.NEAREST))
            # ponytail: 축소 NEAREST는 1x 규칙 위반 없음(확대 아님)
        ys, xs = np.nonzero(spr[..., 3]); spr = spr[ys.min():ys.max()+1, xs.min():xs.max()+1]
        h, w = spr.shape[:2]
        y0 = GROUND_TOP + 10 - h                              # 밑단은 지면 립 뒤로 숨김
        fade = 0                                             # 밑단 디더 페이드 → 원경 헤이즈
        for yy_ in range(h):
            y = y0 + yy_
            if y < 0 or y >= H: continue
            row = spr[yy_].copy()
            t = np.clip((y - (GROUND_TOP - fade)) / fade, 0, 1) if fade else 0
            if fade and t > 0:
                xsr = (np.arange(w) + cx - w // 2) % W
                mix = (B[y % 4, xsr % 4] < t)[:, None]
                row[..., :3] = np.where(mix, (row[..., :3] * .45 + HAZE * .55).astype(np.uint8), row[..., :3])
            xx = (np.arange(w) + cx - w // 2) % W
            m = row[:, 3] > 0
            M[y, xx[m]] = row[m]
    L['mid'].paste(Image.fromarray(M, 'RGBA'))

    # ── lavafalls: 천장 균열에서 나와 바닥 웅덩이로 ──
    for fx in FALLS:
        top = int(bot[fx % W]) - 40; h = GROUND_TOP + 2 - top
        spr = fall_sprite(h, 0)
        a = np.array(spr); sub = ceil[top:top+h, :]; xx = (np.arange(FW) + fx - FW // 2) % W
        a[..., 3][sub[:, xx] & (a[..., 3] < 255)] = 0   # 헤일로는 천장 암반 위에 번지지 않게
        # 천장 안: 아래로 벌어지는 톱니 쐐기(균열)만 보이게 → 평평한 원판 윗단 제거
        cb = bot[xx].astype(int)                           # 열별 천장 밑선
        jag = (np.arange(FW) * 7 % 5) - 2
        for yy_ in range(h):
            y = top + yy_; up = cb - y                     # 천장 밑선 위로 몇 px
            wedge = np.abs(np.arange(FW) - FW // 2 - 3 * np.sin(up * .35)) <= np.clip(11 - up * .28 + jag * (up > 4), 0, 12)
            crack = (a[yy_, :, 0] == 255) & (a[yy_, :, 1] == 150) | (a[yy_, :, 0] == 140) & (a[yy_, :, 1] == 44)
            hide = (up > 0) & ~wedge & ~crack
            a[yy_, hide, 3] = 0
            rimm = (up > 0) & ~wedge & (np.abs(np.arange(FW) - FW // 2 - 3 * np.sin(up * .35)) <= np.clip(13 - up * .28 + jag * (up > 4), 0, 14))
            a[yy_, rimm] = (120, 40, 22, 255)             # 균열 입구 주변 달아오른 암석
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
            pts.append((fx + rng.normal(0, 40), 420 - rng.exponential(90)))
    for _ in range(14):   # 용암강 위에서 피어오르는 소수
        pts.append((rng.uniform(0, W), 420 - rng.exponential(60)))
    for x, y in pts:
        x, y = int(x) % W, int(y)
        if y < 200 or y >= 430: continue   # 배우 밴드(발라인-164) 위로만
        c = cols[int(rng.integers(3))]
        for ox, oy in ((-1, 0), (2, 0), (0, -1), (1, -1), (0, 2), (1, 2)):
            E[y + oy, (x + ox) % W] = (200, 56, 22, 70)
        for ox in (0, 1):
            for oy in (0, 1): E[y + oy, (x + ox) % W] = c + (255,)
    L['embers'].paste(Image.fromarray(E, 'RGBA'))
    return bot
