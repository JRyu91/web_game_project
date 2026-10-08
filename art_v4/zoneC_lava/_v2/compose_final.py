from PIL import Image, ImageEnhance
import random

PX_PER_UNIT, MAP_UNITS, VIEW_W = 40, 80, 1280
MAP_W = MAP_UNITS * PX_PER_UNIT
TREE_ANCHORS = [0.07, 0.34, 0.58, 0.86]

# ── 조명 프리셋: 지하라 하나뿐. 용암(발광부)은 그레이딩 뒤에 원본으로 다시 찍는다 ──
TOD = {
 'cave': dict(bg =dict(tint=(30,10,12), a=.55, sat=.35, br=.40),
              mid=dict(tint=(40,12,12), a=.45, sat=.50, br=.50),
              obj=dict(tint=(90,24,12), a=.10, sat=1.0, br=1.0),
              char=dict(tint=(150,60,30),a=.14, sat=.96, br=.92),
              ground=dict(tint=(80,20,10), a=.05, sat=1.0, br=1.0)),
}

def emissive(img):
    # 용암색(밝은 주황/노랑)만 남긴 레이어 — 그레이딩 후 덮어서 발광 유지
    out=img.copy(); px=out.load()
    for y in range(out.height):
        for x in range(out.width):
            r,g,b,a=px[x,y]
            if not (a and r>200 and g>70 and b<140 and r-b>110): px[x,y]=(0,0,0,0)
    return out

