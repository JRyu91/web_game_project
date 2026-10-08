"""Zone A '시작의 숲' composer (v3).

PIXEL-SCALE RULE (mixel 방지, 3개 존 공통):
  - 플레이 평면(지면·나무·프롭·캐릭터·몬스터·구름·해/달)은 전부 1x 네이티브 픽셀.
  - 원경(산/언덕)만 x2 NEAREST 허용 — 단, 하늘색으로 헤이즈(35~45%) + 약한 블러를 걸어
    픽셀 경계가 읽히지 않게 한다. 하늘은 그라디언트(픽셀 없음).
  - 구름은 1x 전용 에셋(clouds_v3.png). 절대 확대하지 않는다.

LOOP RULE: 모든 레이어는 가로 3200px 주기로 끊김 없이 이어진다(x=0 과 x=3200 이 맞닿음).
  랜덤 배치는 wrap 합성, 지면 흙은 주기적 노이즈, 산/언덕 타일은 3200의 약수 폭.

출력: zoneA_{tod}_FULL.png / view_{tod}.png / view_{tod}_1x(_clean).png / layers/*.png + manifest.json
"""
from PIL import Image, ImageEnhance, ImageFilter
import random, math, json, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
from junction import ao, contact, Shadows

class Shadows2(Shadows):
    """v4: 고대비 잔디 위에서도 읽히게 — 폭 x1.25, 높이 +2, 알파 x1.55 (junction 동작은 그대로)."""
    def add(self, x, ground_y, im, weight=.5):
        super().add(x, ground_y, im, weight); cx, gy, w, wt = self.s[-1]; self.s[-1] = (cx, gy + 1, int(w * 1.25), min(1.5, wt + .25))
    def render(self, tod='day', **kw):
        L = super().render(tod, **kw); a = L.getchannel('A').point(lambda v: min(235, int(v * 1.55)))
        L.putalpha(a); return L

HERE = os.path.dirname(os.path.abspath(__file__)); os.chdir(HERE)
W, H, VIEW_X, VIEW_W = 3200, 720, 800, 1280
SEED = 88
TILE = 32
GROUND_TOP = H - 3*TILE - 24          # 600
GRASS = GROUND_TOP + 18               # 618 = 발 딛는 선 (하단 HUD 110px → 610 이하가 HUD, 지면선은 그 위)
MONS = '../../unity_client/Assets/Resources/Sprites/Monsters'

TOD = {
 'day':   dict(bg=None, obj=None, char=None, ground=None,
               sky=((104,160,226),(206,230,242))),
 'dawn':  dict(bg =dict(tint=(255,152,92),  a=.30, sat=.92, br=.97),
               obj=dict(tint=(248,152,104), a=.24, sat=.88, br=.84),
               char=dict(tint=(255,178,138),a=.14, sat=.96, br=.92),
               ground=dict(tint=(250,168,120), a=.22, sat=.90, br=.86),
               sky=((74,104,178),(255,180,116))),
 'night': dict(bg =dict(tint=(26,40,100),   a=.50, sat=.48, br=.46),
               obj=dict(tint=(34,54,116),   a=.40, sat=.56, br=.56),
               char=dict(tint=(58,82,148),  a=.16, sat=.86, br=.80),
               ground=dict(tint=(38,58,120), a=.34, sat=.62, br=.66),
               sky=((7,10,34),(30,40,86))),
}

def grade(img, p):
    if p is None: return img
    out = ImageEnhance.Color(img.convert('RGB')).enhance(p['sat'])
    out = ImageEnhance.Brightness(out).enhance(p['br'])
    out = Image.blend(out, Image.new('RGB', out.size, p['tint']), p['a'])
    r = out.convert('RGBA'); r.putalpha(img.split()[3]); return r

def load(n):
    im = Image.open(n).convert('RGBA')
    im.putalpha(im.getchannel('A').point(lambda a: 255 if a >= 128 else 0))  # 반투명 프린지 제거
    return im.crop(im.getbbox())

def wpaste(layer, im, x, y):
    """가로 wrap 합성 — 3200 경계를 넘는 스프라이트는 반대편에도 찍는다."""
    x %= W
    for ox in (x - W, x, x + W):
        if ox < W and ox + im.width > 0: _neg(layer, im, ox, y)

