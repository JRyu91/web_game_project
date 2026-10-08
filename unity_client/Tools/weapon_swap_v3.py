"""dev3: PixelLab 가 무기를 '그려 넣은' 프레임(초록 크로마 placeholder 칼날)에서 칼날만 42종 무기로 교체.
frame(raw, 208 canvas) -> 초록 마스크 PCA 로 칼 축/손 끝 추정 -> 원 무기(1x, 손잡이 피벗)를 그 축으로 nearest 회전해 손 위치에 배치
-> 초록 지움 + 몸 안쪽 구멍 메움 -> 무기 -> 손 주변 원본 픽셀을 다시 위에 (손이 손잡이를 감싸도록).
출력은 5/6 로 구운 프레임(round1~). 사용: python3 weapon_swap_v3.py <frame.png> <weapon_name> <out.png>"""
import json, math, os, sys
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, '..', 'Assets', 'Resources')
_GJ = json.load(open(os.path.join(RES, 'Config', 'weapon_grip.json'))); GRIP = _GJ['grip']
KEY_OVR = _GJ.get('key_override', {})   # a_round r2: {무기: {키: {shift_px}}} — L100 K3 전용
K_BACK = {'sword': 11, 'staff': 3}   # ponytail: 초록 끝점→손잡이 중심 거리(px) 보정 노브, 클래스별 1값
HAND_R = 6                          # 손 덮개 반경(px)
MIN_GREEN = 25
FORCE_DEG = None                   # c3: 지정 시 8방향 스냅 대신 이 각도(위 기준 시계, 도)로 원본 nearest 회전 후 굽기(지팡이 K6 4시=120). None = 기존 동작
HEAD_FWD = False                    # True: 지팡이 머리 = 화면 오른쪽(적 방향) 끝. 공격(찌르기/시전)용, devA attack_r1
FLIP = {'L090_종말의낫'}          # 비대칭 헤드: 칼축이 화면 왼쪽을 향하면 좌우 반전해 날이 항상 진행방향(앞)을 보게
FX_W = 44                            # 원본 bbox 폭이 이보다 넓으면 FX 무기 → 축에서 CORE_HALF 밖 픽셀은 FX 레이어(몸 뒤)
RIM = .5                           # 초록 픽셀이 시퀀스 최대의 이 비율 미만이면 '테두리만 초록' 프레임
CORE_HALF = 6                      # 이보다 적으면 칼날 소실 프레임 → 교체 불가(원본 그대로 + 플래그)


def green_mask(a):
    r, g, b, al = [a[..., i].astype(int) for i in range(4)]
    return (al > 128) & (g > r + 30) & (g > b + 30)


def erode(m):
    e = m.copy()
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)): e &= np.roll(m, (dy, dx), (0, 1))
    return e


def fit_axis(a, m):
    e = erode(m)                                # 1px 잔상(FX 궤적)은 깎여 나가고 칼날 몸통만 남음
    ys, xs = np.nonzero(e if e.sum() >= 10 else m)
    pts = np.stack([xs, ys], 1).astype(float); mu = pts.mean(0)
    u = np.linalg.svd(pts - mu, full_matrices=False)[2][0]
    body = np.stack(np.nonzero((a[..., 3] > 128) & ~m)[::-1], 1).mean(0)
    if np.dot(mu - body, u) < 0: u = -u        # u: 손 → 칼끝
    t = (pts - mu) @ u
    return mu + u * np.percentile(t, 1), u      # 손쪽 끝점, 방향


def fill_holes(a, hole):
    a = a.copy()
    for _ in range(4):
        op = a[..., 3] > 128; new = []
        for y, x in zip(*np.nonzero(hole & ~op)):
            nb = [(y + dy, x + dx) for dy in (-1, 0, 1) for dx in (-1, 0, 1) if (dy or dx)]
            src = [a[p] for p in nb if 0 <= p[0] < a.shape[0] and 0 <= p[1] < a.shape[1] and op[p]]
            if len(src) >= 5: new.append(((y, x), src[0]))
        for p, c in new: a[p] = c
    return a


