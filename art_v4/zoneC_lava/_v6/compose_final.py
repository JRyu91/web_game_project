"""Zone C 용암굴 합성 (v3).

픽셀 스케일 규칙 (mixel 금지):
  - 모든 레이어는 1x 네이티브 픽셀로 그린다. x2 NEAREST 업스케일 레이어 없음.
  - 배경(back_wall/ceiling/far/mid)은 절차적으로 1x 생성 → 스프라이트 업스케일이 필요 없다.
  - 예외를 둔다면 far 이하, 충분히 헤이즈 처리된 레이어만 x2 허용.
루프 규칙: 배경 레이어는 주기 W 노이즈 + 랩 배치로 좌우 이음매 없이 반복된다.
          지면/소품 레이어는 가장자리 8px 안쪽에만 배치해 경계를 넘지 않는다.
HUD 안전영역: 상단 80px / 하단 110px. 발 라인(GRASS)은 y<=610.
출력: layers/*.png (그레이딩 전 원본) + manifest.json (cave 그레이딩 값 포함),
      FULL/view 프리뷰는 그레이딩 후 발광부를 원본으로 다시 찍은 결과.
"""
from PIL import Image, ImageEnhance
from cave_bg import cave_bg, FALLS, fall_sprite
import random, math, json, os, glob

PX_PER_UNIT, MAP_UNITS, VIEW_W = 40, 80, 1280
MAP_W, H = MAP_UNITS * PX_PER_UNIT, 720
HUD_TOP, HUD_BOT = 80, 110
GROUND_TOP = 588                  # 지면 윗단 평균
GRASS = GROUND_TOP + 6            # 소품/캐릭터 발 라인 (<= 720-110)
TREE_ANCHORS = [0.07, 0.34, 0.58, 0.86]
MON_DIR = '../../unity_client/Assets/Resources/Sprites/Monsters'
MONSTERS = [('t15', 1480), ('t19', 1700), ('t21', 1930)]   # 맵 x (1x 뷰 = x-800)

# ── 조명 프리셋: 지하라 cave 하나. Unity에서 레이어별로 재현하도록 manifest에도 기록 ──
CAVE = dict(back=dict(tint=(30,10,12), a=.45, sat=.45, br=.55),
            far =dict(tint=(34,12,12), a=.40, sat=.55, br=.65),
            mid =dict(tint=(40,12,12), a=.30, sat=.65, br=.75),
            obj =dict(tint=(90,24,12), a=.10, sat=1.0, br=1.0),
            char=dict(tint=(150,60,30),a=.14, sat=.96, br=.92),
            ground=dict(tint=(80,20,10), a=.05, sat=1.0, br=1.0))

def is_lava(r,g,b,a): return a and r>200 and g>70 and b<140 and r-b>110

def emissive(img):
    out=img.copy(); px=out.load()
    for y in range(out.height):
        for x in range(out.width):
            if not is_lava(*px[x,y]): px[x,y]=(0,0,0,0)
    return out

def dim(img, k):
    img=img.copy(); img.putalpha(img.split()[3].point(lambda v:int(v*k))); return img

def grade(img, p):
    out = ImageEnhance.Color(img.convert('RGB')).enhance(p['sat'])
    out = ImageEnhance.Brightness(out).enhance(p['br'])
    out = Image.blend(out, Image.new('RGB', out.size, p['tint']), p['a']).convert('RGBA')
    out.putalpha(img.split()[3]); return out

