"""최종 FX 여백·접지·원본 재분할 일치 검사. 기존 PIL 의존성 사용."""
import json
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SPRITES = ROOT / 'unity_client/Assets/Resources/Sprites'
for key, folder, count in [('obj_firepillar', 'obj_firepillar/anim1', 9), ('fx_boss_warning', 'fx_boss_warning', 8)]:
    frames = sorted((SPRITES / 'FX' / folder).glob('frame_*.png'))
    assert len(frames) == count, (key, len(frames))
    if key == 'obj_firepillar':
        source = ROOT / 'field/anim/raw/obj_firepillar.png'
        cfg = json.loads(source.with_suffix('.json').read_text())['spritesheet']
        assert cfg['cell_size'] == {'width': 160, 'height': 160}
        sheet = Image.open(source).convert('RGBA')
        row = 160
    else:
        sheet = Image.open(ROOT / 'unity_review/stage3/team_v6/e_round/asset/strips_orig/fx_boss_warning.png').convert('RGBA')
        row = 0
    for i, path in enumerate(frames):
        im = Image.open(path).convert('RGBA')
        assert im.size == (160, 160), (path, im.size)
        box = im.getchannel('A').getbbox()
        assert box and min(box[0], box[1], 160 - box[2], 160 - box[3]) >= 8, (path, box)
        if key == 'obj_firepillar':
            assert box[3] == 144, (path, 'ground drift', box)
        assert im.tobytes() == sheet.crop((i * 160, row, (i + 1) * 160, row + 160)).tobytes(), (path, 'source slice mismatch')
    print(f'{key}: {count} frames, 160px, >=8px margins, exact source slices PASS')
