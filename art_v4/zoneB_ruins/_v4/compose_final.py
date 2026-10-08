"""Zone B 폐허 도시 — v3 합성기.

출력
  layers/*.png + manifest.json : Unity용 무보정(낮) 투명 레이어. 시간대는 런타임 SpriteRenderer.color.
  zoneB_{tod}_FULL.png, view_{tod}.png, view_{tod}_1x(_clean).png : 미리보기(그레이딩은 미리보기 전용).

픽셀 스케일 규칙 (mixel 금지)
  - 플레이면(ground/props/char/monster)·mid·clouds 는 전부 1x 네이티브. 확대 금지.
  - x2 NEAREST 는 far 의 원경 스카이라인 한 장만 허용: 하늘색 haze .68 로 대비가 거의 없어
    픽셀 크기가 읽히지 않는다. 그 외 에셋은 올바른 크기로 PixelLab 재생성해서 1x 로 쓴다.
  - 구름은 1x (프롭보다 굵은 픽셀 금지).

루프: 모든 3200px 레이어는 wrap 합성(x mod W)으로 좌우가 이어진다. check_seams() 참고.
HUD 안전영역: 상단 80px / 하단 110px — 지면선 GROUND=596 (< 720-110).
"""
from PIL import Image, ImageEnhance, ImageDraw
import json, os, random, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
from junction import ao, contact, Shadows

SEED, W, H = 88, 3200, 720
GROUND = 596                      # 발 딛는 선
VIEW_X = 800
MON_DIR = '../../unity_client/Assets/Resources/Sprites/Monsters'
os.makedirs('layers', exist_ok=True)

TOD = {
 'day':   dict(bg=None, obj=None, char=None, sky=None),
 'dawn':  dict(bg =dict(tint=(255,152,92),  a=.34, sat=.92, br=.97),
               obj=dict(tint=(248,152,104), a=.28, sat=.86, br=.80),
               char=dict(tint=(255,178,138),a=.14, sat=.96, br=.92),
               sky=((74,104,178),(255,180,116))),
 'night': dict(bg =dict(tint=(26,40,100),   a=.50, sat=.48, br=.46),
               obj=dict(tint=(38,58,120),   a=.34, sat=.60, br=.70),
               char=dict(tint=(90,110,160), a=.10, sat=.92, br=1.0),   # 캐릭터·몬스터는 약하게 → 가독성
               sky=((7,10,34),(30,40,86))),
}

def grade(img, p):
    if p is None: return img
    out = ImageEnhance.Color(img.convert('RGB')).enhance(p['sat'])
    out = ImageEnhance.Brightness(out).enhance(p['br'])
    out = Image.blend(out, Image.new('RGB', out.size, p['tint']), p['a'])
    r = out.convert('RGBA'); r.putalpha(img.split()[3]); return r

def load(n):
    im = Image.open(n).convert('RGBA'); return im.crop(im.getbbox())

def blank(): return Image.new('RGBA', (W, H), (0,0,0,0))

def wpaste(layer, im, x, y):
    """wrap 합성: 오른쪽 끝을 넘으면 왼쪽으로 이어 붙인다 → 무이음 루프."""
    x %= W
    layer.alpha_composite(im, (x, y))
    if x + im.width > W: layer.alpha_composite(im, (x - W, y))

class WrapDraw:
    """ImageDraw 래퍼: 모든 도형을 x, x-W 두 번 그려 좌우 끝이 이어지게 한다."""
    def __init__(self, im): self.d = ImageDraw.Draw(im)
    def __getattr__(self, name):
        f = getattr(self.d, name)
        def call(xy, *a, **k):
            flat = [c for p in xy for c in (p if isinstance(p, tuple) else (p,))]
            for sh in (0, -W):
                f([v + sh if i % 2 == 0 else v for i, v in enumerate(flat)], *a, **k)
        return call

SKY_DAY_TOP, SKY_DAY_BOT = (120,150,168), (222,212,190)   # 먼지 낀 청회색 → 모래빛 지평선
def sky_strip(top, bot):
    g = Image.new('RGBA', (1, GROUND))
    for y in range(GROUND):
        t = (y / (GROUND-1)) ** 1.4
        g.putpixel((0, y), tuple(int(top[i]+(bot[i]-top[i])*t) for i in range(3)) + (255,))
    return g