def grey_band(a, B, u, half=7):
    # 초록이 회색 강철로 '번역'된 프레임용 폴백: 직전 축 띠 안의 저채도 픽셀 = 칼날
    yy, xx = np.mgrid[:a.shape[0], :a.shape[1]]; d = np.stack([xx - B[0], yy - B[1]], -1)
    t = d @ u; n = np.abs(d @ np.array([-u[1], u[0]]))
    # 색이 회색·보라·분홍으로 번져도 잡도록 색 조건 없음. ponytail: 띠 안 몸통 픽셀도 지워질 수 있음 → 해당 프레임은 수동 확인
    return (a[..., 3] > 128) & (t > 6) & (t < 110) & (n <= half)


def q(a): return (a[..., 0] // 24).astype(int) * 10000 + (a[..., 1] // 24) * 100 + a[..., 2] // 24


def novel_mask(a, pal):
    # 칼날 색이 초록→회색/보라/분홍으로 '번역'된 프레임: 첫 프레임 몸 팔레트에 없는 색 = 칼날 후보
    return (a[..., 3] > 128) & ~np.isin(q(a), pal)


def swap(frame, wname, cls, prev=None, pal=None, gmax=0):
    a = np.asarray(frame.convert('RGBA')).copy(); m = green_mask(a); mode = 'green'
    if m.sum() >= MIN_GREEN:
        B, u = fit_axis(a, m)
        if m.sum() < RIM * gmax:
            # round1: 초록이 테두리만 남고 칼날 속은 회색으로 번진 프레임(남 idle 3~5 등) → 초록 테두리가 감싼 띠 전체를 칼날로 지움.
            # 안 지우면 placeholder 큰 회색 칼날이 교체 무기 뒤에 남아 '단검이 갑자기 커지는' 것처럼 보임
            ys, xs = np.nonzero(m); d = np.stack([xs - B[0], ys - B[1]], 1) @ np.array([[u[0], -u[1]], [u[1], u[0]]])
            yy, xx = np.mgrid[:a.shape[0], :a.shape[1]]; g = np.stack([xx - B[0], yy - B[1]], -1)
            t, n = g @ u, g @ np.array([-u[1], u[0]])
            m = m | ((a[..., 3] > 128) & (t >= d[:, 0].min() + 4) & (t <= d[:, 0].max() + 1) & (n >= d[:, 1].min() - 1) & (n <= d[:, 1].max() + 1))
            mode = 'rim'
    elif prev is not None:
        nm = novel_mask(a, pal) | m if pal is not None else m
        if erode(nm).sum() >= 10: B, u = fit_axis(a, nm); B = B if np.linalg.norm(B - prev[0]) < 15 else prev[0]
        else: B, u = prev
        m = m | nm | grey_band(a, B, u); mode = 'drift'
    else: return frame, 'none', None
    w = Image.open(os.path.join(RES, 'Sprites', 'Weapons', wname + '.png')).convert('RGBA')
    gx, gy = GRIP[wname]
    ys, xs = np.nonzero(m); pts = np.stack([xs, ys], 1).astype(float); t = (pts - B) @ u
    t0, t1 = np.percentile(t, 1), np.percentile(t, 99)
    if cls == 'staff' and mode == 'green':
        # 지팡이는 몸을 가로질러 쥠 → '몸 바깥쪽' 대신 굵은 쪽(머리 구슬)을 머리로. 무기 머리 끝을 그 끝에 맞춤
        L = t1 - t0; nh, nl = (t > t1 - .15 * L).sum(), (t < t0 + .15 * L).sum()
        if max(nh, nl) < 1.3 * min(nh, nl): head_hi = (B + u * t1)[1] < (B + u * t0)[1]   # 굵기 차이 없으면 화면상 위쪽 끝 = 머리
        else: head_hi = nh > nl
        if HEAD_FWD: head_hi = (B + u * t1)[0] > (B + u * t0)[0]   # 공격(찌르기/시전): 머리 = 화면 오른쪽(적 방향) 끝
        E = B + u * (t1 if head_hi else t0); v = -u if head_hi else u      # v: 머리 → 꼬리
        top = w.getbbox()[1]; H = E + v * (gy - top); u = -v; B = E + v * L; t0, t1 = 0, L
    else:
        H = B - u * K_BACK[cls]
    # round1 fix: 무기는 bake_weapon_dirs 의 8방향 사전 구운 스프라이트(5/6, 단검 70%)만 사용 → 매 프레임 같은 픽셀.
    # (구 방식: PCA 임의각 nearest 회전 + 프레임 전체 5/6 nearest → 각도·위상마다 길이 ±수 px 요동 = '커졌다 작아졌다')
    k = FORCE_DEG / 45 if FORCE_DEG is not None else round(math.degrees(math.atan2(u[0], -u[1])) / 45) % 8       # 위(0,-1) 기준 시계방향 45° 단위
    core, fx = weapon_dir(wname, k, wname in FLIP and u[0] < 0)
    base = a.copy(); base[m] = 0
    # (2) placeholder 손잡이 잔재: 손 뒤쪽 축 위 갈색 픽셀 제거
    yy, xx = np.mgrid[:a.shape[0], :a.shape[1]]
    d0 = np.stack([xx - H[0], yy - H[1]], -1); ta = d0 @ u; na = np.abs(d0 @ np.array([-u[1], u[0]]))
    c = a[..., :3].astype(int)
    brown = (c[..., 0] > c[..., 1] + 15) & (c[..., 1] > c[..., 2]) & (c[..., 0] < 170)
    stub = (ta < -(HAND_R - 1)) & (ta > -(HAND_R + 16)) & (na <= 1.5) & brown & (a[..., 3] > 128) if cls == 'sword' else np.zeros_like(m)
    base[stub] = 0; base = fill_holes(base, m | stub)
    d = np.stack([xx - B[0], yy - B[1]], -1); tt = d @ u; nn = np.abs(d @ np.array([-u[1], u[0]]))
    front = (nn <= 1.5) & (tt > min(t0, t1) - 12) & (tt < max(t0, t1))     # 무기 축 위에 있던 비-초록 = 무기를 가린 손
    hand = (((xx - H[0]) ** 2 + (yy - H[1]) ** 2 <= HAND_R ** 2) | front) & (a[..., 3] > 128) & ~m & ~stub
    # 몸은 raw 에서 합성 후 5/6 로 굽고, 무기는 구운 좌표에 정수 배치(재샘플 없음)
    bk = lambda arr: bake(Image.fromarray(arr))
    hb = np.asarray(bake(Image.fromarray(hand)))
    # round2: 출력 캔버스 확장(PAD). 몸은 (PL, PT) 에 붙이고 무기도 같은 오프셋 → 긴 칼끝이 잘리지 않음
    bw_, bh_ = bake(frame).size; size = (bw_ + PAD[0] + PAD[2], bh_ + PAD[1] + PAD[3])
    px = (round(H[0] * F - core.width / 2) + PAD[0], round(H[1] * F - core.height / 2) + PAD[1])
    if SHIFT: px = (px[0] + round(u[0] * SHIFT * F), px[1] + round(u[1] * SHIFT * F))   # 키 오버라이드: 손(H)·덮개는 그대로, 무기만 축(u, 손→머리) 방향 이동
    def lay(im, at=None): L = Image.new('RGBA', size); L.paste(im, at or px); return L
    out = lay(fx); out.alpha_composite(lay(Image.fromarray(drop_specks(np.asarray(bk(base)))), PAD[:2])); out.alpha_composite(lay(core))
    o = np.asarray(out).copy(); pw = ((PAD[1], PAD[3]), (PAD[0], PAD[2])); ab = np.pad(np.asarray(bk(a)), pw + ((0, 0),)); hb = np.pad(hb, pw) & (not NO_COVER); o[hb] = ab[hb]
    global LAST; LAST = (H * F + np.array(PAD[:2]), u, k)   # round4: 손잡이(출력 좌표, 발선 정렬 전), 실제 축, 사용한 8방향 index
    return Image.fromarray(o), mode, (B, u)


def drop_specks(a, keep=40):
    """round1: 몸 레이어에서 떨어져 떠 있는 작은 조각(placeholder 궤적 FX 테두리 잔선·점) 제거. 8-연결 성분 < keep px"""
    a = a.copy(); op = a[..., 3] > 0; seen = np.zeros_like(op); H, W = op.shape
    for y0, x0 in zip(*np.nonzero(op)):
        if seen[y0, x0]: continue
        st, comp = [(y0, x0)], []; seen[y0, x0] = True
        while st:
            y, x = st.pop(); comp.append((y, x))
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    v, w = y + dy, x + dx
                    if 0 <= v < H and 0 <= w < W and op[v, w] and not seen[v, w]: seen[v, w] = True; st.append((v, w))
        if len(comp) < keep:
            for p in comp: a[p][3] = 0
    return a


LAST = None
AXES = []
SHIFT = 0                          # 무기 축 방향 이동(원본 px). render_clip.swap 이 KEY_OVR 로 호출 동안만 설정
NO_COVER = False                   # 측정용: 손 덮개 끔(가림 제외 무기 길이)
PAD = (0, 0, 0, 0)                 # round2: 구운 캔버스 여백 (좌, 위, 우, 아래) px. 캔버스 규칙은 round2/devC/README 참고
F = 5 / 6
def bake(im): return im.resize((round(im.width * F), round(im.height * F)), Image.NEAREST)


_DIR = {}; _SRC = {}
def weapon_dir(wname, k, flip):
    """8방향 구운 무기 (core, fx). 손잡이 = 캔버스 중심. bake_weapon_dirs 와 같은 규칙(FLIP 은 원본 좌우반전 후 동일 굽기)"""
    if k != int(k):                                   # FORCE_DEG 비정수 방향: 캐시 없이 같은 규칙으로 굽기
        k8 = int(k); weapon_dir(wname, k8, flip); import bake_weapon_dirs as bw
        return tuple(bw.down(bw.rot(_SRC[(wname, flip)][j], k), bw.factor(wname)) for j in (0, 1))
    k = int(k); key = (wname, flip)
    if key not in _DIR:
        import bake_weapon_dirs as bw
        im = Image.open(os.path.join(RES, 'Sprites', 'Weapons', wname + '.png')).convert('RGBA')
        if flip: im = im.transpose(Image.FLIP_LEFT_RIGHT)
        gx, gy = bw.grip(np.asarray(im), wname); f = bw.factor(wname); wide = im.getbbox()[2] - im.getbbox()[0] > FX_W
        p = np.asarray(bw.pad(im, gx, gy)).copy(); fxs = np.zeros_like(p)
        if wide:                                        # FX 분리: 칼 중심선(세로, 손잡이 x)에서 먼 픽셀
            far = np.broadcast_to(np.abs(np.arange(p.shape[1]) - bw.C)[None, :] > CORE_HALF, p.shape[:2])
            fxs[far] = p[far]; p[far] = 0
        P, X = Image.fromarray(p), Image.fromarray(fxs); _SRC[key] = (P, X)
        _DIR[key] = [(bw.down(bw.rot(P, i), f), bw.down(bw.rot(X, i), f)) for i in range(8)]
    return _DIR[key][k]


def swap_seq(frames, wname):
    cls = 'staff' if ('지팡이' in wname or '스태프' in wname) else 'sword'; prev = None; out = []
    a0 = np.asarray(frames[0].convert('RGBA')); pal = np.unique(q(a0)[(a0[..., 3] > 128) & ~green_mask(a0)])
    gmax = max(green_mask(np.asarray(f.convert('RGBA'))).sum() for f in frames)
    AXES.clear()
    for f in frames:
        im, mode, ax = swap(f, wname, cls, prev, pal, gmax); prev = ax or prev; out.append((im, mode))
        AXES.append(LAST if mode != 'none' else None)        # round4: 프레임별 (손잡이, 축, 8방향 index) — angle_sheet 용
    return out


if __name__ == '__main__':
    f, wn, out = sys.argv[1:4]
    (im, mode), = swap_seq([Image.open(f)], wn)
    im.save(out); print(mode)


def _selfcheck():
    a = np.zeros((20, 20, 4), np.uint8); a[2:10, 2:10, 3] = 255; a[15, 15, 3] = 255
    b = drop_specks(a); assert b[15, 15, 3] == 0 and b[5, 5, 3] == 255
