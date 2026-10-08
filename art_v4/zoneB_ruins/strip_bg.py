# 테두리 코너색 기준 flood fill로 배경 제거 (전역 색 제거 아님)
import sys
from collections import deque
from PIL import Image
TOL = 24
for f in sys.argv[1:]:
    im = Image.open(f).convert('RGBA'); px = im.load(); W, H = im.size
    ref = px[0, 0][:3]
    close = lambda p: p[3] > 0 and sum(abs(p[i]-ref[i]) for i in range(3)) <= TOL
    q = deque((x, y) for x in range(W) for y in (0, H-1)); q.extend((x, y) for y in range(H) for x in (0, W-1))
    seen = set()
    while q:
        x, y = q.popleft()
        if (x, y) in seen or not (0 <= x < W and 0 <= y < H): continue
        seen.add((x, y))
        if not close(px[x, y]): continue
        px[x, y] = (0, 0, 0, 0)
        q.extend(((x+1, y), (x-1, y), (x, y+1), (x, y-1)))
    im.save(f); print(f, im.getbbox())
