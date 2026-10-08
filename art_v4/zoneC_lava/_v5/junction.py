"""오브젝트-지면 접합 공통 헬퍼 (zone A/B). 전부 1x 픽셀, 블러 없음, wrap 좌표.

- contact(im)          : 스프라이트 바닥 접지 폭 (x0, x1) — 아래 4줄의 불투명 범위
- ao(im, k)            : 각 열의 가장 아래 6px 을 계단식으로 어둡게 (앰비언트 오클루전)
- Shadows              : 접지 그림자 스펙 기록 → render(tod) 로 시간대별 계단/디더 타원 레이어
"""
from PIL import Image

W = 3200
SHADOW_TOD = {'day':   dict(a=1.0,  stretch=1.0,  shift=0.0),
              'dawn':  dict(a=.85,  stretch=1.35, shift=.18),    # 해가 낮다 → 길고 해 반대쪽으로 밀림
              'night': dict(a=.50,  stretch=1.1,  shift=0.0)}

def contact(im):
    b = im.getchannel('A').crop((0, im.height - 4, im.width, im.height)).getbbox()
    return (b[0], b[2]) if b else (0, im.width)

def ao(im, k=.62, rows=6):
    """열마다 최하단 불투명 픽셀부터 위로 rows 줄: k → 1 로 계단(2줄 단위) 복원."""
    im = im.copy(); px = im.load()
    for x in range(im.width):
        y = im.height - 1
        while y >= 0 and px[x, y][3] == 0: y -= 1
        if y < 0 or y < im.height - 12: continue                     # 바닥에 닿지 않는 열(가지 등)은 제외
        for i in range(rows):
            if y - i < 0 or px[x, y - i][3] == 0: break
            f = k + (1 - k) * (i // 2) / (rows // 2)
            r, g, b, a = px[x, y - i]; px[x, y - i] = (int(r*f), int(g*f), int(b*f), a)
    return im

class Shadows:
    def __init__(self, sun_x=None): self.s = []; self.sun_x = sun_x
    def add(self, x, ground_y, im, weight=.5):
        """x: 스프라이트 좌상단 x, weight 0..1 (무거울수록 진함·높음)."""
        c0, c1 = contact(im); w = max(10, int((c1 - c0) * 1.5) + 16)
        self.s.append((x + (c0 + c1) / 2, ground_y, w, weight))
    def render(self, tod='day', size=(W, 720), col=(18, 14, 22)):
        P = SHADOW_TOD[tod]; L = Image.new('RGBA', size); px = L.load()
        for cx, gy, w, wt in self.s:
            w = int(w * P['stretch'])
            if P['shift'] and self.sun_x is not None:
                cx += (1 if cx >= self.sun_x else -1) * w * P['shift'] / 2
            h = 4 + round(wt * 4)                                    # 4~8px
            core = int((120 + 90 * wt) * P['a']); rim = core // 2
            for dy in range(h):
                t = (dy - (h - 1) / 2) / (h / 2)                     # -1..1
                half = int(w / 2 * (1 - t * t) ** .5)
                for dx in range(-half, half + 1):
                    inner = abs(dx) < half * .6 and abs(t) < .6
                    if not inner and (dx + dy) % 2: continue         # 바깥 링은 체커 디더
                    a = core if inner else rim
                    X, Y = int(cx + dx) % size[0], gy - h // 2 + dy
                    if 0 <= Y < size[1] and px[X, Y][3] < a: px[X, Y] = col + (a,)
        return L

if __name__ == '__main__':   # 셀프체크
    t = Image.new('RGBA', (10, 20)); t.paste((200, 200, 200, 255), (2, 5, 8, 20))
    assert contact(t) == (2, 8)
    assert ao(t).getpixel((4, 19))[0] < 200 and ao(t).getpixel((4, 5))[0] == 200
    s = Shadows(); s.add(0, 50, t, 1); d = s.render('day', (100, 100)); n = s.render('night', (100, 100))
    assert d.getpixel((5, 50))[3] > n.getpixel((5, 50))[3] > 0
    print('ok')