HORIZON = SKY_DAY_BOT
def haze(im, k):
    rgb = Image.blend(im.convert('RGB'), Image.new('RGB', im.size, HORIZON), k)
    r = rgb.convert('RGBA'); r.putalpha(im.split()[3]); return r

# ── 레이어 생성 ───────────────────────────────────────────────
def make_clouds(rnd):
    L = blank(); d = ImageDraw.Draw(L)
    cloud = load('cloud.png')
    x = rnd.randint(0, 200)
    while x < W:
        c = cloud.transpose(Image.FLIP_LEFT_RIGHT) if rnd.random() < .5 else cloud
        wpaste(L, c, x, rnd.randint(100, 300)); x += rnd.randint(260, 520)
    # 까마귀 떼: 3~5px 실루엣
    for _ in range(3):
        fx, fy = rnd.randrange(W), rnd.randint(110, 260)
        for _ in range(rnd.randint(3, 6)):
            bx, by = fx + rnd.randint(-40, 40), fy + rnd.randint(-14, 14)
            shape = rnd.choice([((0,0),(1,1),(2,0)), ((0,0),(1,1),(2,1),(3,0),(4,-1)), ((0,-1),(1,0),(2,1),(3,0),(4,0),(5,-1))])
            for px_, py_ in shape: d.point(((bx+px_) % W, by+py_), fill=(52,50,54,255))
    return L

