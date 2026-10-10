"""Register PixelLab outfit frames to the existing male pixel animation.

This script only splits, restores identity pixels and packs generated artwork;
it never draws replacement armour geometry.
"""
import json
from functools import cache
from pathlib import Path
import numpy as np
from PIL import Image
from bake_wearables import ANCHORS, DESIGNS, mask as polygon_mask, pack_layers, pose_data

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / 'Assets/Resources'
REVIEW = ROOT.parent / 'unity_review/stage3/team_v6/f_round/code/final'
POSES = [p for p in json.loads((RES / 'Config/wearable_manifest.json').read_text())['poses'] if p['gender'] == 'main_m']
HANDS = json.loads((RES / 'Config/hand_table.json').read_text())['hands']
MATERIAL_COUNTS = {}


def grip_mask(p):
    hand = HANDS.get(f'main_m/{p["clip"]}/{p["frame"]}')
    yy, xx = np.indices((148, 148))
    return (abs(xx-hand['x']) <= 5) & (abs(yy-hand['y']) <= 5) if hand else np.zeros((148, 148), dtype=bool)


def face_opening(p, original):
    data = pose_data('main_m', p['clip'], p['frame'], original)
    return polygon_mask(original.size, [data['head_point'](x, y) for x, y in ((-10,-3),(10,-3),(10,9),(-10,9))])


def source(p, staff=False):
    clip = ('staff_' + p['clip']) if staff and p['clip'].startswith('attack') else p['clip']
    return Image.open(RES / f'Sprites/CharactersBaked/main_m/{clip}/frame_{p["frame"]:02}.png').convert('RGBA')


def head_mask(p, im):
    d = pose_data('main_m', p['clip'], p['frame'], im)
    # A jaw-shaped boundary excludes raised forearms crossing below the face.
    points = ((-18,-26),(18,-26),(18,-5),(10,8),(-10,8),(-18,-5))
    mask = polygon_mask(im.size, [d['head_point'](x, y) for x, y in points])
    yy, xx = np.indices((148, 148))
    cx, cy = d['head']
    ax, ay = d['head_point'](0, 1)
    local_y = (xx-cx)*(ax-cx) + (yy-cy)*(ay-cy)
    mask &= ~(d['skin'] & ~d['face'] & (local_y >= -3))
    mask &= ~grip_mask(p)
    return mask


def parts(p, im):
    a = np.asarray(im).copy()
    mask = head_mask(p, im)
    head, body = a.copy(), a.copy()
    head[~mask] = 0
    body[mask] = 0
    rebuilt = np.asarray(Image.alpha_composite(Image.fromarray(body), Image.fromarray(head)))
    assert np.array_equal(rebuilt, a), (p['key'], 'head/body split changed original pixels')
    return Image.fromarray(head), Image.fromarray(body)


def head_crop(p, im):
    head, _ = parts(p, im)
    _, cx, cy, *_ = next(a for a in ANCHORS['main_m'][p['clip']] if a[0] == p['frame'])
    rect = (cx - 32, cy - 40, cx + 32, cy + 24)
    return head.crop(rect), rect


def restored_body(p, generated, robe=False, polished=False):
    original = source(p, True)
    src, dst = np.asarray(original).copy(), np.asarray(generated.convert('RGBA')).copy()
    head = head_mask(p, original)
    # Restore whole original hands, including outline and existing grip anchors.
    d = pose_data('main_m', p['clip'], p['frame'], original)
    yy, xx = np.indices((148, 148))
    protect = d['feet'].copy()
    if polished:
        foot_y = np.where(protect)[0]
        assert foot_y.size, (p['key'], 'missing foot contact')
        protect &= yy >= foot_y.max() - 2
        foot_contact = protect.copy()
    protect |= grip_mask(p)
    # The other hand stays exposed below the chest, while upper-arm skin may be covered.
    _, cx, cy, nx, ny, wx, wy, *_ = next(a for a in ANCHORS['main_m'][p['clip']] if a[0] == p['frame'])
    if not d['lying']:
        protect |= d['skin'] & (yy >= wy - 2) & ~head
    dst[protect] = src[protect]
    if polished:
        foot_x = np.where(d['feet'])[1]
        below_feet = (yy >= original.getbbox()[3]) & (xx >= foot_x.min() - 3) & (xx <= foot_x.max() + 3)
        dst[below_feet] = 0
        assert np.array_equal(dst[foot_contact], src[foot_contact]), (p['key'], 'foot contact changed')
    dst[head] = 0
    # Clear latent transparent RGB so source/packed equality is deterministic.
    dst[dst[:, :, 3] == 0] = 0
    return Image.fromarray(dst)


