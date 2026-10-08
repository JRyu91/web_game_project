"""zone 공용 미리보기 헬퍼 (junction.py 동작은 건드리지 않음)."""
from PIL import Image
from junction import Shadows

def render_shadows(SH, tod):
    """junction.Shadows 를 1.25배 폭으로 + 중심 코어(더 짙게) 한 겹 추가. 어두운 아스팔트에서도 읽히게."""
    S = Shadows(SH.sun_x); S.s = [(cx, gy, int(w * 1.25), wt) for cx, gy, w, wt in SH.s]
    L = S.render(tod, col=(6, 4, 8))
    C = Shadows(SH.sun_x); C.s = [(cx, gy, int(w * .7), min(1, wt + .6)) for cx, gy, w, wt in SH.s]
    core = C.render(tod, col=(0, 0, 2))
    L.alpha_composite(core); return L

def rim(im, col=(170,200,245,235)):
    """미리보기 전용 1px 냉색 외곽선(Unity 에선 outline 셰이더로 대체, manifest 참고)."""
    a = im.getchannel('A'); px = a.load(); out = Image.new('RGBA', (im.width+2, im.height+2)); op = out.load()
    for y in range(im.height):
        for x in range(im.width):
            if px[x, y] > 128:
                for dx, dy in ((1,0),(-1,0),(0,1),(0,-1)):
                    X, Y = x+dx, y+dy
                    if not (0 <= X < im.width and 0 <= Y < im.height and px[X, Y] > 128): op[X+1, Y+1] = col
    out.alpha_composite(im, (1, 1)); return out.crop((1, 1, im.width+1, im.height+1))

