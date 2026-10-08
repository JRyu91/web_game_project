from PIL import Image, ImageEnhance, ImageDraw
import random

PX_PER_UNIT, MAP_UNITS, VIEW_W = 40, 80, 1280
MAP_W = MAP_UNITS * PX_PER_UNIT
TREE_ANCHORS = [0.07, 0.34, 0.58, 0.86]

# ── 시간대 프리셋 ────────────────────────────────────────────────
# bg/obj/char 를 따로 두는 이유: 배경만 물들이면 나무·캐릭터가 붕 뜨고,
# 전부 똑같이 물들이면 주인공이 배경에 묻힌다. 뒤로 갈수록 약하게 건다.
TOD = {
 # 폐허: 낮에도 먼지 낀 탈채도 톤 (sky는 원본 유지)
 'day':   dict(bg =dict(tint=(196,178,150), a=.14, sat=.80, br=1.0),
               obj=dict(tint=(190,170,140), a=.08, sat=.85, br=.98),
               char=None,
               ground=dict(tint=(190,170,140), a=.08, sat=.85, br=.98), sky=None),
 'dawn':  dict(bg =dict(tint=(255,152,92),  a=.34, sat=.92, br=.97),
               obj=dict(tint=(248,152,104), a=.30, sat=.86, br=.78),   # 역광 — 실루엣 쪽으로
               char=dict(tint=(255,178,138),a=.16, sat=.96, br=.90),
               ground=dict(tint=(250,168,120), a=.26, sat=.90, br=.86),
               sky=dict(top=(74,104,178), bottom=(255,180,116))),
 'night': dict(bg =dict(tint=(26,40,100),   a=.50, sat=.48, br=.46),
               obj=dict(tint=(34,54,116),   a=.44, sat=.54, br=.50),
               char=dict(tint=(58,82,148),  a=.20, sat=.84, br=.74),
               ground=dict(tint=(38,58,120), a=.36, sat=.62, br=.66),   # 밤 지면만 살짝 밝게
               sky=dict(top=(7,10,34),      bottom=(30,40,86))),
}

def grade(img, p):
    if p is None: return img
    out = ImageEnhance.Color(img.convert('RGB')).enhance(p['sat'])
    out = ImageEnhance.Brightness(out).enhance(p['br'])
    tint = Image.new('RGB', out.size, p['tint'])
    out = Image.blend(out, tint, p['a'])
    r = out.convert('RGBA'); r.putalpha(img.split()[3])
    return r