def restored_head(p, generated):
    original = source(p, True)
    src, dst = np.asarray(original).copy(), np.asarray(generated.convert('RGBA')).copy()
    _, cx, cy, nx, ny, *_ = next(a for a in ANCHORS['main_m'][p['clip']] if a[0] == p['frame'])
    yy, xx = np.indices((148, 148))
    support = (xx-cx)**2 + (yy-cy)**2 <= 40**2
    if not (p['clip'] == 'dead' and p['frame'] >= 5):
        support &= yy <= ny + 2
    else:
        support &= (xx-nx)*(cx-nx) + (yy-ny)*(cy-ny) >= -15
    dst[~support] = 0
    data = pose_data('main_m', p['clip'], p['frame'], original)
    support &= ~((src[:, :, 3] > 0) & ~head_mask(p, original))
    support &= ~((data['arms'] | data['skin']) & ~head_mask(p, original))
    support &= ~grip_mask(p)
    dst[~support] = 0
    facial_opening = face_opening(p, original)
    face = data['skin'] & facial_opening & head_mask(p, original)
    padded = np.pad(face, 1)
    nearby = np.zeros_like(face)
    for dy in range(3):
        for dx in range(3):
            nearby |= padded[dy:dy+148, dx:dx+148]
    nearby &= facial_opening & (src[:, :, 3] > 0) & support
    dst[nearby] = src[nearby]
    dst[dst[:, :, 3] == 0] = 0
    return Image.fromarray(dst)


def pack(name, layers):
    prepared, empty = [], []
    for layer in layers:
        blank = layer.getbbox() is None
        empty.append(blank)
        if blank:
            layer = layer.copy()
            layer.putpixel((0, 0), (255, 255, 255, 255))
        prepared.append(layer)
    atlas, frames = pack_layers(prepared)
    for blank, frame in zip(empty, frames):
        if blank:
            y = atlas.height - frame['y'] - frame['height']
            atlas.paste((0, 0, 0, 0), (frame['x'], y, frame['x']+frame['width'], y+frame['height']))
            frame.update(width=1, height=1, cropX=0, cropY=0)
    path = f'Sprites/Wearables/{name}'
    atlas.save(RES / (path + '.png'))
    return {'path': path, 'frames': frames}


def defaults():
    originals = [source(p, True) for p in POSES]
    split = [parts(p, im) for p, im in zip(POSES, originals)]
    return {'poses': POSES, 'defaultHead': pack('m_head', [s[0] for s in split]),
            'defaultBody': pack('m_body', [s[1] for s in split]),
            'staffDefaultBody': pack('m_staffbody', [parts(p, source(p, True))[1] for p in POSES]),
            'swordTrail': pack('m_swordtrail', sword_trails()),
            'helmets': [None] * 37, 'armors': [None] * 37}


