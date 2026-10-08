"""Stage 3 무기: 사용 프레임(clip_table idle/walk/attack/attack1/attack2)에서 장갑색 군집 자동 시드 + 번호 오버레이 시트.
출력: scratch 시트(후보 번호) → 사람이 hand_table.json 에서 고름/보정."""
import json, os, sys
import numpy as np
from PIL import Image, ImageDraw

def label(m):
    """8-연결 군집 라벨(scipy 없이)"""
    lab = np.zeros(m.shape, int); n = 0
    for y0, x0 in zip(*np.where(m)):
        if lab[y0, x0]: continue
        n += 1; st = [(y0, x0)]; lab[y0, x0] = n
        while st:
            y, x = st.pop()
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    yy, xx = y + dy, x + dx
                    if 0 <= yy < m.shape[0] and 0 <= xx < m.shape[1] and m[yy, xx] and not lab[yy, xx]:
                        lab[yy, xx] = n; st.append((yy, xx))
    return lab, n
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = f'{ROOT}/Assets/Resources'
CT = {c['key']: c for c in json.load(open(f'{RES}/Config/clip_table.json'))['clips']}
CLIPS = ['idle', 'walk', 'attack', 'attack1', 'attack2']
def cands(path):
    a = np.asarray(Image.open(path).convert('RGBA')).astype(int)
    r, g, b, al = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    m = (al > 0) & (r >= 95) & (r <= 150) & (g >= 45) & (g <= 85) & (b >= 25) & (b <= 50)
    ys = np.where(al.any(1))[0]; m[ys.max() - 26:] = False   # 부츠 제외
    lab, n = label(m)
    out = []
    for i in range(1, n + 1):
        yy, xx = np.where(lab == i)
        if len(yy) >= 3: out.append((int(round(xx.mean())), int(round(yy.mean())), len(yy)))
    return out
if __name__ == '__main__':
    outdir = sys.argv[1]; res = {}
    for g in ['main_m', 'main_f']:
        rows = []
        for c in CLIPS:
            for i in CT[f'{g}/{c}']['frames']:
                p = f'{RES}/Sprites/CharactersBaked/{g}/{c}/frame_{i:02d}.png'
                res[f'{g}/{c}/{i}'] = cands(p); rows.append((c, i, p))
        S = 4; W = 148 * S; sheet = Image.new('RGB', (W * 6, W * ((len(rows) + 5) // 6)), (70, 70, 70))
        d = ImageDraw.Draw(sheet)
        for k, (c, i, p) in enumerate(rows):
            im = Image.open(p).convert('RGBA').resize((148 * S, 148 * S), Image.NEAREST)
            ox, oy = (k % 6) * W, (k // 6) * W
            sheet.paste(im, (ox, oy), im)
            d.text((ox + 4, oy + 4), f'{c} {i}', fill=(255, 255, 0))
            for j, (x, y, n) in enumerate(res[f'{g}/{c}/{i}']):
                d.rectangle((ox + x * S, oy + y * S, ox + x * S + S - 1, oy + y * S + S - 1), fill=(255, 0, 0))
                d.text((ox + x * S + 6, oy + y * S - 4), str(j), fill=(0, 255, 255))
        sheet.save(f'{outdir}/seed_{g}.png')
    json.dump(res, open(f'{outdir}/seed.json', 'w'))
