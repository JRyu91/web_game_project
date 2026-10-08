"""E안 Sheath C: 무기 42종 d_sheath.png (+효과 16종 fx_sheath_{j}.png). 기존 bake_weapon_dirs 규칙(nearest 회전 → 5/6 축소, 알파 이진) 재사용.
피벗 = grip(기존 d0..d7 규칙). 코드: grip 앵커 = 폼멜 m=(70,80)/f=(70,74) + L·(sin θ, -cos θ)(화면 y아래 기준 dx=L sinθ, dy=-L cosθ). Config/weapon_sheath.json = {이름:[L,θ]}.
각도: 210°(위=0, 시계) 기본, 칼끝이 지면(idle 몸 bbox 최하단)을 넘으면 5°씩 수평 쪽(+)으로 보정. 결과 Config/weapon_sheath.json.
python3 bake_sheath.py [--check]  (--check: 쓰기 없이 검사만)"""
import json, os, glob, sys, unicodedata
import numpy as np
from PIL import Image
import bake_weapon_dirs as b
RES = b.RES
FXSRC = os.path.join(b.ROOT, '..', 'unity_review/stage3/team_v6/d_round/dev/weapons_v2')
ANCHOR = {'m': (70, 80), 'f': (70, 74)}          # hip 폼멜
BACK = {'m': (68, 42), 'f': (68, 40)}            # back 폼멜(어깨 위)
GROUND = {}  # idle 4프레임 몸 bbox 최하단 행
for s in 'mf':
    GROUND[s] = min(Image.open(p).getbbox()[3] - 1 for p in glob.glob(f'{RES}/Sprites/CharactersBaked/main_{s}/idle/frame_*.png'))


def pommel(a):
    op = a[..., 3] > 0; yb = np.where(op.any(1))[0].max(); xs = np.where(op[yb - 2:yb + 1].any(0))[0]
    return int(round(xs.mean())), int(yb)


def bake(im, pm, ang, f):  # pm = 캔버스 중심(피벗)으로 보낼 점
    p = b.pad(im, *pm).rotate(-ang, resample=Image.NEAREST, center=(b.C, b.C))
    return b.down(p, f)


def depth(sp):  # 피벗(캔버스 중심) 아래 최대 불투명 행 수
    ys = np.where(np.asarray(sp)[..., 3].any(1))[0]; return int(ys.max()) - sp.height // 2


BODY = {s: np.asarray(Image.open(f'{RES}/Sprites/CharactersBaked/main_{s}/idle/frame_00.png').convert('RGBA'))[..., 3] > 0 for s in 'mf'}


def hidden(sp, s, A=None):  # 폼멜 피벗 sp 를 back 앵커에 놓았을 때 몸 실루엣에 가려지는 무기 픽셀 비율
    ax, ay = (A or BACK)[s]; a = np.asarray(sp)[..., 3] > 0; ys, xs = np.nonzero(a); X, Y = xs + ax - sp.width // 2, ys + ay - sp.height // 2
    B = BODY[s]; ok = (X >= 0) & (Y >= 0) & (X < B.shape[1]) & (Y < B.shape[0])
    return float(B[Y[ok], X[ok]].sum()) / max(1, len(xs))


if __name__ == '__main__':
    check = '--check' in sys.argv; out = {'_note': '피벗=폼멜. 앵커(좌상 원점, 픽셀 모서리) m=(70,80) f=(70,74). angle=위0 시계', 'ground': GROUND, 'weapons': {}}
    for src in sorted(glob.glob(f'{RES}/Sprites/Weapons/*.png')):
        name = unicodedata.normalize('NFC', os.path.splitext(os.path.basename(src))[0]); im = Image.open(src).convert('RGBA')
        pm = pommel(np.asarray(im)); f = b.factor(name)
        fxd = os.path.join(FXSRC, name)
        if not os.path.isdir(fxd): fxd = os.path.join(FXSRC, unicodedata.normalize('NFD', name))
        fxs = [(os.path.basename(fp)[2:-4], Image.open(fp).convert('RGBA')) for fp in sorted(glob.glob(f'{fxd}/fx[0-9].png'))]
        def bk(ang): sp = bake(im, pm, ang, f); return sp, max(depth(q) for q in [sp] + [bake(x, pm, ang, f) for _, x in fxs])
        sp, dp = bk(210); mode, ang, hid = 'hip', 210, None
        if not all(ANCHOR[s][1] + dp <= GROUND[s] for s in 'mf'):
            mode, best = 'back', None
            for a_ in range(200, 216, 5):   # 지면 통과 + 몸 뒤 숨김 비율 최대
                sp_, dp_ = bk(a_)
                if not all(BACK[s][1] + dp_ <= GROUND[s] for s in 'mf'): continue
                h = min(hidden(sp_, s) for s in 'mf')
                if best is None or h > best[0]: best = (h, a_, sp_, dp_)
            if best is None: print('PENETRATE', name); mode, ang = 'FAIL', 210
            else: hid, ang, sp, dp = best
        A = BACK if mode == 'back' else ANCHOR
        out['weapons'][name] = {'mode': mode, 'angle': ang, 'pommel': pm, 'depth': dp, 'hidden': hid, 'clear_m': GROUND['m'] - A['m'][1] - dp, 'clear_f': GROUND['f'] - A['f'][1] - dp}
        print(name, ang, dp, out['weapons'][name]['clear_m'], out['weapons'][name]['clear_f'])
        if check: continue
        g = b.grip(np.asarray(im), name); out['weapons'][name]['L'] = round((pm[1] - g[1]) * f, 2)  # 코드 담당 요청: 피벗=grip
        d = f'{RES}/Sprites/WeaponsDir/{b.folder(name)}'; bake(im, g, ang, f).save(f'{d}/d_sheath.png')
        for j, x in fxs: bake(x, g, ang, f).save(f'{d}/fx_sheath_{j}.png')
    if not check:
        json.dump({n: [w['L'], w['angle'], w['mode']] for n, w in out['weapons'].items()}, open(f'{RES}/Config/weapon_sheath.json', 'w'), ensure_ascii=False, indent=1)
        json.dump(out, open(os.path.join(b.ROOT, '..', 'unity_review/stage3/team_v6/e_round/asset/sheath_check.json'), 'w'), ensure_ascii=False, indent=1)
