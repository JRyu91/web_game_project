"""Pack native female outfit frames using the existing paperdoll registration.

Only explicitly requested, complete tiers are published. Source files remain in
place; the builder writes production atlases and their manifest, no review files.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image
from bake_male_paperdoll import RES, face_opening, grip_mask, head_mask, identity_parts, material, pack, parts, pose_data, restored_body, source, sword_trails

POSES = [p for p in json.loads((RES / 'Config/wearable_manifest.json').read_text())['poses'] if p['gender'] == 'main_f']
MANIFEST = RES / 'Config/female_wearable_manifest.json'
NATIVE_TIERS = (0, 2, 5, 15, 21, 24, 30, *range(16, 21), *range(32, 37))


def family_tier(tier):
    if tier in (*range(16, 21), *range(32, 37)):
        return tier
    return 0 if tier < 2 else 2 if tier < 5 else 5 if tier < 15 else 15 if tier < 21 else 21 if tier < 24 else 24 if tier < 30 else 30


def native_ownership(p, head, body, keep_halo):
    h, b = np.asarray(head).copy(), np.asarray(body).copy()
    before = np.asarray(Image.alpha_composite(body, head))
    original = source(p, True)
    original_head = head_mask(p, original)
    head_seed = original_head if not keep_halo else original_head & (face_opening(p, original) | (np.asarray(original)[:, :, 3] > 0))
    d = pose_data(p['gender'], p['clip'], p['frame'], original)
    cx, cy = d['head']
    sx, sy = d['head_point'](1, 0)
    ax, ay = d['head_point'](0, 1)
    remaining = set(zip(*np.nonzero(h[:, :, 3] > 0)))
    while remaining:
        seed = remaining.pop()
        component, pending = [seed], [seed]
        while pending:
            y, x = pending.pop()
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    neighbor = (y + dy, x + dx)
                    if neighbor in remaining:
                        remaining.remove(neighbor); component.append(neighbor); pending.append(neighbor)
        yy, xx = np.asarray(component).T
        local_x = (xx-cx)*(sx-cx)+(yy-cy)*(sy-cy)
        central_halo = abs(local_x.mean()) <= 8 and np.max(np.abs(local_x)) <= 20 and np.all((xx-cx)*(ax-cx)+(yy-cy)*(ay-cy) < -8)
        if head_seed[yy, xx].any() or keep_halo and central_halo:
            continue
        # Detached native ornaments belong to the armour; keep every RGBA pixel.
        assert not np.any(b[yy, xx, 3]), (p['key'], 'wing destination occupied')
        b[yy, xx] = h[yy, xx]; h[yy, xx] = 0
    head, body = Image.fromarray(h), Image.fromarray(b)
    assert np.array_equal(np.asarray(Image.alpha_composite(body, head)), before), (p['key'], 'wing transfer changed outfit')
    return head, body


def registered_parts(p, generated, tier, head_generated=None, armor_generated=None):
    if armor_generated is not None:
        assert tier in (33, 34), 'Independent armour is supported only for native fan and halo tiers'
    head, body = identity_parts(p, armor_generated if tier == 33 and armor_generated is not None else generated, tier, head_generated)
    if armor_generated is not None:
        original = source(p, True)
        original_alpha = np.asarray(original)[:, :, 3] > 0
        original_head = head_mask(p, original)
        opening = face_opening(p, original) & original_head
        b = np.asarray(restored_body(p, armor_generated, polished=True)).copy()
        # Independent native armour ornaments survive in transparent space around the original head.
        ornaments = original_head & ~original_alpha & ~opening & ~grip_mask(p)
        b[ornaments] = np.asarray(armor_generated)[ornaments]
        b[original_head & original_alpha] = 0
        b[original.getbbox()[3]:] = 0
        floor = original.getbbox()[3] - 1
        b[floor, original_alpha[floor]] = np.asarray(original)[floor, original_alpha[floor]]
        b[b[:, :, 3] == 0] = 0
        body = Image.fromarray(b)
    if tier == 20:
        head, body = native_ownership(p, head, body, keep_halo=True)
    if tier not in range(16, 21):
        original = source(p, True)
        opening = face_opening(p, original) & head_mask(p, original)
        h, b = np.asarray(head).copy(), np.asarray(body).copy()
        # Restore the whole opening so AI eyes cannot remain beside restored skin.
        h[opening] = np.asarray(original)[opening]
        b[opening] = 0
        head, body = Image.fromarray(h), Image.fromarray(b)
        assert np.array_equal(np.asarray(Image.alpha_composite(body, head))[opening], np.asarray(original)[opening]), (tier, p['key'], 'face opening changed')
    if tier == 36:
        head, body = native_ownership(p, head, body, keep_halo=False)
    return head, body


def build(source_dir, tiers, families=False):
    tiers = sorted(set(tiers))
    if not tiers or any(t not in range(37) for t in tiers):
        raise ValueError('Select tiers 0..36 explicitly')
    if families:
        if set(tiers) != set(NATIVE_TIERS):
            raise ValueError('Family publication requires all seven families and ten identity tiers')
        tiers = list(range(37))
    assert len(POSES) == 35, 'Female source pose order requires 35 active poses'
    source_dir = Path(source_dir)
    male_paths = [RES / 'Config/male_wearable_manifest.json', *(RES / 'Sprites/Wearables').glob('m_*.png')]
    male_hashes = {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in male_paths}
    unchanged = {p: hashlib.sha256(p.read_bytes()).hexdigest()
                 for tier in range(37) if tier not in tiers for slot in ('h', 'a')
                 for p in [RES / f'Sprites/Wearables/f_{slot}_{tier:02}.png'] if p.exists()}
    prepared, hashes = {}, {}
    # Validate and restore every input before publishing any asset.
    for tier in tiers:
        heads, bodies = [], []
        base_tier = family_tier(tier) if families else tier
        for i, p in enumerate(POSES):
            path = source_dir / f'female_tier{base_tier:02}_identity_pose{i:02}.png'
            with Image.open(path) as raw:
                if raw.size != (p['width'], p['height']):
                    raise ValueError(f'{path.name}: expected native {p["width"]}x{p["height"]}, got {raw.size}')
                generated = raw.convert('RGBA')
            if tier != base_tier or tier == 15:
                # Female references follow the icon chroma; the male navy-robe override is not applicable.
                generated = material(generated, tier, base_tier, p, head=True, target_color='#d6dbe1' if tier == 15 else None)
            head_path = source_dir / f'female_tier{base_tier:02}_identity_head_pose{i:02}.png'
            head_generated = None
            if base_tier == 33:
                with Image.open(head_path) as raw:
                    if raw.size != (p['width'], p['height']):
                        raise ValueError(f'{head_path.name}: independent helmet must remain native size')
                    head_generated = raw.convert('RGBA')
                hashes[head_path.name] = hashlib.sha256(head_path.read_bytes()).hexdigest()
            armor_path = source_dir / f'female_tier{base_tier:02}_identity_armor_pose{i:02}.png'
            armor_generated = None
            if base_tier in (33, 34):
                with Image.open(armor_path) as raw:
                    if raw.size != (p['width'], p['height']):
                        raise ValueError(f'{armor_path.name}: independent armour must remain native size')
                    armor_generated = raw.convert('RGBA')
                hashes[armor_path.name] = hashlib.sha256(armor_path.read_bytes()).hexdigest()
            head, body = registered_parts(p, generated, tier, head_generated, armor_generated)
            original = source(p, True)
            excluded_head = head_mask(p, original)
            if armor_generated is not None:
                excluded_head &= np.asarray(original)[:, :, 3] > 0
                ornaments = head_mask(p, original) & ~(np.asarray(original)[:, :, 3] > 0) & ~face_opening(p, original) & ~grip_mask(p)
                assert np.array_equal(np.asarray(body)[ornaments], np.asarray(armor_generated)[ornaments]), (tier, p['key'], 'independent armour ornaments changed')
            assert not np.any(np.asarray(body)[:, :, 3][excluded_head]), (tier, p['key'], 'head in armor')
            assert not np.any(np.asarray(head)[:, :, 3][grip_mask(p)]), (tier, p['key'], 'helmet covers grip')
            assert np.array_equal(np.asarray(body)[grip_mask(p)], np.asarray(original)[grip_mask(p)]), (tier, p['key'], 'grip changed')
            composite = Image.alpha_composite(body, head)
            assert composite.getbbox()[3] == original.getbbox()[3], (tier, p['key'], 'ground changed')
            source_body = (np.asarray(original)[:, :, 3] > 0) & ~head_mask(p, original)
            if head_generated is not None:
                source_body = pose_data(p['gender'], p['clip'], p['frame'], original)['arms'] & ~head_mask(p, original)
            assert not np.any(np.asarray(head)[:, :, 3][source_body]), (tier, p['key'], 'source body in helmet')
            if tier not in range(16, 21):
                facial = face_opening(p, original) & head_mask(p, original)
                assert np.array_equal(np.asarray(composite)[facial], np.asarray(original)[facial]), (tier, p['key'], 'face changed')
            for layer in (head, body):
                alpha = np.asarray(layer)[:, :, 3]
                assert np.any(alpha), (tier, p['key'], 'empty slot')
                assert not np.any(alpha[[0, -1], :]) and not np.any(alpha[:, [0, -1]]), (tier, p['key'], 'canvas border touched')
            heads.append(head); bodies.append(body)
            hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
        prepared[tier] = heads, bodies
    data = json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {'poses': POSES, 'helmets': [None] * 37, 'armors': [None] * 37}
    assert data['poses'] == POSES and len(data['helmets']) == len(data['armors']) == 37, 'Existing female manifest schema changed'
    originals = [source(p, True) for p in POSES]
    split = [parts(p, im) for p, im in zip(POSES, originals)]
    data.update(defaultHead=pack('f_head', [s[0] for s in split]),
                defaultBody=pack('f_body', [s[1] for s in split]),
                staffDefaultBody=pack('f_staffbody', [s[1] for s in split]),
                swordTrail=pack('f_swordtrail', sword_trails(POSES)))
    for tier, (heads, bodies) in prepared.items():
        data['helmets'][tier] = pack(f'f_h_{tier:02}', heads)
        data['armors'][tier] = pack(f'f_a_{tier:02}', bodies)
    provenance = data.setdefault('artProvenance', {})
    provenance.update(generator='PixelLab transfer_outfit native144',
                      processing='Native outfit split; original hands and floor restored, complete original face-opening RGBA restored except closed warrior visors 16..20; no palette tint',
                      poseKeys=[p['key'] for p in POSES])
    provenance.setdefault('sourceSHA256', {}).update(hashes)
    provenance.update(independentHelmetTiers=[33], independentArmorTiers=[33, 34],
                      independentArmorOwnership='Tier33 native four-fan armour separate from feather hood; tier33/34 native armour ornaments retained in original transparent head space; original opaque head, face opening, grip and floor remain protected')
    if families:
        provenance['processing'] = provenance['processing'].replace('no palette tint', 'Native identities unchanged; family variants selectively recolored')
        provenance.update(nativeIdentityTiers=list(range(16, 21)) + list(range(32, 37)),
                          familyTiers=[0, 2, 5, 15, 21, 24, 30],
                          targetOverrides={'15': '#d6dbe1'},
                          tierVariants='Selective icon chroma, original luminance and protected skin/outline/trim; seven shared silhouettes outside native identity tiers')
    MANIFEST.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')
    for path, digest in {**male_hashes, **unchanged}.items():
        assert hashlib.sha256(path.read_bytes()).hexdigest() == digest, (path.name, 'unselected asset changed')
    print(f'PASS female native144: {len(tiers)} complete tiers x 35 poses; exact split, grip, ground, borders; male assets unchanged')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--source-dir', type=Path, required=True)
    parser.add_argument('--tiers', type=int, nargs='+', required=True)
    parser.add_argument('--families', action='store_true', help='Publish all 37 palettes from the seven native families plus ten native identities')
    args = parser.parse_args()
    build(args.source_dir, args.tiers, args.families)
