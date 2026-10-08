"""obsidian.png 밑단 수평 절단 → 불규칙 암반 + 작은 결정 무리 (1x, 원본은 _v5/obsidian.png)."""
from PIL import Image
import random, math
src=Image.open('_v5/obsidian.png').convert('RGBA'); src=src.crop(src.getbbox())
w,h=src.size; P=14; out=Image.new('RGBA',(w+2*P,h+10)); out.alpha_composite(src,(P,0)); px=out.load()
rng=random.Random(5); W,H=out.size
DARK=[(14,10,20),(24,18,34),(38,28,50),(56,42,70)]; CRY=[(40,20,70),(80,44,130),(140,90,210),(190,150,240)]
# 몸통 밑단 수평선 제거: 열마다 0~9px 불규칙하게 깎고 둔덕이 덮는다
for x in range(P,P+w):
    cut=rng.choice((2,4,5,7,9,3,6))
    for y in range(h-cut,h): px[x,y]=(0,0,0,0)
# 암반 둔덕: 열마다 높이 노이즈, 위쪽 1px 하이라이트
cx=W/2; half=w/2+P-2
for x in range(W):
    t=abs(x-cx)/half
    if t>=1: continue
    hh=int(max(12,26*(1-t**2.2))+3*math.sin(x*.7)+2*math.sin(x*.23+1)+rng.choice((0,0,1,-1)))
    for d in range(max(1,hh)):
        y=H-1-d
        c=DARK[1] if d==hh-1 else DARK[0] if d<3 else DARK[2] if (x+d)%5==0 else DARK[1]
        if d==hh-1 and x<cx: c=DARK[3]
        px[x,y]=c+(255,)
# 작은 결정 무리: 기울어진 가는 삼각형
for _ in range(16):
    bx=int(cx+rng.uniform(-half*.85,half*.85)); ln=rng.randint(8,24); wd=rng.randint(2,4); lean=rng.uniform(-.5,.5)
    by=H-3-rng.randint(4,14)
    for i in range(ln):
        f=1-i/ln; hw=max(0,int(wd*f+.5))
        for dx in range(-hw,hw+1):
            x=int(bx+lean*i)+dx; y=by-i
            if 0<=x<W and 0<=y<H:
                px[x,y]=(CRY[3] if dx==-hw and i>1 else CRY[2] if dx<0 else CRY[1] if dx==0 else CRY[0])+(255,)
out.save('obsidian.png'); print(out.size)