def ruins_band(W, rnd):
    """1x로 그리고 2배 확대. 부서진 건물 윤곽 + 뚫린 창 + 철근 + 잔해 더미."""
    w, h = W//2 + 40, 110
    im = Image.new('RGBA', (w, h), (0,0,0,0)); d = ImageDraw.Draw(im)
    body, dark, lite, rust = (70,64,62,255), (48,44,45,255), (96,88,82,255), (130,72,46,255)
    x = -rnd.randint(0, 20)
    while x < w:
        bw, bh = rnd.randint(28, 70), rnd.randint(35, 100)
        top = h - bh
        # 깨진 윗선: 계단식으로 들쭉날쭉
        pts = [(x, h), (x, top + rnd.randint(0, 12))]
        cx = x
        while cx < x + bw:
            cx = min(x + bw, cx + rnd.randint(4, 12))
            pts.append((cx, top + rnd.randint(0, 28)))
        pts.append((x + bw, h))
        d.polygon(pts, fill=body)
        d.line([(x+bw-1, top+10), (x+bw-1, h)], fill=dark)
        # 층 슬래브 + 창 구멍(투명 = 뒤 하늘이 비침)
        for fy in range(top + 14, h - 10, 12):
            d.line([(x+1, fy), (x+bw-2, fy)], fill=lite)
            for wx in range(x + 4, x + bw - 6, 9):
                if rnd.random() < .7:
                    d.rectangle([wx, fy+3, wx+4, fy+8], fill=(0,0,0,0) if rnd.random()<.5 else dark)
        # 철근
        for _ in range(rnd.randint(1, 4)):
            rx = rnd.randint(x+2, x+bw-2)
            ry = top + rnd.randint(0, 20)
            d.line([(rx, ry), (rx + rnd.randint(-3, 3), ry - rnd.randint(4, 10))], fill=rust)
        x += bw + rnd.randint(-6, 18)
    # 잔해 더미
    for _ in range(w // 25):
        rx, rw = rnd.randint(0, w), rnd.randint(14, 40)
        rh = rnd.randint(6, 16)
        d.polygon([(rx, h), (rx + rw//3, h - rh), (rx + rw//2, h - rh + 3), (rx + 2*rw//3, h - rh - 2), (rx + rw, h)], fill=dark)
    im = im.resize((w*2, h*2), Image.NEAREST)
    return im.crop((0, 0, W, h*2))

def build(width, seed, tod, out, scale=1.0):
    random.seed(seed)
    W, H = width, 720
    TILE, GROUND_TILES = 32, 3
    GROUND_TOP = H - GROUND_TILES*TILE - 24
    GRASS = GROUND_TOP + 18
    P = TOD[tod]

    sky  = Image.open('sky.png').convert('RGBA')
    gset = Image.open('ground_set.png').convert('RGBA')
    def cell(i,r,c): return i.crop((c*TILE,r*TILE,(c+1)*TILE,(r+1)*TILE))
    g_top, g_dirt = cell(gset,0,3), cell(gset,1,2)
    def x2(im): return im.resize((im.width*2,im.height*2), Image.NEAREST)

    # ── 배경 레이어 ──
    bg = Image.new('RGBA',(W,H),(0,0,0,255))
    sky2 = x2(sky)
    if P['sky'] is None:
        bg.alpha_composite(sky2.resize((2,sky2.height),Image.BILINEAR).resize((W,GRASS+8),Image.BILINEAR),(0,0))
    else:  # 새벽/밤은 하늘 자체를 다시 칠한다 (파란 낮하늘을 물들이면 탁해진다)
        top,bot = P['sky']['top'], P['sky']['bottom']
        gradimg = Image.new('RGB',(1,GRASS+8))
        for y in range(GRASS+8):
            t=y/(GRASS+7)
            gradimg.putpixel((0,y), tuple(int(top[i]+(bot[i]-top[i])*t) for i in range(3)))
        bg.alpha_composite(gradimg.resize((W,GRASS+8), Image.BILINEAR).convert('RGBA'),(0,0))

    HORIZON = bg.getpixel((W//2, GRASS-40))[:3]   # 대기원근 목표색(지평선 하늘)
    def haze(im, k):  # 멀수록 하늘색에 섞고 대비를 낮춘다
        rgb = Image.blend(im.convert('RGB'), Image.new('RGB', im.size, HORIZON), k)
        r = rgb.convert('RGBA'); r.putalpha(im.split()[3]); return r

    # 구름: 온전한 구름 한 장을 크기·반전·위치를 흩어 배치 (미러 타일링 금지)
    cloud = Image.open('cloud.png').convert('RGBA'); cloud = cloud.crop(cloud.getbbox())
    clouds = Image.new('RGBA',(W,H),(0,0,0,0))
    x = random.randint(-80, 60)
    while x < W:
        sc = random.choice([1.5, 2, 2, 3])
        c = cloud.resize((int(cloud.width*sc), int(cloud.height*sc)), Image.NEAREST)
        if random.random() < .5: c = c.transpose(Image.FLIP_LEFT_RIGHT)
        clouds.alpha_composite(c, (x, random.randint(30, 230)))
        x += c.width + random.randint(120, 420)
    if tod == 'night':  # 밤 구름은 거의 비치게
        clouds.putalpha(clouds.split()[3].point(lambda v: int(v*.22)))

    # 원경 스카이라인: 가장 옅게
    sky_l = x2(Image.open('skyline.png').convert('RGBA'))
    sky_l = haze(sky_l, .68)
    SKY_Y = GRASS - sky_l.height - 60
    i = -1
    while i*sky_l.width < W:
        off = (i*137) % 90
        bg.alpha_composite(sky_l, (i*sky_l.width - off, SKY_Y)); i += 1
    bg.paste(haze(Image.new('RGBA',(1,1),(92,90,88,255)), .55).getpixel((0,0)), (0, SKY_Y+sky_l.height-4, W, GRASS+2))

    # 중경: 무너진 건물 실루엣 (절차 생성, 창문 구멍·철근·잔해 더미)
    mid = ruins_band(W, random.Random(seed+7))
    mid = haze(mid, .10)
    bg.alpha_composite(mid, (0, GRASS - mid.height + 2))

    # ── 오브젝트 레이어 (지면·나무·프롭) ──
    gnd = Image.new('RGBA',(W,H),(0,0,0,0))
    for i in range(-1,(W//TILE)+1):
        gnd.alpha_composite(g_top,(i*TILE,GROUND_TOP))
        for j in range(1,GROUND_TILES+1):
            gnd.alpha_composite(g_dirt,(i*TILE,GROUND_TOP+j*TILE))
    gd = ImageDraw.Draw(gnd)
    gr = random.Random(seed+3)
    for _ in range(W//6):   # 채움: 콘크리트 덩이 + 녹슨 철근
        cx_, cy_ = gr.randrange(W), gr.randrange(GROUND_TOP+30, H)
        w_, h_ = gr.randint(6,18), gr.randint(4,10)
        c = gr.choice([(96,92,88,255),(118,112,104,255),(74,70,68,255),(136,128,118,255)])
        gd.polygon([(cx_,cy_+h_),(cx_+2,cy_),(cx_+w_-3,cy_+1),(cx_+w_,cy_+h_)], fill=c)
    for _ in range(W//40):
        cx_, cy_ = gr.randrange(W), gr.randrange(GROUND_TOP+34, H)
        gd.line([(cx_,cy_),(cx_+gr.randint(-14,14),cy_+gr.randint(-8,8))], fill=(128,70,44,255), width=2)
    gd.rectangle([0,GROUND_TOP+14,W,GROUND_TOP+25], fill=(62,62,66,255))     # 아스팔트 상판
    for _ in range(W//60):  # 균열
        cx_ = gr.randrange(W); pts=[(cx_,GROUND_TOP+15)]
        for k in range(3): pts.append((pts[-1][0]+gr.randint(-5,5), pts[-1][1]+3))
        gd.line(pts, fill=(38,38,42,255))
    gd.rectangle([0,GROUND_TOP+26,W,GROUND_TOP+31], fill=(150,146,138,255))  # 연석 면
    gd.line([(0,GROUND_TOP+31),(W,GROUND_TOP+31)], fill=(90,86,82,255))
    for x_ in range(0,W,48): gd.line([(x_,GROUND_TOP+26),(x_,GROUND_TOP+31)], fill=(110,106,100,255))
    gd.line([(0,GROUND_TOP+16),(W,GROUND_TOP+16)], fill=(120,120,124,255))   # 상판 하이라이트
    for _ in range(W//500):  # 물웅덩이
        px_, pw = random.randint(100, W-200), random.randint(50, 110)
        gd.ellipse([px_, GROUND_TOP+17, px_+pw, GROUND_TOP+23], fill=(72,84,98,255))
        gd.line([(px_+pw//4, GROUND_TOP+19),(px_+pw//2, GROUND_TOP+19)], fill=(140,156,170,255))
    ob = Image.new('RGBA',(W,H),(0,0,0,0))

    def load(n):
        im=Image.open(n+'.png').convert('RGBA'); return im.crop(im.getbbox())
    oak, pine, maple = load('lm_facade'), load('lm_lamp'), load('lm_deadtree')
    bush, rock, grass, stump = load('cone'), load('rubble'), load('weeds'), load('barrel')
    ch = load('char_idle')

    def trunk_span(im):
        b = im.crop((0,int(im.height*.78),im.width,im.height)).getbbox()
        return (b[0],b[2]) if b else (0,im.width)
    occupied=[]
    def free(x0,x1,pad): return all(x1+pad<=a or x0-pad>=b for a,b in occupied)
    def place(im,x,sink=4,flip=False,span=None,pad=10):
        s=im.transpose(Image.FLIP_LEFT_RIGHT) if flip else im
        x=int(x)
        if span is None: x0,x1=x,x+s.width
        else:
            sx0,sx1=span
            if flip: sx0,sx1=s.width-sx1,s.width-sx0
            x0,x1=x+sx0,x+sx1
        if not free(x0,x1,pad): return False
        ob.alpha_composite(s,(x,GRASS-s.height+sink)); occupied.append((x0,x1)); return True

    char_xs=[int(W*0.30)]+([int(W*0.72)] if W>VIEW_W else [])
    for cx in char_xs: occupied.append((cx-6,cx+ch.width+6))

    car, bench, tlight, sand = load('car'), load('bench'), load('trafficlight'), load('sandbags')
    lamps=[]
    trees=[oak,pine,maple]; last=None
    for a in TREE_ANCHORS:
        t=random.choice([t for t in trees if t is not last]); last=t
        x0=max(8,min(a*W-t.width/2,W-t.width-8)); sp=trunk_span(t); fl=random.random()<.5
        if t is oak: fl=False   # 간판 글자 뒤집힘 방지
        for off in (0,40,-40,90,-90,150,-150):
            xx=max(8,min(x0+off,W-t.width-8))
            if place(t,xx,6,fl,span=sp,pad=6):
                if t is pine: lamps.append((xx+(t.width-13 if fl else 12), GRASS-t.height+6+20))
                break
    # 중형 프롭: 종류를 돌려가며 촘촘히
    mids=[car,bench,tlight,sand,rock,stump,bush]; random.shuffle(mids); k=0
    x=random.randint(40,140)
    while x<W-120:
        if place(mids[k%len(mids)],x,4,flip=random.random()<.5,pad=14): k+=1
        x+=random.randint(80,190)
    x=random.randint(20,80)
    while x<W-40:
        if random.random()<.85: place(grass,x,4,flip=random.random()<.5,pad=2)
        x+=random.randint(50,140)

    # ── 그레이딩 후 합성 ──
    cv = grade(bg, P['bg'])
    cv.alpha_composite(grade(clouds, P['bg']))

    # 밤: 별 (그레이딩 뒤에 찍어야 탁해지지 않는다)
    if tod=='night':
        import random as rnd; rnd.seed(seed+1)
        px=cv.load()
        for _ in range(W//7):
            sx,sy=rnd.randrange(W), rnd.randrange(0, SKY_Y)
            b=rnd.choice([190,220,255])
            for dx,dy in ((0,0),(1,0),(0,1)):
                if sx+dx<W and sy+dy<H: px[sx+dx,sy+dy]=(b,b,min(255,b+18),255)

    # 태양/달 — 그레이딩을 적용하지 않는다(자체 발광). 예전 '달이 회색으로 보이던' 원인이 이것.
    if tod=='dawn':
        sun=Image.open('sun.png').convert('RGBA'); sun=sun.crop(sun.getbbox())
        sun=sun.resize((sun.width*2,sun.height*2),Image.NEAREST)
        cv.alpha_composite(sun,(int(W*0.47), GRASS-int(sun.height*2.6)))
    if tod=='night':
        mo=Image.open('moon.png').convert('RGBA'); mo=mo.crop(mo.getbbox())
        mo=mo.resize((int(mo.width*1.8),int(mo.height*1.8)),Image.NEAREST)
        cv.alpha_composite(mo,(int(W*0.30), 78))

    cv.alpha_composite(grade(gnd, P.get('ground') or P['obj']))
    cv.alpha_composite(grade(ob, P['obj']))
    if tod=='night':  # 가로등: 그레이딩 뒤 자체 발광
        glow=Image.new('RGBA',(W,H),(0,0,0,0)); gdr=ImageDraw.Draw(glow)
        for lx,ly in lamps:
            for r,al in ((70,18),(46,30),(26,55),(10,140)):
                gdr.ellipse([lx-r,ly-r,lx+r,ly+r], fill=(255,214,140,al))
            gdr.polygon([(lx-8,ly),(lx+8,ly),(lx+60,GRASS+4),(lx-60,GRASS+4)], fill=(255,210,130,26))
        cv=Image.alpha_composite(cv, glow)
    gch = grade(ch, P['char']) if P['char'] else ch
    for cx in char_xs: cv.alpha_composite(gch,(cx,GRASS-ch.height+2))

    img=cv.convert('RGB')
    if scale!=1.0: img=img.resize((int(W*scale),int(H*scale)),Image.LANCZOS)
    img.save(out)
    return img

for tod in ('day','dawn','night'):
    raw = build(MAP_W, 88, tod, f'zoneB_{tod}_FULL.png')          # 실사용 원본 3200x720
    raw.resize((1920,432), Image.LANCZOS).save(f'view_{tod}.png')  # 보기용 0.6배
    raw.crop((800,0,2080,720)).save(f'view_{tod}_1x.png')          # 실제 게임 화면 1:1
    print('built', tod, raw.size)