def sword_trails():
    layers = []
    for p in POSES:
        out = np.zeros((148, 148, 4), dtype=np.uint8)
        if p['clip'].startswith('attack'):
            a, b = np.asarray(source(p)), np.asarray(source(p, True))
            changed = np.any(a != b, axis=2)
            rgb = a[:, :, :3].astype(int)
            white = (rgb.min(axis=2) >= 100) & (rgb.max(axis=2)-rgb.min(axis=2) <= 80) & (rgb[:, :, 0]-rgb[:, :, 1] <= 10) & (a[:, :, 3] > 0)
            out[changed & white] = a[changed & white]
            assert not np.any(out[:, :, 3] & ~changed), 'non-trail pixels copied'
        layers.append(Image.fromarray(out))
    return layers


@cache
def robe_material_chroma():
    rgb = np.asarray(Image.open(REVIEW / 'male_robe_pose_00.png').convert('RGBA'))[:, :, :3].astype(float)
    # The AI robe is navy, unlike the cyan item palette; use one reference for every pose.
    selected = (rgb[:, :, 2] > rgb[:, :, 0]) & (rgb[:, :, 2] > rgb[:, :, 1]) & (rgb.max(axis=2) > 65) & (rgb.min(axis=2) < 190)
    assert selected.any(), 'robe reference material missing'
    return np.median(rgb[selected] / rgb[selected].sum(axis=1, keepdims=True), axis=0)


def material(im, tier, base_tier, p, head=False):
    a = np.asarray(im).copy()
    rgb = a[:, :, :3].astype(float)
    weights = np.array([.2126, .7152, .0722])
    value = rgb @ weights
    base = np.array([int(DESIGNS[base_tier][1][i:i+2], 16) for i in (1, 3, 5)])
    target = np.array([int(DESIGNS[tier][1][i:i+2], 16) for i in (1, 3, 5)])
    chroma = rgb / np.maximum(rgb.sum(axis=2, keepdims=True), 1)
    base_chroma = robe_material_chroma() if base_tier == 21 and not head else base / base.sum()
    # Tint only the family material; preserve contrasting trim and source skin.
    selected = np.linalg.norm(chroma - base_chroma, axis=2) <= .16
    if base_tier == 21 and not head:
        selected &= (chroma[:, :, 2] - chroma[:, :, 0] >= .06) & (chroma[:, :, 2] - chroma[:, :, 1] >= .04)
    material_shadow = (base_tier == 21 and not head) & (value >= 25) & ((rgb.max(axis=2) - rgb.min(axis=2)) >= 15)
    selected &= ((rgb.max(axis=2) > 65) | material_shadow) & (rgb.min(axis=2) < 190) & (a[:, :, 3] > 0)
    original = source(p, True)
    d = pose_data('main_m', p['clip'], p['frame'], original)
    skin = (rgb[:, :, 0] >= 185) & (rgb[:, :, 0] > rgb[:, :, 1] * 1.08) & (rgb[:, :, 1] > rgb[:, :, 2] * 1.12)
    protected_skin = skin & (d['arms'] | face_opening(p, original))
    selected &= ~protected_skin
    if tier < 2:
        selected &= ~d['arms']
    lightness = target @ weights
    amount = np.minimum(value / lightness, (255 - value) / (255 - lightness))
    tinted = value[:, :, None] + (target - lightness) * amount[:, :, None]
    a[selected, :3] = np.clip(np.rint(tinted[selected]), 0, 255).astype(np.uint8)
    assert np.array_equal(a[~selected], np.asarray(im)[~selected]), 'protected material pixels changed'
    if tier < 2:
        assert np.array_equal(a[d['arms']], np.asarray(im)[d['arms']]), 'cloth skin or outline changed'
    assert np.max(np.abs((a[:, :, :3] @ weights)[selected] - value[selected]), initial=0) <= 1, 'material contrast changed'
    opaque = a[:, :, 3] > 0
    regions = {'outline': (rgb.max(axis=2) - rgb.min(axis=2) <= 15) & (value < 45) & opaque,
               'highlights': (rgb.min(axis=2) >= 190) & opaque, 'skin': protected_skin & opaque}
    if base_tier == 21 and not head:
        regions['neutralLining'] = (rgb.max(axis=2) - rgb.min(axis=2) <= 15) & opaque
    count = MATERIAL_COUNTS.setdefault(f'{tier:02}/{"head" if head else "body"}', {'calls': 0, 'selectedPixels': 0, 'recoloredPixels': 0})
    count['calls'] += 1
    count['selectedPixels'] += int(selected.sum())
    count['recoloredPixels'] += int(np.count_nonzero(np.any(a[:, :, :3] != rgb, axis=2)))
    for name, region in regions.items():
        assert np.array_equal(a[region], np.asarray(im)[region]), (tier, p['key'], name + ' changed')
        count[name + 'ProtectedPixels'] = count.get(name + 'ProtectedPixels', 0) + int(region.sum())
    a[a[:, :, 3] == 0] = 0
    return Image.fromarray(a)