def dim(img, k):
    a=img.split()[3].point(lambda v:int(v*k)); img.putalpha(a); return img

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

    mtn  = Image.open('mountains.png').convert('RGBA')
    hill = Image.open('hills.png').convert('RGBA')
    def cell(i,r,c): return i.crop((c*TILE,r*TILE,(c+1)*TILE,(r+1)*TILE))
    def load(n):
        im=Image.open(n+'.png').convert('RGBA'); return im.crop(im.getbbox())
    def x2(im): return im.resize((im.width*2,im.height*2), Image.NEAREST)

    # ── 배경: 절차적 동굴 벽 (타일/미러 없음, 어둡고 저대비) ──
    bw, bh = W//8+2, H//8+2
    wall = Image.new('RGB',(bw,bh)); wp=wall.load()
    for y in range(bh):
        for x in range(bw):
            v = 14 + random.randint(0,8) + int(10*(1-y/bh))
            wp[x,y]=(v+6, v-2, v)
    wall = wall.resize((bw*8,bh*8), Image.BILINEAR).crop((0,0,W,H))
    bg = wall.convert('RGBA')
    # 멀리 있는 균열 몇 개 (희미한 적색)
    bpx=bg.load()
    for _ in range(W//260):
        x,y=random.randrange(W),random.randrange(30,300)
        for _ in range(random.randint(20,60)):
            bpx[x%W,y]=(70,22,14,255); x+=random.choice((-1,0,1)); y+=1
            if y>=H: break

    # 중경: 산 실루엣을 랜덤 간격·랜덤 플립으로 (i=-1부터 덮음)
    # mountains.png에 박힌 옛 용암폭포(갈색 기둥) 제거 → 어두운 암석색
    mp=mtn.load()
    for yy in range(mtn.height):
        for xx in range(mtn.width):
            r,g,b,a=mp[xx,yy]
            if a and r>90 and g>60 and r-b>30: mp[xx,yy]=(46,32,34,a)
    # 천장: 산 실루엣을 뒤집어 랜덤 배치 (규칙 반복 방지, 아주 어둡게)
    ceil=x2(mtn).transpose(Image.FLIP_TOP_BOTTOM)
    x=-random.randint(0,ceil.width//2)
    while x<W:
        c=ceil if random.random()<.5 else ceil.transpose(Image.FLIP_LEFT_RIGHT)
        c=ImageEnhance.Brightness(c).enhance(.45)
        bg.alpha_composite(c,(x,-random.randint(60,130)))
        x+=int(ceil.width*random.uniform(.5,.95))
    mtn2 = x2(mtn); MTN_Y = GRASS-mtn2.height-40
    x=-random.randint(0,mtn2.width//2)
    while x<W:
        m=mtn2 if random.random()<.5 else mtn2.transpose(Image.FLIP_LEFT_RIGHT)
        bg.alpha_composite(m,(x,MTN_Y+random.randint(-20,20)))
        x+=int(mtn2.width*random.uniform(.55,.9))
    mid = Image.new('RGBA',(W,H),(0,0,0,0))
    lf = load('lavafall'); n_lf=0; x=random.randint(150,500)
    while x<W-60:
        s_=lf.resize((lf.width, int(lf.height*random.uniform(.9,1.4))), Image.NEAREST)
        mid.alpha_composite(dim(s_,.7),(x, GRASS-s_.height-60))
        x+=random.randint(500,1100)
    h2=x2(hill)
    x=-random.randint(0,h2.width//2)
    while x<W:
        mid.alpha_composite(h2 if random.random()<.5 else h2.transpose(Image.FLIP_LEFT_RIGHT),(x, GRASS-h2.height+random.randint(-14,6)))
        x+=int(h2.width*random.uniform(.5,.85))

    # ── 지면: 절차적 현무암, 뚜렷한 윗단 + 드문 불규칙 균열 ──
    gnd = Image.new('RGBA',(W,H),(0,0,0,0)); gp_=gnd.load()
    gblk={}
    top=[GROUND_TOP]*W; t=GROUND_TOP
    for x in range(W):
        if random.random()<.08: t=max(GROUND_TOP-3,min(GROUND_TOP+3,t+random.choice((-1,1))))
        top[x]=t
    for x in range(W):
        for y in range(top[x],H):
            d=y-top[x]
            if d==0: c=(12,8,12)
            elif d<3: c=(92,78,86)
            elif d<6: c=(58,48,56)
            else:
                v=gblk.get((x//14+(y//9)%2*7, y//9)) or gblk.setdefault((x//14+(y//9)%2*7, y//9), random.randint(24,40))
                v-=min(10,(d-6)//10)
                if (x+(y//9)%2*7)%14==0 or y%9==0: v-=8
                c=(v+2,v-2,v+4)
            gp_[x,y]=c+(255,)
    for _ in range(W//200):
        x,y=random.randrange(W),random.randint(GROUND_TOP+6,H-20)
        for k in range(random.randint(14,40)):
            if 0<=x<W and y<H:
                gp_[x,y]=(255,200,90,255) if k%3 else (255,110,30,255)
                if y+1<H: gp_[x,y+1]=(150,40,20,255)
            x+=random.choice((-1,1,1)); y+=random.choice((0,0,1,-1))
    ob = Image.new('RGBA',(W,H),(0,0,0,0))

    oak, pine, maple = load('stalagmite'), load('obsidian'), load('lavavent')
    bush, rock, grass, stump = load('bones'), load('rock'), load('ember'), load('lavapool')
    smalls = [rock, stump, bush, load('magmarocks'), load('charred'), load('geyser')]
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
        place(random.choice(smalls),x,4,flip=random.random()<.5,pad=16)
        x+=random.randint(180,420)
    x=random.randint(40,160)
    while x<W-60:
        if random.random()<.35: place(grass,x,4,flip=random.random()<.5,pad=8)
        x+=random.randint(150,380)

    # ── 그레이딩 후 합성 (용암은 그레이딩 뒤에 원본 색으로) ──
    cv = grade(bg, P['bg'])
    cv.alpha_composite(dim(emissive(bg),.25))
    cv.alpha_composite(grade(mid, P['mid']))
    cv.alpha_composite(dim(emissive(mid),.55))
    cv.alpha_composite(grade(gnd, P['ground']))
    cv.alpha_composite(emissive(gnd))
    cv.alpha_composite(grade(ob, P['obj']))
    cv.alpha_composite(emissive(ob))
    gch = grade(ch, P['char'])
    for cx in char_xs: cv.alpha_composite(gch,(cx,GRASS-ch.height+2))

    # 떠다니는 불티 (별 대신)
    import random as rnd; rnd.seed(seed+1)
    px=cv.load()
    for _ in range(W//10):
        sx,sy=rnd.randrange(W-1), rnd.randrange(40, GRASS-10)
        c=rnd.choice([(255,190,80),(255,140,40),(255,225,140)])
        for dx,dy in ((0,0),(1,0),(0,1))[:rnd.choice([1,3])]:
            px[sx+dx,sy+dy]=c+(255,)

    img=cv.convert('RGB')
    if scale!=1.0: img=img.resize((int(W*scale),int(H*scale)),Image.LANCZOS)
    img.save(out)
    return img

raw = build(MAP_W, 88, 'cave', 'zoneC_cave_FULL.png')          # 실사용 원본 3200x720
raw.resize((1920,432), Image.LANCZOS).save('view_cave.png')     # 보기용 0.6배
raw.crop((800,0,2080,720)).save('view_cave_1x.png')              # 인게임 1:1 뷰포트
print('built cave', raw.size)
