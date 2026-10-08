"""수동 보정용: 사용 프레임을 4배 + 10px 격자(좌표 라벨)로 시트화. hand_table.json 을 주면 손 점(빨강)·방향 선 오버레이."""
import json, os, sys
from PIL import Image, ImageDraw
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__))); RES = f'{ROOT}/Assets/Resources'
CT = {c['key']: c for c in json.load(open(f'{RES}/Config/clip_table.json'))['clips']}
CLIPS = ['idle', 'walk', 'attack', 'attack1', 'attack2']
def sheet(g, out, table=None, clips=CLIPS, S=4, x0=20, y0=10, w=110, h=125):
    rows = [(c, i) for c in clips for i in CT[f'{g}/{c}']['frames']]
    cw, ch = w * S, h * S; cols = 6
    im = Image.new('RGB', (cw * cols, ch * ((len(rows) + cols - 1) // cols)), (60, 60, 60)); d = ImageDraw.Draw(im)
    for k, (c, i) in enumerate(rows):
        fr = Image.open(f'{RES}/Sprites/CharactersBaked/{g}/{c}/frame_{i:02d}.png').convert('RGBA').crop((x0, y0, x0 + w, y0 + h)).resize((cw, ch), Image.NEAREST)
        ox, oy = (k % cols) * cw, (k // cols) * ch; im.paste(fr, (ox, oy), fr)
        for gx in range(0, w, 10): d.line((ox + gx * S, oy, ox + gx * S, oy + ch), fill=(0, 90, 160)); d.text((ox + gx * S + 1, oy + ch - 12), str(x0 + gx), fill=(0, 200, 255))
        for gy in range(0, h, 10): d.line((ox, oy + gy * S, ox + cw, oy + gy * S), fill=(0, 90, 160)); d.text((ox + 1, oy + gy * S + 1), str(y0 + gy), fill=(0, 200, 255))
        d.text((ox + 40, oy + 4), f'{c} {i}', fill=(255, 255, 0))
        if table and f'{g}/{c}/{i}' in table:
            e = table[f'{g}/{c}/{i}']; hx, hy = (e['x'] - x0) * S, (e['y'] - y0) * S
            d.rectangle((ox + hx, oy + hy, ox + hx + S - 1, oy + hy + S - 1), fill=(255, 0, 0))
    im.save(out)
if __name__ == '__main__':
    t = json.load(open(sys.argv[3]))['hands'] if len(sys.argv) > 3 else None
    sheet(sys.argv[1], sys.argv[2], t)