def make_far(rnd):
    L = blank()
    # 원경 스카이라인: x2 허용(haze .68, 규칙 참고). 400*2=800 → 3200/800 = 4장 정확히 루프.
    sl = haze(load('skyline.png').resize((800, 224), Image.NEAREST), .68)
    sky_y = GROUND - sl.height - 30
    # 연기 기둥·크레인: 1x, 스카이라인 뒤
    smoke = load('smoke_v3.png'); smoke = smoke.crop((0, 0, smoke.width, 200))
    alpha = smoke.split()[3]; sa = alpha.load()
    for y in range(24):          # 윗단 3계단 페이드(캔버스에 잘린 평평한 윗선 숨김)
        for x_ in range(smoke.width): sa[x_, y] = int(sa[x_, y] * (y // 8 + 1) / 4)
    smoke.putalpha(alpha)
    for sx in (1240, 2620):
        s = haze(smoke, .45); s.putalpha(s.split()[3].point(lambda v: v*.85))
        wpaste(L, s, sx, sky_y - 120)   # 밑동은 스카이라인 뒤로 숨김
    crane = haze(load('crane_v3.png'), .60)
    for cx in (1350, 2850): wpaste(L, crane, cx, GROUND - crane.height - 60)
    for i in range(4): wpaste(L, sl, i*800, sky_y)
    ImageDraw.Draw(L).rectangle([0, sky_y+sl.height-4, W, GROUND+2], fill=haze(Image.new('RGBA',(1,1),(92,90,88,255)), .55).getpixel((0,0)))
    return L

def make_mid(rnd):
    """1x 네이티브. 창 크기/간격/결손·무너진 층·들쭉날쭉 실루엣을 건물마다 다르게."""
    band_h = 280
    im = Image.new('RGBA', (W, band_h), (0,0,0,0)); d = WrapDraw(im)
    rust = (120,70,48,255)
    x = 0
    while x < W:
        bw, bh = rnd.randint(70, 180), rnd.randint(110, 260)
        v = rnd.randint(-10, 10)
        body = (78+v, 72+v, 70+v, 255); dark = (54+v, 50+v, 51+v, 255); lite = (98+v, 92+v, 86+v, 255)
        top = band_h - bh
        # 윤곽: 한쪽이 크게 무너진 사선 + 잘게 깨진 윗선
        slope = rnd.choice([0, 0, 1, -1])
        pts = [(x, band_h)]; cx = x
        while cx <= x + bw:
            t = (cx - x) / bw
            drop = int((t if slope > 0 else (1-t) if slope < 0 else 0) * bh * rnd.uniform(.3, .6))
            pts.append((cx, top + drop + rnd.randint(0, 10)))
            cx += rnd.randint(3, 14)
        pts.append((x + bw, band_h))
        d.polygon(pts, fill=body)
        # 층: 층고·창 크기를 건물마다 다르게, 일부 층은 무너져 비어 있음
        fh = rnd.randint(16, 26); ww, wh = rnd.randint(5, 11), rnd.randint(7, fh-6); gap = rnd.randint(4, 12)
        fy = top + rnd.randint(10, 20)
        while fy < band_h - 18:
            if rnd.random() < .12:        # 무너진 층: 슬래브 끊기고 뻥 뚫림
                hx = x + rnd.randint(0, bw//2); d.rectangle([hx, fy+2, hx + rnd.randint(14, bw//2), fy+fh-3], fill=(0,0,0,0))
            else:
                d.line([(x+1, fy), (x+bw-2, fy)], fill=lite)
                wx = x + rnd.randint(3, 8)
                while wx + ww < x + bw - 3:
                    r = rnd.random()
                    if r < .55:   d.rectangle([wx, fy+3, wx+ww, fy+3+wh], fill=(0,0,0,0))      # 창 구멍(하늘 비침)
                    elif r < .8:  d.rectangle([wx, fy+3, wx+ww, fy+3+wh], fill=dark)
                    wx += ww + gap + rnd.randint(-2, 3)
            fy += fh
        d.line([(x+bw-1, top+14), (x+bw-1, band_h)], fill=dark)
        for _ in range(rnd.randint(2, 6)):   # 철근
            rx = rnd.randint(x+2, x+bw-2); ry = top + rnd.randint(0, 24)
            d.line([(rx, ry), (rx + rnd.randint(-4, 4), ry - rnd.randint(5, 14))], fill=rust)
        x += bw + rnd.randint(-10, 40)
    for _ in range(W // 30):   # 밑동 잔해 더미
        rx, rw, rh = rnd.randint(0, W+300), rnd.randint(18, 60), rnd.randint(8, 22)
        d.polygon([(rx, band_h), (rx+rw//3, band_h-rh), (rx+rw//2, band_h-rh+4), (rx+2*rw//3, band_h-rh-3), (rx+rw, band_h)], fill=(58,54,54,255))
    # 잔해 턱: 띠 밑동이 직선으로 끝나지 않게 끊김 없는 울퉁불퉁 잔해 줄 (신규 RNG → 기존 배치 불변)
    r2 = random.Random(SEED + 60); x = 0
    while x < W:
        rw, rh = r2.randint(6, 20), r2.randint(4, 14); c = r2.choice([(58,54,54,255), (66,62,60,255), (50,47,48,255)])
        d.polygon([(x, band_h), (x + rw//3, band_h - rh), (x + 2*rw//3, band_h - rh + r2.randint(-2, 3)), (x + rw, band_h)], fill=c)
        d.line([(x + rw//3, band_h - rh), (x + 2*rw//3, band_h - rh)], fill=(84,80,76,255))
        x += rw - r2.randint(1, 4)
    d.rectangle([0, band_h - 3, W, band_h], fill=(52,49,50,255))
    band = haze(im, .22)
    L = blank(); L.alpha_composite(band, (0, GROUND - band_h + 4)); return L

def make_ground(rnd):
    """아스팔트 상판 + 연석 + 불규칙 콘크리트/잔해 채움 (벽돌 격자 없음). 전부 wrap."""
    gh = H - GROUND
    im = Image.new('RGBA', (W, gh), (70,68,66,255)); d = WrapDraw(im)
    # 큰 슬래브 조각: 크기·기울기·명도 제각각
    for _ in range(W // 4):
        cx, cy = rnd.randint(0, W+180), rnd.randint(20, gh)
        w_, h_ = rnd.choice([4, 6, 8, 10, 14, 22]), rnd.randint(3, 9)
        v = rnd.randint(-10, 10); c = (74+v, 71+v, 68+v, 255)
        j = lambda: rnd.randint(-3, 3)
        d.polygon([(cx+j(), cy+h_), (cx+2+j(), cy+j()), (cx+w_//2, cy-2+j()), (cx+w_-2+j(), cy+j()), (cx+w_+j(), cy+h_)], fill=c)
        d.line([(cx+2, cy+1), (cx+w_-3, cy+1)], fill=(c[0]+9, c[1]+9, c[2]+8, 255))   # 윗면 하이라이트
    for _ in range(W // 3):   # 자갈
        px_, py_ = rnd.randint(0, W+199), rnd.randint(22, gh-1)
        v = rnd.choice([-22, -12, 16, 28]); d.point((px_, py_), fill=(76+v, 73+v, 70+v, 255))
    for _ in range(W // 70):   # 녹물·철근
        cx, cy = rnd.randint(0, W+180), rnd.randint(30, gh)
        d.line([(cx, cy), (cx + rnd.randint(-12, 12), cy + rnd.randint(-5, 5))], fill=(118,68,44,255), width=2)
    # 상판(아스팔트) + 균열 + 연석
    d.rectangle([0, 0, W+200, 11], fill=(60,60,64,255))
    d.line([(0, 1), (W+200, 1)], fill=(112,112,116,255))
    for _ in range(W // 45):
        cx = rnd.randint(0, W+190); pts = [(cx, 2)]
        for _ in range(3): pts.append((pts[-1][0] + rnd.randint(-4, 4), pts[-1][1] + 3))
        d.line(pts, fill=(36,36,40,255))
    d.rectangle([0, 12, W+200, 18], fill=(146,142,134,255))
    d.line([(0, 19), (W+200, 19)], fill=(84,80,76,255))
    x = 0
    while x < W:  # 연석 줄눈: 불규칙 간격, 일부 깨짐
        d.line([(x, 12), (x, 18)], fill=(108,104,98,255))
        if rnd.random() < .25: d.polygon([(x+3, 12), (x+9, 12), (x+6, 16)], fill=(90,86,82,255))
        x += rnd.randint(34, 70)
    for _ in range(W // 700):  # 물웅덩이
        px_, pw = rnd.randint(100, W-200), rnd.randint(50, 110)
        d.ellipse([px_, 3, px_+pw, 9], fill=(72,84,98,255)); d.line([(px_+pw//4, 5), (px_+pw//2, 5)], fill=(140,156,170,255))
    # 아래로 갈수록 살짝 어둡게(깊이) — 3단 계단
    for i, a in enumerate((30, 55, 80)):
        ov = Image.new('RGBA', (W, gh - 40 - i*28), (24,22,22,a//3)); im.alpha_composite(ov, (0, 40 + i*28))
    L = blank(); L.alpha_composite(im, (0, GROUND)); return L

def bulb_center(lamp):
    """lm 램프 유리(밝은 청백색 픽셀) 무게중심 = 전구 위치."""
    px = lamp.load(); xs = ys = n = 0
    for y in range(min(80, lamp.height)):
        for x in range(lamp.width):
            r, g, b, a = px[x, y]
            if a > 200 and r + g + b > 520: xs += x; ys += y; n += 1
    assert n, 'bulb not found'
    return xs / n, ys / n

def make_props(rnd):
    back, front, glow = blank(), blank(), blank()
    occupied = []
    def free(x0, x1, pad): return all(x1+pad <= a or x0-pad >= b for a, b in occupied)
    SH = Shadows(W*.47); bases = []; grime = []
    def place(layer, im, x, sink=5, flip=False, pad=12, reserve=True, wt=.5):
        s = im.transpose(Image.FLIP_LEFT_RIGHT) if flip else im
        x = int(x)
        if reserve and not free(x, x+s.width, pad): return None
        heavy = im is facade or im is car
        s = ao(s, .5 if heavy else .64)
        layer.alpha_composite(s, (x, GROUND - s.height + sink))
        if reserve: occupied.append((x, x+s.width))
        SH.add(x, GROUND + 3, s, wt); c0, c1 = contact(s); bases.append((x + c0, x + c1, wt))
        if heavy: grime.append((x + c0, x + c1))
        return s
    # 캐릭터·몬스터 자리 비우기(미리보기 구간)
    occupied.append((VIEW_X+290, VIEW_X+360))   # 캐릭터 발밑만

    DUST = dict(tint=(170,150,124), a=.14, sat=.62, br=.92)   # 폐허 팔레트 맞춤(에셋 룩, 시간대 아님)
    car = None
    facade, lamp, tree = grade(load('facade_v3.png'), DUST), load('lamp_v3.png'), grade(load('tree_v3.png'), DUST)
    bx, by = bulb_center(lamp)
    # 랜드마크: 같은 종류 인접 금지 순서 고정
    lamps = []
    for a, im in ((0.05, facade), (0.19, lamp), (0.36, tree), (0.50, lamp), (0.64, facade), (0.80, lamp), (0.90, tree)):
        flip = im is tree and rnd.random() < .5    # 파사드·램프는 뒤집지 않음(글자/전구 위치 보존)
        for off in (0, 60, -60, 140, -140, 220):
            x = a*W + off
            if place(back, im, x, sink=6 if im is facade else 5, flip=flip, pad=24, wt={id(facade): 1, id(tree): .8}.get(id(im), .5)):
                if im is lamp: lamps.append((x + bx, GROUND - lamp.height + 3 + by))
                break
    car, bench, tl = grade(load('car_v3.png'), DUST), grade(load('bench_v3.png'), DUST), load('tlight_v3b.png')
    sand, rub, bar, cone = load('sandbags.png'), load('rubble.png'), load('barrel.png'), load('cone.png')
    mids = [car, bench, tl, sand, bar, cone, rub]; rnd.shuffle(mids); k = 0; last = None
    x = rnd.randint(60, 160)
    while x < W - 260:
        im = mids[k % len(mids)]
        if im is last: k += 1; continue
        if place(back, im, x, sink=5, flip=rnd.random() < .5, wt=.9 if im is car else .5): last = im; k += 1
        x += rnd.randint(70, 160)
    # 앞 레이어: 캐릭터 앞을 지나가는 낮은 잡초/잔해만 (가림 최소)
    weeds = load('weeds.png'); x = rnd.randint(20, 80)
    while x < W - 70:
        if rnd.random() < .6: place(front, weeds, x, sink=6, flip=rnd.random() < .5, reserve=False, wt=.1)
        x += rnd.randint(140, 320)
    # 발광: 전구 중심에 작고 부드러운 원 + 약한 빛기둥 (tint:false 레이어)
    gd = ImageDraw.Draw(glow)
    for lx, ly in lamps:
        lx, ly = int(lx), int(ly)
        gd.polygon([(lx-6, ly+6), (lx+6, ly+6), (lx+44, GROUND+2), (lx-44, GROUND+2)], fill=(255,210,140,12))
        for r, al in ((34, 14), (20, 26), (10, 60), (4, 170)):
            gd.ellipse([lx-r, ly-r, lx+r, ly+r], fill=(255,216,150,al))
    # ground_front: 잔해 조각·균열·잡초를 바닥 앞에 → 이음새 숨김.  grime: 파사드/차 아래 오염선(지면에 굽는다)
    r2 = random.Random(SEED + 61); gf = blank(); fp = gf.load()
    chips = [(92,88,84,255), (112,108,100,255), (70,67,66,255), (60,60,64,255)]
    weed = [(78,88,52,255), (96,104,58,255), (62,70,44,255)]
    def chip(x):
        w_, y0 = r2.randint(2, 4), GROUND + r2.randint(1, 4); c = r2.choice(chips)
        for dx in range(w_):
            for dy in range(r2.randint(1, 2)): fp[(x+dx) % W, y0 - dy] = c
        fp[x % W, y0 - 2] = (130,126,118,255)
    def blade(x, hmax):
        h = r2.randint(2, hmax)
        for i in range(h): fp[(x + (r2.choice((0, 1, -1)) if i == h-1 else 0)) % W, GROUND + 3 - i] = r2.choice(weed)
    for a, b, wt in bases:
        for x in range(a - 6, b + 7):
            r = r2.random()
            if r < .22: chip(x)
            elif r < .34: blade(x, 3 + round(wt * 3))
        for _ in range(max(1, (b - a) // 30)):                      # 바닥 앞 아스팔트 균열
            x, y = r2.randint(a - 4, b + 4), GROUND + 2
            for _ in range(r2.randint(3, 6)): fp[x % W, y] = (34,34,38,255); x += r2.choice((-1, 1)); y += r2.choice((0, 1))
    ch_x = (VIEW_X + 296, VIEW_X + 380)
    for x in range(W):
        if r2.random() < .05: (chip if r2.random() < .6 else (lambda x: blade(x, 3)))(x) if not ch_x[0] <= x <= ch_x[1] else None
    return back, front, glow, gf, SH, grime

def make_stars(rnd):
    L = blank(); px = L.load()
    for _ in range(W // 7):
        sx, sy = rnd.randrange(W), rnd.randrange(0, 380)
        b = rnd.choice([170, 200, 240])
        for dx, dy in ((0,0),(1,0),(0,1)): px[(sx+dx) % W, sy+dy] = (b, b, min(255, b+18), 255)
    return L

# ── 몬스터 가독성 테스트용 ───────────────────────────────────
def monster(t):
    return load(f'{MON_DIR}/t{t}/walk/frame_00.png').transpose(Image.FLIP_LEFT_RIGHT)  # 캐릭터 쪽을 보게

def check_seams(img):
    """루프 검증: FULL 을 두 장 이었을 때 이음새 좌우 열 차이(평균)."""
    a, b = img.crop((W-1, 0, W, GROUND)), img.crop((0, 0, 1, GROUND))
    return sum(abs(p - q) for pa, pb in zip(a.getdata(), b.getdata()) for p, q in zip(pa, pb)) / GROUND

# ── 실행 ────────────────────────────────────────────────────
rnd = random.Random(SEED)
L = dict(clouds=make_clouds(random.Random(SEED+1)), far=make_far(random.Random(SEED+2)),
         mid=make_mid(random.Random(SEED+7)), ground=make_ground(random.Random(SEED+3)))
L['props_back'], L['props_front'], L['lamp_glow'], L['ground_front'], SHADOWS, GRIME = make_props(random.Random(SEED+5))
gd_ = ImageDraw.Draw(L['ground'])
for a, b in GRIME:                                                   # 파사드/차 오염선: 2px 짙은 줄 + 디더 번짐
    gd_.rectangle([a, GROUND + 1, b, GROUND + 2], fill=(30,28,28,255))
    for x in range(a - 2, b + 3, 2): L['ground'].putpixel((x % W, GROUND + 3), (44,42,42,255))
stars = make_stars(random.Random(SEED+9))
sky_day = sky_strip(SKY_DAY_TOP, SKY_DAY_BOT)

# Unity 레이어 export (무보정 낮 기준)
sky_day.resize((16, GROUND)).save('layers/sky.png')
for n, im in L.items(): im.save(f'layers/{n}.png')
SHADOWS.render('day', col=(6, 4, 8)).save('layers/shadows.png')
stars.save('layers/stars.png')
load('sun.png').save('layers/sun.png'); load('moon.png').save('layers/moon.png')
manifest = dict(zone='B_ruins', width=W, height=H, ground_y_from_top=GROUND, pixels_per_unit=40,
  note='Untinted day layers. Apply time-of-day via SpriteRenderer.color on tint:true layers only. '
       'sky.png is a 16px-wide gradient; stretch to view width. Loop: all 3200px layers tile seamlessly. '
       'stars/moon: night only; sun: dawn only; lamp_glow: enable at night (emissive).',
  layers=[
  dict(name='sky',        file='sky.png',        parallax=0.0,  z=100, sortingOrder=-100, tint=True,  stretch=True),
  dict(name='stars',      file='stars.png',      parallax=0.0,  z=95,  sortingOrder=-95,  tint=False, tod=['night']),
  dict(name='moon',       file='moon.png',       parallax=0.0,  z=94,  sortingOrder=-94,  tint=False, tod=['night'], pos=[0.30, 78]),
  dict(name='sun',        file='sun.png',        parallax=0.0,  z=94,  sortingOrder=-94,  tint=False, tod=['dawn'], pos=[0.47, 300]),
  dict(name='clouds',     file='clouds.png',     parallax=0.05, z=90,  sortingOrder=-90,  tint=True),
  dict(name='far',        file='far.png',        parallax=0.15, z=80,  sortingOrder=-80,  tint=True),
  dict(name='mid',        file='mid.png',        parallax=0.35, z=60,  sortingOrder=-60,  tint=True),
  dict(name='ground',     file='ground.png',     parallax=1.0,  z=20,  sortingOrder=-20,  tint=True),
  dict(name='shadows',    file='shadows.png',    parallax=1.0,  z=15,  sortingOrder=-15,  tint=False, note='contact shadows (day). dawn: alpha x.85, width x1.35, shifted away from sun; night: alpha x.5. actors: runtime blob shadow same style'),
  dict(name='props_back', file='props_back.png', parallax=1.0,  z=10,  sortingOrder=-10,  tint=True),
  dict(name='lamp_glow',  file='lamp_glow.png',  parallax=1.0,  z=9,   sortingOrder=-9,   tint=False, tod=['night']),
  dict(name='props_front',file='props_front.png',parallax=1.0,  z=-10, sortingOrder=10,   tint=True, note='in front of player'),
  dict(name='ground_front',file='ground_front.png',parallax=1.0, z=-12, sortingOrder=12,   tint=True, note='debris/cracks/weeds over object & actor bases'),
])
json.dump(manifest, open('layers/manifest.json', 'w'), indent=1, ensure_ascii=False)

ch = load('char_idle.png')
mons = [(monster(9), VIEW_X+560), (monster(11), VIEW_X+760), (monster(13), VIEW_X+940)]

def compose(tod, with_mon):
    P = TOD[tod]
    cv = Image.new('RGBA', (W, H), (0,0,0,255))
    sk = sky_day if P['sky'] is None else sky_strip(*P['sky'])
    cv.alpha_composite(sk.resize((W, GROUND), Image.BILINEAR))
    cv = grade(cv, P['bg']) if P['sky'] is None else cv   # 새벽/밤 하늘은 다시 칠함
    if tod == 'night': cv.alpha_composite(stars)
    if tod == 'dawn':
        sun = load('sun.png'); cv.alpha_composite(sun, (int(W*.47), 300))
    if tod == 'night':
        mo = load('moon.png'); cv.alpha_composite(mo, (int(W*.30), 90))
    clouds = L['clouds']
    if tod == 'night': clouds = clouds.copy(); clouds.putalpha(clouds.split()[3].point(lambda v: int(v*.3)))
    for n, im in (('clouds', clouds), ('far', L['far']), ('mid', L['mid'])): cv.alpha_composite(grade(im, P['bg']))
    cv.alpha_composite(grade(L['ground'], P['obj']))
    SH = Shadows(SHADOWS.sun_x); SH.s = list(SHADOWS.s); ach = ao(ch, .72, 4)   # 액터 그림자/AO 는 미리보기용
    SH.add(VIEW_X+300, GROUND + 3, ach, .55)
    if with_mon:
        for m, mx in mons: SH.add(mx, GROUND + 3, m, .7)
    cv.alpha_composite(SH.render(tod, col=(6, 4, 8)))
    cv.alpha_composite(grade(L['props_back'], P['obj']))
    if tod == 'night': cv.alpha_composite(L['lamp_glow'])        # 그레이딩 뒤 발광
    gch = grade(ach, P['char'])
    cv.alpha_composite(gch, (VIEW_X+300, GROUND - ch.height + 2))
    if with_mon:
        for m, mx in mons: cv.alpha_composite(grade(ao(m, .72, 4), P['char']), (mx, GROUND - m.height + 2))
    cv.alpha_composite(grade(L['props_front'], P['obj']))
    cv.alpha_composite(grade(L['ground_front'], P['obj']))
    return cv.convert('RGB')

for tod in ('day', 'dawn', 'night'):
    full = compose(tod, False)
    full.save(f'zoneB_{tod}_FULL.png')
    full.resize((1920, 432), Image.LANCZOS).save(f'view_{tod}.png')
    full.crop((VIEW_X, 0, VIEW_X+1280, H)).save(f'view_{tod}_1x_clean.png')
    compose(tod, True).crop((VIEW_X, 0, VIEW_X+1280, H)).save(f'view_{tod}_1x.png')
    print(tod, 'seam diff', round(check_seams(full), 1))
v = Image.open('view_day_1x.png')                                      # 접합 확인: 캐릭터 + 인접 랜드마크, x2
v.crop((100, GROUND - 170, 740, GROUND + 70)).resize((1280, 480), Image.NEAREST).save('view_ground_zoom.png')
