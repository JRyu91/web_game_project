"""무기 42종 × 8방향(45° 단위, 0=칼끝 위, 시계방향) 사전 생성. 사용자 결정: 크기 5/6(플레이어와 같은 nearest), 회전 8방향.
방식 A(채택 기본): 1x 원본을 손잡이 중심 기준 nearest 회전 → 5/6 nearest(위상 0). 방식 B: 5/6 먼저 → 회전. 비교 시트는 --compare.
출력: Resources/Sprites/WeaponsDir/<파일명>/d0..d7.png (160x160, 손잡이 = 캔버스 중심 → 피벗 중심), Config/weapon_grip.json(원본 좌표)."""
import json, os, glob, sys
import numpy as np
from PIL import Image
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__))); RES = f'{ROOT}/Assets/Resources'
F = 5 / 6; C = 96
DAGGER = 0.7                            # 사용자 결정(2026-09-30): 단검류 = 5/6 의 70% (≈52px). 이름에 '단검'
def factor(name): return F * DAGGER if '단검' in name else F                       # 192 캔버스, 손잡이 (96,96) → 5/6 후 (80,80) 정확히
def folder(name):
    """Resources.Load 가 한글 폴더명을 못 찾아서(실측) UTF-8 hex 폴더명 사용. C# GearAttachment.Folder 와 동일 규칙"""
    import unicodedata; return 'w_' + unicodedata.normalize('NFC', name).encode().hex().upper()
STAFF_GRIP = 1 / 3   # c5(형 결정): 지팡이는 자루 끝(bbox 아래)에서 전체 길이 1/3 지점을 쥠
def grip(a, name=''):
    """손잡이 중심: 불투명 최하단 행에서 위로 4~12px 구간 불투명 픽셀 중심(원본 96 캔버스, 날 위/손잡이 아래).
    지팡이/스태프: 행 y = 아래 끝 − STAFF_GRIP·(아래−위), x = 그 행에서 자루 x(아래 끝 손잡이 x)에 가장 가까운 불투명 연속 구간 중심(장식 날개 제외)"""
    op = a[..., 3] > 0; ys = np.where(op.any(1))[0]; yb = ys.max()
    yy, xx = np.where(op[yb - 12:yb - 3]); gx, gy = int(round(xx.mean())), int(round(yy.mean() + yb - 12))
    if '지팡이' in name or '스태프' in name:
        gy = int(round(yb - STAFF_GRIP * (yb - ys.min()))); r = np.nonzero(op[gy])[0]
        runs = np.split(r, np.nonzero(np.diff(r) > 1)[0] + 1); run = min(runs, key=lambda q: min(abs(q - gx)))
        if len(run) <= 12: gx = int(round(run.mean()))   # 장식이 자루를 덮은 행(L100 용 머리)은 아래 손잡이 x 유지
    return gx, gy
def pad(im, gx, gy):
    out = Image.new('RGBA', (2 * C, 2 * C), (0, 0, 0, 0)); out.paste(im, (C - gx, C - gy)); return out
def rot(im, k): return im.rotate(-45 * k, resample=Image.NEAREST, center=(im.width / 2, im.height / 2)) if k % 8 else im
def down(im, f=F):
    a = np.asarray(im); n = a.shape[0]; idx = np.minimum(np.floor(np.arange(round(n * f)) / f).astype(int), n - 1)
    a = a[idx][:, idx].copy(); a[..., 3] = np.where(a[..., 3] >= 128, 255, 0); return Image.fromarray(a)
def bake(src, method='A'):
    im = Image.open(src).convert('RGBA'); gx, gy = grip(np.asarray(im), os.path.basename(src)); p = pad(im, gx, gy)
    f = factor(os.path.basename(src))  # 192*f 캔버스, 손잡이 = 96*f = 정중앙(단검 0.5833 → 112px, 56)
    return [down(rot(p, k), f) if method == 'A' else rot(down(p, f), k) for k in range(8)], (gx, gy)
if __name__ == '__main__':
    srcs = sorted(glob.glob(f'{RES}/Sprites/Weapons/*.png')); grips = {}
    if '--compare' in sys.argv:
        out = sys.argv[-1]; picks = srcs[:6]
        S = Image.new('RGBA', (160 * 8, 160 * 12), (60, 60, 60, 255))
        for r, s in enumerate(picks):
            for m, meth in enumerate('AB'):
                ims, _ = bake(s, meth)
                for k, d in enumerate(ims): S.alpha_composite(d, (k * 160, (r * 2 + m) * 160))
        S.resize((S.width * 2, S.height * 2), Image.NEAREST).save(out); sys.exit()
    for s in srcs:
        name = os.path.splitext(os.path.basename(s))[0]; ims, g = bake(s, 'A'); grips[name] = g
        d = f'{RES}/Sprites/WeaponsDir/{folder(name)}'; os.makedirs(d, exist_ok=True)
        for k, im in enumerate(ims): im.save(f'{d}/d{k}.png')
    json.dump({'_note': '원본 96 캔버스 손잡이 중심(x,y). 구운 스프라이트는 이 점이 캔버스 중심(피벗)', 'grip': grips}, open(f'{RES}/Config/weapon_grip.json', 'w'), ensure_ascii=False, indent=0)
    print('baked', len(srcs), 'x 8')
