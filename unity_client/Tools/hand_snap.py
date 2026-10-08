"""hand_table.json 의 수동 추정 손 좌표를 반경 3px 안 장갑 하이라이트 군집 중심으로 스냅(없으면 그대로). 결과를 덮어쓰고 이동량 출력."""
import json, os
import numpy as np
from PIL import Image
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__))); RES = f'{ROOT}/Assets/Resources'
P = f'{RES}/Config/hand_table.json'; T = json.load(open(P))
for k, e in T['hands'].items():
    g, c, i = k.split('/')
    a = np.asarray(Image.open(f'{RES}/Sprites/CharactersBaked/{g}/{c}/frame_{int(i):02d}.png').convert('RGBA')).astype(int)
    r, gg, b, al = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    m = (al > 0) & (r >= 95) & (r <= 150) & (gg >= 45) & (gg <= 85) & (b >= 25) & (b <= 50)
    x0, y0 = e.get('ex', e['x']), e.get('ey', e['y']); e['ex'], e['ey'] = x0, y0
    yy, xx = np.where(m[max(0, y0 - 3):y0 + 4, max(0, x0 - 3):x0 + 4])
    if len(xx) >= 2:
        e['x'], e['y'] = int(round(xx.mean() + max(0, x0 - 3))), int(round(yy.mean() + max(0, y0 - 3)))
    print(k, (x0, y0), '->', (e['x'], e['y']), len(xx))
json.dump(T, open(P, 'w'), ensure_ascii=False, indent=0)