def pnoise(W, period, amp, rng):
    """주기 W로 닫힌 1D 값 노이즈 → 좌우 루프 이음매 없음."""
    n=max(1,W//period); pts=[rng.uniform(-1,1) for _ in range(n)]
    out=[]
    for x in range(W):
        f=x*n/W; i=int(f); t=f-i; t=(1-math.cos(t*math.pi))/2
        out.append(amp*(pts[i%n]*(1-t)+pts[(i+1)%n]*t))
    return out

def wrap_paste(layer, im, x, y):
    W=layer.width; x%=W
    layer.alpha_composite(im.crop((0,0,min(im.width,W-x),im.height)),(x,y))
    if x+im.width>W: layer.alpha_composite(im.crop((W-x,0,im.width,im.height)),(0,y))

def lavafall_of(lf, h, off=0):
    # 늘리지 않고(NEAREST 스트레치=픽셀 크기 변형) 줄기 구간(40..200)을 세로 타일링; off로 흐름 애니메이션
    head,body,foot=lf.crop((0,0,lf.width,40)),lf.crop((0,40,lf.width,200)),lf.crop((0,200,lf.width,lf.height))
    out=Image.new('RGBA',(lf.width,h),(0,0,0,0)); bh=h-head.height-foot.height
    y=-(off%body.height)
    while y<bh: out.alpha_composite(body.crop((0,max(0,-y),body.width,min(body.height,bh-y))),(0,head.height+max(0,y))); y+=body.height
    out.alpha_composite(head,(0,0)); out.alpha_composite(foot,(0,h-foot.height)); return out


BAYER=[[0,8,2,10],[12,4,14,6],[3,11,1,9],[15,7,13,5]]
def contact_shadow(layer, cx, y, w, h=6, a=150):
    """픽셀아트 접지 그림자: 계단 3단 + 가장자리 베이어 디더."""
    px=layer.load(); W=layer.width
    for dy in range(-h//2, h-h//2):
        for dx in range(-w//2-3, w//2+4):
            r=(dx/(w/2+2))**2+(dy/(h/2))**2
            if r>1.25: continue
            k=a if r<.45 else a*2//3 if r<.8 else a//3
            if r>=1 and BAYER[(y+dy)%4][(cx+dx)%4]>=8: continue
            x=(cx+dx)%W; yy=y+dy
            if 0<=yy<layer.height:
                o=px[x,yy]; px[x,yy]=(8,4,6,max(o[3],k))

def light_match(im, warm):
    """아래쪽 AO(어둡게) + 용암원 근처면 아래 1/3에 따뜻한 반사광."""
    im=im.copy(); p=im.load(); w,h=im.size
    for y in range(h):
        t=(y-h*.62)/(h*.38)
        for x in range(w):
            r,g,b,a=p[x,y]
            if not a or is_lava(r,g,b,a): continue
            if warm>0 and t>0:
                k=warm*t*.55; r=int(r+(230-r)*k*.5); g=int(g+(90-g)*k*.3); b=int(b*(1-k*.4))
            if y>=h-6:
                k=.2+.1*(y-(h-6)); r,g,b=int(r*(1-k)),int(g*(1-k)),int(b*(1-k))
            p[x,y]=(r,g,b,a)
    return im

def rubble(layer, rng, x0, x1, y, n=None):
    """밑단 앞에 자갈/잔해 → 수평 절단면과 이음매를 가린다 (ground_front 레이어)."""
    px=layer.load(); W=layer.width
    n=n or max(3,(x1-x0)//6)
    for _ in range(n):
        cx=rng.randint(x0-6,x1-2); w=rng.randint(5,12); hh=rng.randint(3,7)
        base=y+rng.randint(0,3)
        for i in range(w):
            top=base-hh+(1 if i in (0,w-1) else 0)
            for j in range(top,base+1):
                c=(26,18,22) if j==base else (120,96,100) if j==top else (78,60,66) if i<w//2 else (52,38,46)
                if 0<=j<layer.height: px[(cx+i)%W,j]=c+(255,)

def build(W, seed):
    rng=random.Random(seed)
    L={k:Image.new('RGBA',(W,H),(0,0,0,0)) for k in
       ('back_wall','ceiling','far','mid','lavafalls','ground','shadows','props_back','props_front','ground_front','embers')}
    def load(n):
        im=Image.open(n+'.png').convert('RGBA'); return im.crop(im.getbbox())

    cave_bg(L,W,rng)

    # ground: 깨진 윗단·높이 변화·현무암 셀 텍스처·드문 발광 균열
    gp=L['ground'].load()
    gn=pnoise(W,90,3,rng)
    top=[GROUND_TOP+int(gn[x]) for x in range(W)]
    for _ in range(W//60):          # 깨진 홈
        cx,wd=rng.randrange(W),rng.randint(3,9)
        for d in range(wd): top[(cx+d)%W]+=rng.choice((1,2,2,3))
    cells={}
    def cell(cx,cy): return cells.setdefault((cx,cy),(rng.randint(22,42),rng.randint(-3,3)))
    for x in range(W):
        for y in range(top[x],H):
            d=y-top[x]
            if d==0: c=(14,10,14)
            elif d<3: c=(96,80,86)
            elif d<5: c=(62,50,58)
            else:
                row=(y-GROUND_TOP)//9+(x//23)%2   # 엇갈린 행
                cw=9+(row%4)*5; off=row*7
                v,h_=cell((x+off)//cw,row)
                v+=((x*13+y*7)%5==0)*4-((x*3+y*11)%7==0)*4-min(10,(d-5)//12)
                if (x+off)%cw==0 and (y+x//cw)%3: v-=7
                c=(v+2+h_,v-2,v+4)
            gp[x,y]=c+(255,)
    for _ in range(W//30):          # 윗단 자갈
        x=rng.randrange(8,W-12); w=rng.randint(2,5); t=top[x]
        for i in range(w):
            for j in range(1,rng.randint(2,3)+1): gp[x+i,t-j]=(78,66,74,255) if j==1 else (50,40,48,255)
    for _ in range(W//260):         # 윗단 발광 균열
        x=rng.randrange(20,W-60); y=top[x]+2
        for k in range(rng.randint(10,24)):
            if 0<=x<W and y<H:
                gp[x,y]=(255,200,90,255) if k%3 else (255,110,30,255)
                if y+1<H: gp[x,y+1]=(150,40,20,255)
            x+=1; y+=rng.choice((0,0,1,1,-1)) if y>top[min(x,W-1)]+2 else 1
    for _ in range(W//220):         # 깊은 균열
        x,y=rng.randrange(W),rng.randint(GROUND_TOP+14,H-20)
        for k in range(rng.randint(14,40)):
            if 0<=x<W and y<H:
                gp[x,y]=(255,200,90,255) if k%3 else (255,110,30,255)
                if y+1<H: gp[x,y+1]=(150,40,20,255)
            x+=rng.choice((-1,1,1)); y+=rng.choice((0,0,1,-1))

    # props
    big=[load('stalagmite'),load('obsidian'),load('lavavent')]
    smalls=[load(n) for n in ('rock','lavapool','bones','magmarocks','charred','geyser')]
    ember=load('ember'); ch=load('char_idle')
    occupied=[]
    def free(x0,x1,pad): return all(x1+pad<=a or x0-pad>=b for a,b in occupied)
    def span(im):
        b=im.crop((0,int(im.height*.78),im.width,im.height)).getbbox()
        return (b[0],b[2]) if b else (0,im.width)
    def place(layer,im,x,sink=4,flip=False,sp=None,pad=10):
        s=im.transpose(Image.FLIP_LEFT_RIGHT) if flip else im; x=int(x)
        sx0,sx1=sp or (0,s.width)
        if flip: sx0,sx1=s.width-sx1,s.width-sx0
        if not free(x+sx0,x+sx1,pad): return False
        # 발 밑 최저 지면에 맞춰 떠 보이지 않게
        y=max(top[min(W-1,max(0,xx))] for xx in range(x+sx0,x+sx1))
        c=x+(sx0+sx1)//2; d=min(abs((f-c+W/2)%W-W/2) for f in FALLS)
        warm=max(0,1-d/360)+.25                          # 용암강 기본 반사 + 폭포 근처 강하게
        contact_shadow(L['shadows'],c,y+2,sx1-sx0+26,8 if sx1-sx0>40 else 5,210)
        L[layer].alpha_composite(light_match(s,min(1,warm)),(x,y+sink-s.height))
        rubble(L['ground_front'],rng,x+sx0,x+sx1,y+2,max(8,(sx1-sx0)//3))
        occupied.append((x+sx0,x+sx1)); return True

    char_xs=[int(W*0.30)]+([int(W*0.72)] if W>VIEW_W else [])
    for cx in char_xs: occupied.append((cx-6,cx+ch.width+6))
    for _,mx in MONSTERS: occupied.append((mx-20,mx+140))      # 몬스터 자리 비움
    last=None
    for a in TREE_ANCHORS:
        t=rng.choice([t for t in big if t is not last]); last=t
        x0=max(8,min(a*W-t.width/2,W-t.width-8)); fl=rng.random()<.5
        for off in (0,40,-40,90,-90,150,-150,220,-220):
            if place('props_back',t,max(8,min(x0+off,W-t.width-8)),6,fl,span(t),6): break
    x=rng.randint(60,200); prev=None
    while x<W-120:
        s=rng.choice([s for s in smalls if s is not prev])
        if place('props_front',s,x,5,rng.random()<.5,pad=16): prev=s
        x+=rng.randint(180,420)
    x=rng.randint(40,160)
    while x<W-60:
        if rng.random()<.35: place('props_front',ember,x,4,rng.random()<.5,pad=8)
        x+=rng.randint(150,380)

    return L, ch, char_xs, top

ORDER=[('back_wall',None,.05,False),('far',None,.15,True),('mid',None,.35,False),('ceiling',None,.35,False),
       ('lavafalls',None,.35,True),('ground','ground',1.0,False),('shadows',None,1.0,False),
       ('props_back','obj',1.0,False),('props_front','obj',1.0,False),('ground_front','ground',1.0,False),
       ('embers',None,.35,True)]

def composite(L):
    cv=Image.new('RGBA',L['back_wall'].size,(0,0,0,255))
    for name,g,_,em in ORDER:
        im=L[name]
        if g: cv.alpha_composite(grade(im,CAVE[g])); cv.alpha_composite(emissive(im))  # 발광부 원본 재적용
        else: cv.alpha_composite(im)
    return cv

def sprite(n):
    im=Image.open(f'{MON_DIR}/{n}/walk/frame_00.png').convert('RGBA'); return im.crop(im.getbbox())

if __name__=='__main__':
    L,ch,char_xs,top=build(MAP_W,88)
    os.makedirs('layers',exist_ok=True)
    man=[]
    for z,(name,g,par,em) in enumerate(ORDER):
        L[name].save(f'layers/{name}.png')
        man.append(dict(name=name,file=f'{name}.png',parallax=par,z=z,sortingOrder=(z-5)*10,
                        emissive=em or name in('far','ground','props_back','props_front','ground_front'),
                        emissive_rule='pixels r>200,g>70,b<140,r-b>110 keep original color' if not em else None,
                        grading=CAVE[g] if g else None))
    json.dump(dict(zone='C_lava',width=MAP_W,height=H,px_per_unit=PX_PER_UNIT,lighting='cave',
                   ground_line_y=GRASS,hud_safe=dict(top=HUD_TOP,bottom=HUD_BOT),
                   grading_note='out=blend(brightness(saturation(rgb,sat),br),tint,a); emissive pixels re-drawn ungraded on top',
                   character_grading=CAVE['char'],layers=man),open('layers/manifest.json','w'),indent=1)

    # 캐릭터/몬스터 프리뷰: 접지 그림자 + 3px 묻기 + 하단 반사광 (런타임 재현용, 레이어엔 미포함)
    def actor(cv,im,x,foot,rng_):
        sh=Image.new('RGBA',cv.size,(0,0,0,0)); b=im.crop((0,im.height-6,im.width,im.height)).getbbox() or (0,0,im.width,6)
        contact_shadow(sh,x+(b[0]+b[2])//2,foot+2,max(16,b[2]-b[0]+10),6,200); cv.alpha_composite(sh)
        cv.alpha_composite(light_match(grade(im,CAVE['char']),.5),(x,foot+3-im.height))
    cv=composite(L)
    for cx in char_xs: actor(cv,ch,cx,max(top[cx:cx+ch.width]),None)
    clean=cv.copy()
    for n,mx in MONSTERS:
        m=sprite(n); actor(cv,m,mx,max(top[mx:mx+m.width]),None)
    clean.convert('RGB').save('zoneC_cave_FULL.png')
    clean.convert('RGB').resize((1920,432),Image.LANCZOS).save('view_cave.png')
    clean.crop((800,0,2080,720)).convert('RGB').save('view_cave_1x_clean.png')
    cv.crop((800,0,2080,720)).convert('RGB').save('view_cave_1x.png')
    cv.crop((800,380,1440,620)).convert('RGB').resize((1280,480),Image.NEAREST).save('view_cave_ground_zoom.png')
    # 용암폭포 8프레임 루프 (흐름 텍스처 주기 64px / 8px, 튀김도 같은 주기로 순환)
    import shutil; shutil.rmtree('layers/lavafall_frames',ignore_errors=True); os.makedirs('layers/lavafall_frames')
    for i in range(8): fall_sprite(420,i*8).save(f'layers/lavafall_frames/frame_{i:02d}.png')
    # 루프 검사: 배경 레이어만 2장 이어붙인 이미지
    bgc=Image.new('RGBA',(MAP_W,H))
    for name,g,_,_ in ORDER[:5]: bgc.alpha_composite(grade(L[name],CAVE[g]) if g else L[name])
    seam=Image.new('RGB',(1280,H)); seam.paste(bgc.crop((MAP_W-640,0,MAP_W,H)),(0,0)); seam.paste(bgc.crop((0,0,640,H)),(640,0))
    seam.save('_seam_check.png')
    print('built cave', MAP_W, 'ground', GRASS)
