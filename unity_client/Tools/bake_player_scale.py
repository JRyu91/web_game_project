"""플레이어(main_m/main_f 전 클립) + 무기 + 비정수 배율 몬스터 티어 스프라이트를 playerBakeFactor 로 한 번 리샘플해 별도 폴더에 저장.
런타임은 이 결과를 1x 로 그린다(비정수 런타임 스케일 = 믹셀 → 금지). 확대·축소 모두 순수 nearest(새 색 0) + 알파 128 하드 임계값.
축소는 클립마다 샘플 격자 위상을 골라 외곽선/눈 픽셀 손실 최소화(클립 내 동일 위상 → 프레임 간 떨림 방지).
캔버스는 짝수 px 로 패딩(중심 피벗이 반 픽셀에 걸리지 않게).
실행: python3 unity_client/Tools/bake_player_scale.py   (Config/actor_scale.json 의 factor/경로 사용)"""
import json, os, glob
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = f'{ROOT}/Assets/Resources'
cfg = json.load(open(f'{RES}/Config/actor_scale.json'))
F = cfg['playerBakeFactor']

def grid(n, f, o):
    """nearest 샘플 인덱스: dst i ← src floor((i + o/6) / f). o = 위상(0..5), 축소 시 어떤 열/행이 빠질지 결정"""
    m = max(1, round(n * f))
    return np.minimum(np.floor((np.arange(m) + o / 6) / f).astype(int), n - 1)

def important(a):
    """잃으면 티 나는 픽셀: 실루엣 외곽(투명 이웃) + 어두운 픽셀(눈·외곽선, luma<60)"""
    op = a[..., 3] >= 128
    pad = np.pad(op, 1)
    edge = op & ~(pad[:-2, 1:-1] & pad[2:, 1:-1] & pad[1:-1, :-2] & pad[1:-1, 2:])
    dark = op & ((a[..., :3] * [.299, .587, .114]).sum(-1) < 60)
    return edge | dark

def best_phase(frames, f):
    """클립 전체에서 빠지는 중요 픽셀 수가 최소인 (ox, oy) — 클립 안 모든 프레임에 같은 위상(프레임 간 떨림 방지)"""
    if f >= 1: return 0, 0
    imp = [important(np.asarray(fr)) for fr in frames]
    h, w = imp[0].shape
    def lost(axis, o):
        keep = np.zeros(w if axis else h, bool); keep[grid(w if axis else h, f, o)] = True
        return sum(int(m[:, ~keep].sum() if axis else m[~keep, :].sum()) for m in imp)
    return min(range(6), key=lambda o: lost(1, o)), min(range(6), key=lambda o: lost(0, o))

def bake_img(im, f, ox=0, oy=0):
    a = np.asarray(im)
    # 확대·축소 모두 순수 nearest(새 색 0). 축소는 클립별 위상(ox, oy)로 빠질 행/열을 고른다.
    a = a[grid(a.shape[0], f, oy)][:, grid(a.shape[1], f, ox)].copy()
    a[..., 3] = np.where(a[..., 3] >= 128, 255, 0)
    h, w = a.shape[:2]
    out = Image.new('RGBA', (w + w % 2, h + h % 2), (0, 0, 0, 0))
    out.paste(Image.fromarray(a), (0, h % 2))       # 홀수면 오른쪽/위에 1px 패딩(짝수 캔버스 = 정수 px 피벗)
    return out

def bake_dir(srcs, dst_of, f):
    frames = [Image.open(p).convert('RGBA') for p in srcs]
    ox, oy = best_phase(frames, f)
    for p, fr in zip(srcs, frames):
        d = dst_of(p); os.makedirs(os.path.dirname(d), exist_ok=True); bake_img(fr, f, ox, oy).save(d)
    return ox, oy

n = 0; phases = {}
for d in sorted(glob.glob(f'{RES}/Sprites/Characters/*/*/')):
    srcs = sorted(glob.glob(d + '*.png')); n += len(srcs)
    phases[d.split('/Characters/')[1].rstrip('/')] = bake_dir(srcs, lambda p: p.replace('/Sprites/Characters/', '/' + cfg['playerRoot'] + '/'), F)
for src in glob.glob(f'{RES}/Sprites/Weapons/*.png'):
    bake_dir([src], lambda p: p.replace('/Sprites/Weapons/', '/' + cfg['weaponRoot'] + '/'), F); n += 1
for i, f in enumerate(cfg['tiers']):   # 비정수 몬스터 배율만 굽는다(정수는 런타임 nearest)
    if abs(f - round(f)) < 1e-4 and f >= 1: continue
    for d in glob.glob(f'{RES}/Sprites/Monsters/t{i+1}/*/'):
        srcs = sorted(glob.glob(d + '*.png')); n += len(srcs)
        bake_dir(srcs, lambda p: p.replace(f'/Sprites/Monsters/t{i+1}/', f"/{cfg['monsterBakedRoot']}/t{i+1}/"), f)
print('player clip phases (ox, oy):', phases)
idle = Image.open(f"{RES}/{cfg['playerRoot']}/main_m/idle/frame_00.png")
print(f'baked {n} files, factor {F}, main_m idle opaque h = {idle.getbbox()[3] - idle.getbbox()[1]}px')
