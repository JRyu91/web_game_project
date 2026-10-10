"""Remove baked sword trails from all staff attack clips; preserve the original body assets and anchors."""
from pathlib import Path
from PIL import Image
import numpy as np

ROOT = Path(__file__).resolve().parents[1] / 'Assets/Resources/Sprites/CharactersBaked'


def clean(source, frame, reference):
    a = np.array(source.convert('RGBA'))
    rgb = a[..., :3].astype(int)
    white = (a[..., 3] > 0) & (rgb.min(2) >= 100) & (rgb.max(2) - rgb.min(2) <= 40) & (abs(rgb[..., 0] - rgb[..., 1]) <= 10)
    y, x = np.indices(white.shape)
    mask = np.zeros_like(white)
    if frame == 4:
        mask = white & (y < 51)
    elif frame == 5:
        mask = white & (x < 50) & (y < 60)
    elif frame == 7:
        mask = white & ((x < 63) | (x > 85) | ((y >= 90) & ((x < 64) | (x > 82))))
        mask &= (y >= 56) & (y <= 98)
    elif frame == 8:
        mask = white & (((x >= 82) & (x <= 95) & (y >= 91) & (y <= 103)) | ((x >= 42) & (x <= 57) & (y >= 101) & (y <= 109)))
    elif frame == 9:
        mask = white & ((x >= 87) | (y >= 100))
    # The face, skin and cloth center never belong to an external sword trail.
    protected = ((x >= 50) & (x <= 85) & (y >= 55) & (y < 75)) | ((x >= 64) & (x <= 82) & (y >= 75) & (y <= 98))
    protected |= (rgb[..., 0] - rgb[..., 1] > 10) | (y >= 114)
    mask &= ~protected
    embedded = np.zeros_like(mask)
    if frame == 7:
        embedded = white & (x >= 59) & (x <= 85) & (y >= 86) & (y <= 99)
        mask |= embedded
        protected &= ~embedded
    out = a.copy()
    out[mask] = 0
    # Frames 8/9 share the settled pose with 10: restore the leg under the trail.
    if frame in (8, 9):
        ref = np.array(reference.convert('RGBA'))
        restore = mask & (ref[..., 3] > 0)
        out[restore] = ref[restore]
    if frame == 7:
        ref = np.array(reference.convert('RGBA'))
        yy, xx = np.where(embedded)
        out[yy, xx] = ref[yy + 3, xx + 5]
    assert np.array_equal(out[protected], a[protected])
    assert np.array_equal(out[~mask], a[~mask])
    assert set(np.unique(out[..., 3])).issubset({0, 255})
    return Image.fromarray(out), int(mask.sum())


def clean_other(source, palette, reference, restore_body=False):
    a = np.array(source.convert('RGBA'))
    mask = np.zeros(a.shape[:2], dtype=bool)
    for color in palette:
        mask |= np.all(a == color, axis=2)
    rgb = a[..., :3].astype(int)
    ref = np.array(reference.convert('RGBA'))
    neutral = (a[..., 3] > 0) & (rgb.min(2) >= 140) & (rgb.max(2) - rgb.min(2) <= 70) & (rgb[..., 0] - rgb[..., 1] <= 10)
    # Trail anti-alias colors can also occur in clothing: remove only the immediate
    # fringe of confirmed trail colors, never unchanged reference-body pixels.
    same = np.all(a == ref, axis=2) & (ref[..., 3] > 0)
    for _ in range(2):
        p = np.pad(mask, 1)
        near = np.zeros_like(mask)
        for dy in range(3):
            for dx in range(3):
                near |= p[dy:dy+mask.shape[0], dx:dx+mask.shape[1]]
        mask |= near & neutral & ~same
    out = a.copy()
    out[mask] = 0
    # Remove detached antialiased trail specks; original body is one connected
    # silhouette in these six affected frames. This also catches warm FX shades.
    remaining = out[..., 3] > 0
    seen = np.zeros_like(remaining)
    components = []
    for yy, xx in zip(*np.where(remaining)):
        if seen[yy, xx]:
            continue
        stack = [(yy, xx)]; seen[yy, xx] = True; component = []
        while stack:
            y, x = stack.pop(); component.append((y, x))
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < remaining.shape[0] and 0 <= nx < remaining.shape[1] and remaining[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True; stack.append((ny, nx))
        components.append(component)
    if mask.any():
        for component in components:
            if len(component) <= 40:
                for y, x in component:
                    mask[y, x] = True; out[y, x] = 0
        if restore_body:
            restore = mask & (ref[..., 3] > 0)
            out[restore] = ref[restore]
    # Exact trail-only colors: every other original pixel, including warm skin,
    # dark hair/limbs and the complete bottom 30 rows, is byte-for-byte preserved.
    assert np.array_equal(out[~mask], a[~mask])
    assert np.array_equal(out[-30:], a[-30:])
    return Image.fromarray(out), int(mask.sum())


if __name__ == '__main__':
    for gender in ('main_m', 'main_f'):
        for clip in ('attack', 'attack1', 'attack2'):
            source = ROOT / gender / clip
            dest = ROOT / gender / ('staff_' + clip)
            dest.mkdir(exist_ok=True)
            baseline = set()
            for i in (0, 1, 2, 3, 4, 5, 6, 10):
                baseline.update(map(tuple, np.array(Image.open(source / f'frame_{i:02}.png').convert('RGBA')).reshape(-1, 4).tolist()))
            palette = set()
            if clip != 'attack2':
                for i in (7, 8, 9):
                    colors = set(map(tuple, np.array(Image.open(source / f'frame_{i:02}.png').convert('RGBA')).reshape(-1, 4).tolist()))
                    palette.update(c for c in colors - baseline if c[3] and min(c[:3]) >= 100 and c[0] - c[1] <= 10 and max(c[:3]) - min(c[:3]) <= 80)
            reference = Image.open(source / 'frame_10.png')
            settled, _ = clean(Image.open(source / 'frame_08.png'), 8, reference) if gender == 'main_f' and clip == 'attack2' else (reference, 0)
            for path in sorted(source.glob('*.png')):
                frame = int(path.stem.split('_')[-1])
                original = Image.open(path).convert('RGBA')
                if clip == 'attack2':
                    result, changed = clean(original, frame, settled if frame == 7 else reference) if gender == 'main_f' else (original, 0)
                else:
                    result, changed = clean_other(original, palette, Image.open(source / ('frame_08.png' if gender == 'main_m' and clip == 'attack' else 'frame_10.png')), gender == 'main_m' and clip == 'attack' and frame == 7)
                arr = np.array(original); actual = np.array(result)
                y, x = np.indices(arr.shape[:2]); rgb = arr[..., :3].astype(int)
                skin = (arr[..., 3] > 0) & (rgb[..., 0] - rgb[..., 1] > 10) & (x >= 48) & (x <= 100) & (y >= 48) & (y <= 85)
                assert np.array_equal(actual[skin], arr[skin]), (gender, clip, frame, 'skin')
                assert result.size == original.size
                assert np.array_equal(np.array(result)[-30:], np.array(original)[-30:])
                result.save(dest / path.name)
                print(gender, clip, frame, changed)