def build(partial=False):
    # shortcut: tiers share seven AI head and four AI body silhouettes; add native families only when unique item silhouettes are required.
    MATERIAL_COUNTS.clear()
    heads = (0, 2, 5, 15, 21, 24, 30)
    required = [REVIEW / f'male_{family}_pose_{i:02}.png'
                for family in ('plate', 'leather', 'robe', 'plate_polish', 'robe_polish') for i in range(35)]
    ready_heads = [tier for tier in heads if all((REVIEW / f'male_head{tier:02}_pose_{i:02}.png').exists() for i in range(35))]
    if not partial:
        required += [REVIEW / f'male_head{tier:02}_pose_{i:02}.png' for tier in heads for i in range(35)]
    missing = [str(p.name) for p in required if not p.exists()]
    if missing:
        raise ValueError(f'Incomplete AI frames: {len(missing)} missing; first {missing[:5]}')
    data = defaults()
    data['artProvenance'] = {'generator': 'PixelLab transfer_outfit native148',
                             'headFamilies': list(heads), 'bodyFamilies': ['leather', 'plate', 'plate_polish', 'robe_polish'],
                             'clothBodyTiers': [0, 1], 'clothBodySource': 'original clean male cloth outfit, recolored',
                             'leatherSleeveEditPoses': [13, 18],
                             'skyHoodColorCorrectionPoses': [18, 19, 20],
                             'polishedPlateTiers': list(range(15, 21)), 'polishedRobeTiers': list(range(21, 37)),
                             'polishedPlateFootProtection': 'original foot contact bottom three pixels; verified below-ground AI foot overflow removed',
                             'robeFootProtection': 'original feet retained; dead frame6 hem ends one pixel above original clothing, foot contact unchanged',
                             'generationLedger': 'male_polish_jobs.json',
                             'tierVariants': 'selective material chroma with original luminance, neutral lining, trim, outline and bounded skin preserved; shared family silhouettes'}
    for tier in range(37):
        headtier = 0 if tier < 2 else 2 if tier < 5 else 5 if tier < 15 else 15 if tier < 21 else 21 if tier < 24 else 24 if tier < 30 else 30
        family = 'leather' if tier < 5 else 'plate' if tier < 15 else 'plate_polish' if tier < 21 else 'robe_polish'
        polished_plate = family == 'plate_polish'
        h, a = [], []
        for i, p in enumerate(POSES):
            rawbody = source(p, True) if tier < 2 else Image.open(REVIEW / f'male_{family}_pose_{i:02}.png').convert('RGBA')
            sleeve = REVIEW / f'male_leather_sleevecandidate_pose_{i:02}.png'
            if family == 'leather' and tier >= 2 and sleeve.exists():
                rawbody = Image.open(sleeve).convert('RGBA')
            assert rawbody.size == (148, 148), (tier, i)
            if headtier in ready_heads:
                rawhead = Image.open(REVIEW / f'male_head{headtier:02}_pose_{i:02}.png').convert('RGBA')
                colorfix = REVIEW / f'male_head30_colorcandidate_pose_{i:02}.png'
                if headtier == 30 and colorfix.exists():
                    corrected = Image.open(colorfix).convert('RGBA')
                    assert np.array_equal(np.asarray(corrected)[:, :, 3], np.asarray(rawhead)[:, :, 3]), (tier, i, 'cap correction changed silhouette')
                    rawhead = corrected
                assert rawhead.size == (148, 148), (tier, i)
                h.append(restored_head(p, rawhead if tier == headtier else material(rawhead, tier, headtier, p, head=True)))
            bodytier = {'leather': 2, 'plate': 5, 'plate_polish': 15, 'robe_polish': 21}[family]
            material_tier = 5 if polished_plate else bodytier if tier >= 2 else 0
            a.append(restored_body(p, rawbody if tier == bodytier else material(rawbody, tier, material_tier, p), family == 'robe_polish', polished=polished_plate))
            assert not np.any(np.asarray(a[-1])[:, :, 3][head_mask(p, source(p, True))]), (tier, i, 'head in armour')
            assert np.array_equal(np.asarray(a[-1])[grip_mask(p)], np.asarray(source(p, True))[grip_mask(p)]), (tier, i, 'grip changed')
            if h:
                assert not np.any(np.asarray(h[-1])[:, :, 3][grip_mask(p)]), (tier, i, 'helmet covers grip')
                source_body = (np.asarray(source(p, True))[:, :, 3] > 0) & ~head_mask(p, source(p, True))
                assert not np.any(np.asarray(h[-1])[:, :, 3][source_body]), (tier, i, 'source arm in helmet')
                original = source(p, True)
                facial = pose_data('main_m', p['clip'], p['frame'], original)['skin'] & face_opening(p, original) & head_mask(p, original)
                composite = Image.alpha_composite(a[-1], h[-1])
                if polished_plate:
                    assert composite.getbbox()[3] == original.getbbox()[3], (tier, i, 'plate ground changed')
                mixed = np.asarray(composite)
                assert np.array_equal(mixed[facial], np.asarray(original)[facial]), (tier, i, 'face pixels changed')
            for layer in (a[-1], h[-1] if h else a[-1]):
                alpha = np.asarray(layer)[:, :, 3]
                assert not np.any(alpha[[0, -1], :]) and not np.any(alpha[:, [0, -1]]), (tier, i, 'canvas border touched')
        if h:
            data['helmets'][tier] = pack(f'm_h_{tier:02}', h)
        data['armors'][tier] = pack(f'm_a_{tier:02}', a)
    (RES / 'Config/male_wearable_manifest.json').write_text(json.dumps(data, indent=2) + '\n')
    checks = {'poses': 35, 'armorLayers': 37 * 35, 'helmetLayers': sum(x is not None for x in data['helmets']) * 35,
              'sourceBasis': 'existing staff-clean display sprites; original character PNGs are unchanged',
              'defaultSplitExactRGBA': True, 'atlasCropExactRGBA': True, 'armorGripExactRGBA': True,
              'helmetGripAlphaZero': True, 'helmetSourceBodyAlphaZero': True, 'mixedFaceSkinROIExactRGBA': True,
              'canvasBorderAlphaZero': True, 'readyHeadFamilies': ready_heads,
              'polishedPlateGroundExact': True, 'materialLuminanceMaxDelta': 1,
              'materialProtectionCounts': MATERIAL_COUNTS,
              'limits': 'Face assertion uses the original skin seed in the bounded facial opening; visual eyes and helmet openings require independent review.'}
    (REVIEW / 'male_bake_assertions.json').write_text(json.dumps(checks, indent=2) + '\n')
    print(f'PASS {sum(x is not None for x in data["helmets"])}+37 native148 atlases, 35 original poses, identity restored; shared family palettes')


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--partial', action='store_true', help='Publish only complete native AI families')
    build(parser.parse_args().partial)
