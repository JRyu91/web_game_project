# 새 PixelLab 에셋 전처리: 불투명 배경 제거(테두리 flood fill) + 파사드 하단 인도 잘라내기
from PIL import Image
from collections import deque
def unbg(src, dst, tol=40, cut_bottom=None):
    im=Image.open(src).convert('RGBA'); px=im.load(); W,H=im.size
    bgc=px[0,0]; seen=set(); q=deque((x,y) for x in range(W) for y in (0,H-1))
    q.extend((x,y) for y in range(H) for x in (0,W-1))
    while q:
        x,y=q.popleft()
        if (x,y) in seen or not(0<=x<W and 0<=y<H): continue
        seen.add((x,y)); p=px[x,y]
        if sum(abs(p[i]-bgc[i]) for i in range(3))>tol: continue
        px[x,y]=(0,0,0,0); q.extend(((x+1,y),(x-1,y),(x,y+1),(x,y-1)))
    if cut_bottom: im=im.crop((0,0,W,cut_bottom))
    im.crop(im.getbbox()).save(dst)
unbg('skyline_raw.png','skyline.png')
unbg('lm_facade_raw.png','lm_facade.png',cut_bottom=172)