def _neg(layer, im, ox, y):
    # alpha_composite 는 음수 좌표를 못 받으므로 잘라서 합성
    c = im.crop((max(0, -ox), 0, min(im.width, layer.width - ox), im.height))
    if c.width > 0: layer.alpha_composite(c, (max(0, ox), y))

def gradient(top, bot, h):
    g = Image.new('RGB', (1, h))
    for y in range(h):
        t = y / (h - 1); g.putpixel((0, y), tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)))
    return g

def periodic_noise(w, h, cells, rnd):
    """x 방향 주기 w 인 value noise (0..1)."""
    cx, cy = cells, max(2, int(cells * h / w))
    grid = [[rnd.random() for _ in range(cx)] for _ in range(cy + 1)]
    out = [[0.0] * w for _ in range(h)]
    for y in range(h):
        fy = y / h * cy; y0 = int(fy); ty = fy - y0; ty = ty * ty * (3 - 2 * ty)
        for x in range(w):
            fx = x / w * cx; x0 = int(fx); tx = fx - x0; tx = tx * tx * (3 - 2 * tx)
            a, b = grid[y0][x0 % cx], grid[y0][(x0 + 1) % cx]
            c, d = grid[y0 + 1][x0 % cx], grid[y0 + 1][(x0 + 1) % cx]
            out[y][x] = (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty
    return out

# ───────────────────────── layer builders (untinted = day) ─────────────────────────
def build_layers():
    R = random.Random(SEED)
    L = {}
    gset = Image.open('ground_set.png').convert('RGBA')
    cell = lambda r, c: gset.crop((c*TILE, r*TILE, (c+1)*TILE, (r+1)*TILE))
    g_top, g_dirt = cell(0, 3), cell(1, 2)
    sky_bot = TOD['day']['sky'][1]
    # 원경 타일은 폭 800(=3200/4, 짝수 개) 으로 늘려 정/반전 교대 → 루프 이음새까지 거울 대칭으로 매끈
    x2 = lambda im: im.resize((800, im.height*2), Image.NEAREST)
    mir = lambda im, i: im.transpose(Image.FLIP_LEFT_RIGHT) if i % 2 else im

    def haze(im, col, a, blur):
        rgb = Image.blend(im.convert('RGB'), Image.new('RGB', im.size, col), a)
        r = rgb.convert('RGBA'); r.putalpha(im.getchannel('A'))
        return r.filter(ImageFilter.GaussianBlur(blur)) if blur else r

    # 해/달 위치 (1x 뷰포트 안, 나무와 겹치지 않는 자리 → 나무 배치 전에 예약)
    SUN_X = 1330                                   # 뷰포트 x=530, 참나무(~1088)와 소나무/단풍(~1856) 사이
    SUN_Y = 330                                    # HUD 상단 80px 과 멀리
    sun = load('sun.png'); moon = load('moon.png')
    MOON = (SUN_X + 10, 150)
    boxes = [(SUN_X - 40, SUN_Y - 40, SUN_X + sun.width + 40, SUN_Y + sun.height + 40),
             (MOON[0] - 40, MOON[1] - 40, MOON[0] + moon.width + 40, MOON[1] + moon.height + 40)]

    # clouds (1x) — 해 주변은 비운다
    cl = load('clouds_v3.png'); A = cl.getchannel('A')
    cols = [A.crop((x, 0, x+1, cl.height)).getbbox() is not None for x in range(cl.width)]
    bits, x = [], 0
    while x < cl.width:
        if cols[x]:
            e = x
            while e < cl.width and cols[e]: e += 1
            if e - x > 24: p = cl.crop((x, 0, e, cl.height)); bits.append(p.crop(p.getbbox()))
            x = e
        else: x += 1
    _dummy = Image.new('RGBA', (W, H))  # v3 레이아웃: R 소비만 유지(seed 88 배치 보존), 결과 폐기
    clouds = _dummy; x = R.randint(0, 80)
    while x < W - 60:
        c = R.choice(bits); c = c.transpose(Image.FLIP_LEFT_RIGHT) if R.random() < .5 else c
        y = R.randint(100, 250)
        if not any(x < b[2] and x + c.width > b[0] and y < b[3] and y + c.height > b[1] for b in boxes):
            wpaste(_dummy, c, x, y)
        x += c.width + R.randint(90, 360)
    # v4: 메이플풍 스월 구름 (PixelLab 1x 네이티브, clouds_maple.png) — 반전 없음, 전용 RNG
    cm = load('clouds_maple.png'); big, small = [], []
    for y0, y1, dst, mn in ((0, 128, big, 24), (128, cm.height, small, 6)):
        band = cm.crop((0, y0, cm.width, y1)); A = band.getchannel('A'); x = 0
        while x < band.width:
            if A.crop((x, 0, x+1, band.height)).getbbox():
                e = x
                while e < band.width and A.crop((e, 0, e+1, band.height)).getbbox(): e += 1
                if e - x > mn: p = band.crop((x, 0, e, band.height)); dst.append(p.crop(p.getbbox()))
                x = e
            else: x += 1
    small = [c for i, c in enumerate(small) if i != 1]             # 뾰족한 변형 제외
    R3 = random.Random(SEED + 60); clouds = Image.new('RGBA', (W, H)); placed = []
    def hit(x, y, c, pad):
        return any(x - pad < b[2] and x + c.width + pad > b[0] and y - pad < b[3] and y + c.height + pad > b[1]
                   for b in boxes + placed + [(b[0] - W, b[1], b[2] - W, b[3]) for b in placed] + [(b[0] + W, b[1], b[2] + W, b[3]) for b in placed])
    hi_ = sorted(big, key=lambda c: c.width)[:2]                  # 작은 두 변형은 높은 하늘에도
    for pool, n, ylo, yhi in ((big, 10, 170, 300), (hi_, 9, 88, 165)):
        k = 0
        for _ in range(400):
            if k >= n: break
            c = R3.choice(pool); x = R3.randrange(W); y = R3.randint(ylo, yhi)
            if hit(x, y, c, 40): continue
            wpaste(clouds, c, x, y); placed.append((x, y, x + c.width, y + c.height)); k += 1
    L['clouds'] = clouds

    # far: 산 (헤이즈+블러)
    mtn = haze(x2(Image.open('mountains.png').convert('RGBA')), sky_bot, .38, .6)
    MTN_Y = GRASS - mtn.height - 24
    far = Image.new('RGBA', (W, H))
    for i in range(W // mtn.width + 1): wpaste(far, mir(mtn, i), i*mtn.width, MTN_Y)
    L['far'] = far

    # mid: 언덕 두 줄 + 채움 띠
    gp = [p for p in (g_top.getpixel((x, y)) for x in range(32) for y in range(16, 32)) if p[3] > 200]
    avg = [sum(p[i] for p in gp)//len(gp) for i in range(3)]
    farc = tuple(min(255, int(avg[i]*.75 + (78, 62, 66)[i])) for i in range(3))
    nearc = tuple(min(255, int(avg[i]*.88 + (34, 26, 28)[i])) for i in range(3))
    mid = Image.new('RGBA', (W, H))
    mid.paste(farc + (255,), (0, MTN_Y + mtn.height - 12, W, GRASS + 2))
    hill = haze(x2(Image.open('hills.png').convert('RGBA')), sky_bot, .22, .5)
    for row, (off, dy) in enumerate(((0, -34), (90, 4))):
        for i in range(W // hill.width):
            wpaste(mid, mir(hill, i + row), i*hill.width + off, GRASS - hill.height + dy)
    mid.paste(nearc + (255,), (0, GRASS - 18, W, GRASS + 2))
    L['mid'] = mid

    # near: 중경 숲 띠 (축소 나무 = 원경 취급, 헤이즈로 픽셀 대비를 죽인다)
    def midify(im, s, hz):
        im = im.resize((max(1, int(im.width*s)), max(1, int(im.height*s))), Image.NEAREST)
        return haze(ImageEnhance.Color(im).enhance(.55), nearc, hz, 0)
    mids = [load(n + '.png') for n in ('tree_big_v2c', 'tree_pine2_c', 'tree_maple_c', 'bush')]
    near = Image.new('RGBA', (W, H)); x = 0
    while x < W:
        k = R.randrange(4); s = R.uniform(.22, .32) if k < 3 else R.uniform(.8, 1.0)
        m = midify(mids[k], s, R.uniform(.50, .60))
        if R.random() < .5: m = m.transpose(Image.FLIP_LEFT_RIGHT)
        wpaste(near, m, x, GRASS - m.height + R.randint(4, 10))
        x += R.randint(28, int(m.width*.9) + 30)
    # 덤불 턱: 중경 나무 밑동이 수평선으로 끝나지 않게 울퉁불퉁한 덤불 띠로 덮는다 (주기 W → 루프 OK)
    bl, bd = tuple(max(0, c - 14) for c in nearc), tuple(max(0, c - 30) for c in nearc)
    hi = tuple(min(255, c + 12) for c in nearc); npx = near.load(); x = 0
    R2 = random.Random(SEED + 50)                                  # 신규 요소 전용 RNG → 기존 seed 88 배치 불변
    while x < W:
        bw_ = R2.randint(10, 26); top = R2.randint(8, 16)
        for dx in range(bw_):
            t = (dx - bw_/2) / (bw_/2); hh = int(top * (1 - t*t)**.5) + 4
            for y in range(GRASS + 2 - hh, GRASS + 3):
                c = hi if y == GRASS + 2 - hh else (bd if y > GRASS - 3 and (dx + y) % 2 else bl)
                npx[(x + dx) % W, y] = c + (255,)
        x += bw_ - R2.randint(3, 6)
    L['near'] = near

    # ground: 잔디 윗줄 타일(32 주기) + 흙은 타일이 아니라 주기 노이즈를 원래 흙 팔레트로 양자화
    gnd = Image.new('RGBA', (W, H))
    pal = sorted({p[:3] for p in g_dirt.getdata()}, key=lambda c: sum(c))
    pal = [pal[int(i*(len(pal)-1)/5)] for i in range(6)]          # 어두움→밝음 6단
    nh = H - (GROUND_TOP + TILE)
    n1 = periodic_noise(W, nh, 64, R); n2 = periodic_noise(W, nh, 220, R)
    gp_ = gnd.load(); y0 = GROUND_TOP + TILE
    for y in range(nh):
        for x in range(W):
            v = n1[y][x]*.6 + n2[y][x]*.4 + (R.random() - .5)*.10
            gp_[x, y0 + y] = pal[1 + max(0, min(3, int((v - .25) / .5 * 4)))] + (255,)
    for i in range(W // TILE):
        gnd.alpha_composite(g_top, (i*TILE, GROUND_TOP))
    for _ in range(W // 12):                                       # 돌·뿌리
        sx = R.randrange(W); sy = R.randrange(y0 + 4, H - 4)
        if R.random() < .6:
            c = R.choice([(118,112,104,255), (96,90,86,255), (140,134,124,255)]); w_ = R.randint(2, 5)
            for dx in range(w_):
                for dy in range(2 if w_ < 4 else 3): gp_[(sx+dx) % W, sy+dy] = c
            gp_[(sx) % W, sy-1] = (160,154,142,255) if w_ > 2 else c
        else:
            yy = sy
            for dx in range(R.randint(5, 14)):
                gp_[(sx+dx) % W, yy] = (74,50,34,255); yy = min(max(yy + R.choice((0,0,1,-1)), y0), H-1)
    top_y = next(y for y in range(TILE) if any(g_top.getpixel((x, y))[3] > 200 for x in range(TILE)))
    ref = [g_top.getpixel((x, y)) for x in range(TILE) for y in range(top_y, top_y + 4) if g_top.getpixel((x, y))[3] > 200]
    for x in range(W):
        if R.random() < .35:
            c = R.choice(ref)
            for dy in range(1, R.randint(2, 6)): gp_[x, GROUND_TOP + top_y - dy] = c
    for y in range(y0, H):                                         # 아래로 어둡게
        a = ((y - y0) / (H - y0))**1.3 * .55
        for x in range(W):
            r, g, b, al = gp_[x, y]
            gp_[x, y] = (int(r*(1-a)+20*a), int(g*(1-a)+12*a), int(b*(1-a)+10*a), al)
    L['ground'] = gnd

    # props
    back = Image.new('RGBA', (W, H)); front = Image.new('RGBA', (W, H))
    oak, pine, maple = load('tree_big_v2c.png'), load('tree_pine2_c.png'), load('tree_maple_c.png')
    bush, rock, stump, grass = load('bush.png'), load('rock.png'), load('stump.png'), load('grass.png')
    flowers, mush, fence, log, sign = (load(n + '.png') for n in ('flowers_v3', 'mushrooms_v3', 'fence_v3', 'log_v3', 'signpost'))
    ch = load('char_idle.png')
    char_xs = [int(W*.30), int(W*.72)]
    occ = [(cx - 6, cx + ch.width + 6) for cx in char_xs]
    occ.append((SUN_X - 10, SUN_X + sun.width + 10))               # 해 앞을 가리는 큰 나무 금지
    def free(a, b, pad): return all(b + pad <= p or a - pad >= q for p, q in occ)
    SH = Shadows2(SUN_X); bases = []                               # 접지 그림자 / 앞쪽 데칼용 바닥 폭
    def place(layer, im, x, sink, flip=False, span=None, pad=10, tall=False, wt=.5):
        s = ao(im.transpose(Image.FLIP_LEFT_RIGHT) if flip else im); x = int(x)
        a, b = (x, x + s.width) if span is None else (x + span[0], x + span[1])
        if not free(a, b, pad) or x < 8 or x + s.width > W - 8: return False
        if tall and not free(x, x + s.width, 0): return False
        layer.alpha_composite(s, (x, GRASS - s.height + sink)); occ.append((a, b))
        SH.add(x, GRASS + 1, s, wt); c0, c1 = contact(s); bases.append((x + c0, x + c1, wt)); return True
    def trunk(im):
        b = im.crop((0, int(im.height*.78), im.width, im.height)).getbbox(); return (b[0], b[2])
    last = None
    for anc in (0.07, 0.34, 0.58, 0.86):
        t = R.choice([t for t in (oak, pine, maple) if t is not last]); last = t
        fl = R.random() < .5; sp = trunk(t.transpose(Image.FLIP_LEFT_RIGHT) if fl else t)
        for off in (0, 40, -40, 90, -90, 150, -150, 220, -220):
            if place(back, t, anc*W - t.width/2 + off, 6, fl, sp, 6, tall=anc in (0.34, 0.58), wt=1): last = t; break
    mons = []                                                      # 미리보기 몬스터 슬롯 예약(나무 배치 뒤) → 프롭과 겹치지 않게
    for i, t in enumerate(('t1', 't3', 't5')):
        m = load(f'{MONS}/{t}/walk/frame_00.png'); mx = char_xs[0] + ch.width + 250 + i*140
        mons.append((m, mx)); occ.append((mx - 8, mx + m.width + 8))
    L['_mons'] = mons
    place(back, sign, char_xs[0] - sign.width - 40, 6, True, pad=4)
    x, prev = R.randint(60, 200), None                              # 같은 프롭 연속 금지
    while x < W - 140:
        p = R.choice([q for q in (rock, stump, bush, fence, log) if q is not prev])
        if place(back, p, x, 5 if p in (fence, log) else 4, R.random() < .5, pad=16, wt=.7 if p in (rock, stump, log) else .45): prev = p
        x += R.randint(260, 520)
    x, prev = R.randint(20, 100), None
    while x < W - 70:
        p = R.choice([q for q in (grass, grass, flowers, flowers, mush) if q is not prev])
        if place(front, p, x, 4, R.random() < .5, pad=4, wt=.1): prev = p
        x += R.randint(80, 200)
    L['props_back'], L['props_front'] = back, front
    L['_shadows'] = SH

    R2 = random.Random(SEED + 51)
    # ground_front: 풀잎·조약돌을 오브젝트 바닥 앞에 그려 이음새를 가린다 (프롭·액터 위 레이어)
    gf = Image.new('RGBA', (W, H)); fp = gf.load()
    dark = sorted(ref, key=sum)[:max(1, len(ref)//3)]; light = sorted(ref, key=sum)[-max(1, len(ref)//3):]
    peb = [(118,112,104,255), (96,90,86,255), (150,144,132,255)]
    def blade(x, hmax):
        h = R2.randint(2, hmax); lean = R2.choice((0, 0, 1, -1))
        for i in range(h):
            fp[(x + (lean if i >= h - 2 else 0)) % W, GRASS + 3 - i] = R2.choice(light if i >= h - 2 else dark)
    near_char = lambda x: any(cx - 4 <= x % W <= cx + ch.width + 4 for cx in char_xs)
    for a, b, wt in bases:                                         # 뿌리·프롭 바닥을 살짝 덮는 풀 술
        for x in range(a - 6, b + 7):
            if R2.random() < .55: blade(x, 3 + round(wt * 3))
        for _ in range(max(1, (b - a) // 24)):
            px_ = R2.randint(a - 8, b + 8); c = R2.choice(peb)
            for dx in range(R2.randint(2, 3)): fp[(px_ + dx) % W, GRASS + 3] = c
            fp[px_ % W, GRASS + 2] = peb[2]
    for x in range(W):                                             # 지면 전체 드문 풀잎 (캐릭터 발 앞은 낮게)
        if R2.random() < .10: blade(x, 3 if near_char(x) else 4)
    L['ground_front'] = gf

    # emissive / celestial (별은 밤 전용)
    L['_sun'] = (sun, (SUN_X, SUN_Y)); L['_moon'] = (moon, MOON)
    L['_char'] = (ch, char_xs)
    return L

# ───────────────────────── compose per TOD ─────────────────────────
def compose(L, tod, monsters=None):
    P = TOD[tod]
    cv = gradient(*P['sky'], GRASS + 8).resize((W, GRASS + 8)).convert('RGBA')
    cv = cv.crop((0, 0, W, H)); base = Image.new('RGBA', (W, H), (0, 0, 0, 255)); base.alpha_composite(cv); cv = base
    for k in ('far', 'mid', 'near', 'clouds'):                     # 그레이딩 먼저
        cv.alpha_composite(grade(L[k], P['bg']))
    if tod == 'night':                                             # 발광체는 그레이딩 후
        rnd = random.Random(SEED + 1); px = cv.load()
        for _ in range(W // 7):
            sx, sy = rnd.randrange(W), rnd.randrange(84, GRASS - 170)
            if L['clouds'].getpixel((sx, sy))[3]: continue          # 구름 위엔 안 찍음
            b = rnd.choice([190, 220, 255])
            for dx, dy in ((0, 0), (1, 0), (0, 1)): px[(sx+dx) % W, sy+dy] = (b, b, min(255, b+18), 255)
        m, pos = L['_moon']; cv.alpha_composite(m, pos)
    if tod == 'dawn':
        s, pos = L['_sun']; cv.alpha_composite(s, pos)
    cv.alpha_composite(grade(L['ground'], P['ground']))
    ch, xs = L['_char']; ach = ao(ch, .72, 4)                      # 액터: 미리보기용 AO + 접지 그림자
    SH = Shadows2(L['_shadows'].sun_x); SH.s = list(L['_shadows'].s)
    for cx in xs: SH.add(cx, GRASS + 1, ach, .55)
    for im, x in (monsters or []): SH.add(x, GRASS + 1, im, .7)
    cv.alpha_composite(SH.render(tod))                             # 그림자: 지면 위·프롭 아래
    cv.alpha_composite(grade(L['props_back'], P['obj']))
    gch = grade(ach, P['char'])
    for cx in xs: cv.alpha_composite(gch, (cx, GRASS - ch.height + 2))
    for im, x in (monsters or []):
        cv.alpha_composite(grade(ao(im, .72, 4), P['char']), (x, GRASS - im.height + 2))
    cv.alpha_composite(grade(L['props_front'], P['obj']))
    cv.alpha_composite(grade(L['ground_front'], P['ground']))
    return cv.convert('RGB')

def export_layers(L):
    os.makedirs('layers', exist_ok=True)
    for tod in TOD:                                                # 하늘은 늘릴 수 있는 1px 폭 스트립 (TOD별)
        gradient(*TOD[tod]['sky'], GRASS + 8).resize((4, GRASS + 8)).save(f'layers/sky_{tod}.png')
    meta = [('sky', 'sky_day.png', 0.0, 'sky_{tod}.png 교체(틴트 아님)', False)]
    spec = [('clouds', .05, True), ('far', .15, True), ('mid', .35, True), ('near', .6, True),
            ('ground', 1.0, True), ('props_back', 1.0, True), ('props_front', 1.0, True)]
    for k, f, _ in spec: L[k].save(f'layers/{k}.png')
    L['_shadows'].render('day').save('layers/shadows.png'); L['ground_front'].save('layers/ground_front.png')
    s, pos = L['_sun']; s.save('layers/sun.png'); m, mpos = L['_moon']; m.save('layers/moon.png')
    man = {'zone': 'A_forest', 'map_width': W, 'height': H, 'ground_line_y': GRASS, 'ppu_hint': 40,
           'loop': 'all 3200-wide layers tile seamlessly on x',
           'tint_presets': {t: {k: v for k, v in TOD[t].items() if k != 'sky'} for t in TOD},
           'layers': []}
    order = [('sky', 'sky_day.png', 0.0, False, 'per-TOD file: sky_{day,dawn,night}.png; stretch to view')] + \
            [(k, f'{k}.png', f, t, None) for k, f, t in spec[:4]] + \
            [('sun', 'sun.png', 0.02, False, f'dawn only, pos {pos}, emissive'),
             ('moon', 'moon.png', 0.02, False, f'night only, pos {mpos}, emissive; stars procedural')] + \
            [('ground', 'ground.png', 1.0, True, None),
             ('shadows', 'shadows.png', 1.0, False, 'contact shadows (day). dawn: alpha x.85, width x1.35, shifted away from sun; night: alpha x.5. actors: runtime blob shadow same style'),
             ('props_back', 'props_back.png', 1.0, True, None), ('props_front', 'props_front.png', 1.0, True, None),
             ('ground_front', 'ground_front.png', 1.0, True, 'grass blades/pebbles over object & actor bases')]
    z_after_char = {'props_front', 'ground_front'}
    for i, (k, f, par, tint, note) in enumerate(order):
        so = -100 + i*10 if k not in z_after_char else (50 if k == 'props_front' else 60)         # 캐릭터/몬스터 = 0
        e = {'name': k, 'file': f, 'parallax': par, 'z': i, 'sortingOrder': so,
             'tod_tint': tint, 'tint_group': {'ground': 'ground', 'ground_front': 'ground', 'props_back': 'obj', 'props_front': 'obj'}.get(k, 'bg') if tint else None}
        if note: e['note'] = note
        man['layers'].append(e)
    man['layers'].insert(0, {'name': 'characters', 'sortingOrder': 0, 'tod_tint': True, 'tint_group': 'char', 'note': 'runtime sprites'})
    json.dump(man, open('layers/manifest.json', 'w'), ensure_ascii=False, indent=2)

if __name__ == '__main__':
    L = build_layers()
    export_layers(L)
    ch = L['_char'][0]; cx = L['_char'][1][0]; mons = L['_mons']
    for tod in TOD:
        clean = compose(L, tod); clean.save(f'zoneA_{tod}_FULL.png')
        clean.resize((1920, 432), Image.LANCZOS).save(f'view_{tod}.png')
        clean.crop((VIEW_X, 0, VIEW_X + VIEW_W, H)).save(f'view_{tod}_1x_clean.png')
        compose(L, tod, mons).crop((VIEW_X, 0, VIEW_X + VIEW_W, H)).save(f'view_{tod}_1x.png')
        print('built', tod)
    # 접합 확인: 캐릭터 + 가장 가까운 랜드마크 640x240 → x2 NEAREST
    v = Image.open('view_day_1x.png'); zx = cx - VIEW_X - 60
    v.crop((zx, GRASS - 170, zx + 640, GRASS + 70)).resize((1280, 480), Image.NEAREST).save('view_ground_zoom.png')
    # loop check: FULL 두 장 이어붙이기
    d = Image.open('zoneA_day_FULL.png'); t = Image.new('RGB', (1280, H))
    t.paste(d.crop((W - 640, 0, W, H)), (0, 0)); t.paste(d.crop((0, 0, 640, H)), (640, 0)); t.save('loop_seam_check.png')
