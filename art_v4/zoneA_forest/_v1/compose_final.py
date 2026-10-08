from PIL import Image, ImageEnhance
import random

PX_PER_UNIT, MAP_UNITS, VIEW_W = 40, 80, 1280
MAP_W = MAP_UNITS * PX_PER_UNIT
TREE_ANCHORS = [0.07, 0.34, 0.58, 0.86]

# ── 시간대 프리셋 ────────────────────────────────────────────────
# bg/obj/char 를 따로 두는 이유: 배경만 물들이면 나무·캐릭터가 붕 뜨고,
# 전부 똑같이 물들이면 주인공이 배경에 묻힌다. 뒤로 갈수록 약하게 건다.
TOD = {
 'day':   dict(bg=None, obj=None, char=None, ground=None, sky=None),
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

def build(width, seed, tod, out, scale=1.0):
    random.seed(seed)
    W, H = width, 720
    TILE, GROUND_TILES = 32, 3
    GROUND_TOP = H - GROUND_TILES*TILE - 24
    GRASS = GROUND_TOP + 18
    P = TOD[tod]

    sky  = Image.open('sky.png').convert('RGBA')
    mtn  = Image.open('mountains.png').convert('RGBA')
    hill = Image.open('hills.png').convert('RGBA')
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

    sflip = sky2.transpose(Image.FLIP_LEFT_RIGHT)
    clouds = Image.new('RGBA',(W,H),(0,0,0,0))
    for i in range((W//sky2.width)+1):
        band=(sky2 if i%2==0 else sflip).crop((0,0,sky2.width,300))
        # 구름만 뽑아내기: 밝은 픽셀만 남긴다
        cm = band.copy(); px=cm.load()
        for y in range(cm.height):
            for x in range(cm.width):
                r,g,b,a=px[x,y]
                px[x,y]=(r,g,b,255 if (r>228 and g>238 and b>238) else 0)
        clouds.alpha_composite(cm,(i*sky2.width,120))

    mtn2 = x2(mtn); MTN_Y = GRASS-mtn2.height-24
    mflip = mtn2.transpose(Image.FLIP_LEFT_RIGHT)
    for i in range((W//mtn2.width)+1):
        bg.alpha_composite(mtn2 if i%2==0 else mflip,(i*mtn2.width,MTN_Y))

    gp=[g_top.getpixel((x,y)) for x in range(32) for y in range(16,32)]
    gp=[p for p in gp if p[3]>200]
    gr=sum(p[0] for p in gp)//len(gp); gg=sum(p[1] for p in gp)//len(gp); gb=sum(p[2] for p in gp)//len(gp)
    far =(min(255,int(gr*.75+78)),min(255,int(gg*.75+62)),min(255,int(gb*.75+66)))
    near=(min(255,int(gr*.88+34)),min(255,int(gg*.88+26)),min(255,int(gb*.88+28)))
    bg.paste(far,(0,MTN_Y+mtn2.height-12,W,GRASS+2))
    h2=x2(hill); hflip=h2.transpose(Image.FLIP_LEFT_RIGHT)
    for i in range((W//h2.width)+1):
        bg.alpha_composite((h2 if i%2 else hflip),(i*h2.width, GRASS-h2.height-34))
    for i in range((W//h2.width)+1):
        bg.alpha_composite((hflip if i%2 else h2),(i*h2.width+90, GRASS-h2.height+4))
    bg.paste(near,(0,GRASS-18,W,GRASS+2))

    # ── 오브젝트 레이어 (지면·나무·프롭) ──
    gnd = Image.new('RGBA',(W,H),(0,0,0,0))
    for i in range((W//TILE)+1):
        gnd.alpha_composite(g_top,(i*TILE,GROUND_TOP))
        for j in range(1,GROUND_TILES+1):
            gnd.alpha_composite(g_dirt,(i*TILE,GROUND_TOP+j*TILE))
    ob = Image.new('RGBA',(W,H),(0,0,0,0))

    def load(n):
        im=Image.open(n+'.png').convert('RGBA'); return im.crop(im.getbbox())
    oak, pine, maple = load('tree_big_v2c'), load('tree_pine2_c'), load('tree_maple_c')
    bush, rock, grass, stump = load('bush'), load('rock'), load('grass'), load('stump')
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

    trees=[oak,pine,maple]; last=None
    for a in TREE_ANCHORS:
        t=random.choice([t for t in trees if t is not last]); last=t
        x0=max(8,min(a*W-t.width/2,W-t.width-8)); sp=trunk_span(t); fl=random.random()<.5
        for off in (0,40,-40,90,-90,150,-150):
            if place(t,max(8,min(x0+off,W-t.width-8)),6,fl,span=sp,pad=6): break
    x=random.randint(60,200)
    while x<W-120:
        place(random.choice([rock,stump,bush]),x,4,flip=random.random()<.5,pad=16)
        x+=random.randint(300,620)
    x=random.randint(40,160)
    while x<W-60:
        if random.random()<.7: place(grass,x,4,flip=random.random()<.5,pad=8)
        x+=random.randint(150,380)

    # ── 그레이딩 후 합성 ──
    cv = grade(bg, P['bg'])
    cv.alpha_composite(grade(clouds, P['bg']))

    # 밤: 별 (그레이딩 뒤에 찍어야 탁해지지 않는다)
    if tod=='night':
        import random as rnd; rnd.seed(seed+1)
        px=cv.load()
        for _ in range(W//7):
            sx,sy=rnd.randrange(W), rnd.randrange(0, GRASS-160)
            b=rnd.choice([190,220,255])
            for dx,dy in ((0,0),(1,0),(0,1)):
                if sx+dx<W and sy+dy<H: px[sx+dx,sy+dy]=(b,b,min(255,b+18),255)

    # 태양/달 — 그레이딩을 적용하지 않는다(자체 발광). 예전 '달이 회색으로 보이던' 원인이 이것.
    if tod=='dawn':
        sun=Image.open('sun.png').convert('RGBA'); sun=sun.crop(sun.getbbox())
        sun=sun.resize((sun.width*2,sun.height*2),Image.NEAREST)
        cv.alpha_composite(sun,(int(W*0.285), GRASS-int(sun.height*1.9)))
    if tod=='night':
        mo=Image.open('moon.png').convert('RGBA'); mo=mo.crop(mo.getbbox())
        mo=mo.resize((int(mo.width*1.8),int(mo.height*1.8)),Image.NEAREST)
        cv.alpha_composite(mo,(int(W*0.30), 78))

    cv.alpha_composite(grade(gnd, P.get('ground') or P['obj']))
    cv.alpha_composite(grade(ob, P['obj']))
    gch = grade(ch, P['char'])
    for cx in char_xs: cv.alpha_composite(gch,(cx,GRASS-ch.height+2))

    img=cv.convert('RGB')
    if scale!=1.0: img=img.resize((int(W*scale),int(H*scale)),Image.LANCZOS)
    img.save(out)
    return img

for tod in ('day','dawn','night'):
    raw = build(MAP_W, 88, tod, f'zoneA_{tod}_FULL.png')          # 실사용 원본 3200x720
    raw.resize((1920,432), Image.LANCZOS).save(f'view_{tod}.png')  # 보기용 0.6배
    print('built', tod, raw.size)
