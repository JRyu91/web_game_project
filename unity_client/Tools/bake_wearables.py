"""Draw native-size wearable pixel geometry onto every active player pose.

Icons supply material colours and motifs; their pixels are never pasted or scaled.
The source body stays unchanged. Pose masks keep the face, arms and feet visible.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / 'Assets/Resources'
OUT = RES / 'Sprites/Wearables'
REVIEW = ROOT.parent / 'unity_review/stage3/team_v6/f_round/code/final'
CELL, COLS = 148, 5
CLIPS = ('idle', 'walk', 'attack', 'attack1', 'attack2', 'hurt', 'dead')

# frame, head x/y, neck x/y, waist x/y, head roll (degrees). These are
# registered to the existing body pixels, including the three lying poses.
ANCHORS = {
 'main_m': {
  'idle': [(0,74,46,74,57,76,89,0),(1,74,45,74,56,76,88,0),(2,74,46,74,57,76,89,0),(3,74,46,74,57,76,89,0)],
  'walk': [(0,76,49,75,60,76,89,0),(1,78,47,77,58,77,88,0),(2,77,45,77,56,77,86,0),(3,76,46,75,57,76,87,0),(4,76,49,75,60,76,89,0),(5,76,49,75,59,76,89,0),(6,77,45,76,57,77,87,0),(7,77,46,76,57,77,87,0)],
  'attack': [(3,71,47,70,57,70,87,0),(5,70,49,68,64,72,90,-5),(7,80,58,76,65,70,87,8),(8,80,59,76,65,70,88,8),(10,79,58,75,65,69,87,8)],
  'attack1': [(4,68,49,67,60,68,87,0),(6,76,49,68,64,69,87,-6),(7,85,59,80,65,69,88,12),(8,87,60,82,67,72,89,12),(10,83,57,78,64,66,87,8)],
  'attack2': [(3,73,48,73,59,74,87,0),(6,66,52,68,61,68,89,-8),(8,64,54,63,64,59,87,-10),(9,79,57,75,64,67,87,8),(10,80,57,76,64,68,87,8)],
  'hurt': [(0,74,46,73,59,74,90,0),(2,64,52,69,62,76,89,-18),(5,70,48,71,61,74,90,-8)],
  'dead': [(0,73,46,72,58,74,90,0),(3,85,53,80,64,79,90,12),(5,99,104,87,103,65,106,-55),(6,101,113,91,113,67,115,-72),(10,101,115,91,114,67,116,-78)],
 },
 'main_f': {
  'idle': [(0,74,44,74,55,75,85,0),(1,72,43,72,54,74,85,0),(2,73,44,73,55,74,86,0),(3,72,43,72,54,74,85,0)],
  'walk': [(0,79,46,75,57,75,84,0),(1,78,48,75,59,75,86,0),(2,79,45,75,56,75,83,0),(3,79,45,75,57,75,84,0),(4,79,48,75,60,75,87,0),(5,79,49,75,61,75,88,0),(6,79,45,75,56,75,83,0),(7,79,43,75,55,75,82,0)],
  'attack': [(4,65,50,65,63,65,85,-4),(6,65,50,65,63,65,85,-4),(7,83,50,76,59,71,84,8),(8,87,58,79,66,68,87,12),(10,87,55,80,64,68,87,10)],
  'attack1': [(4,72,48,72,59,73,86,0),(6,72,52,72,63,73,87,-6),(7,79,54,72,63,63,85,10),(8,83,55,74,64,64,85,10),(10,84,54,77,63,65,86,10)],
  'attack2': [(4,66,52,64,60,64,86,-5),(6,61,48,61,59,63,84,-8),(7,73,52,68,61,65,84,8),(9,73,54,68,63,65,85,8),(10,73,54,68,63,65,85,8)],
  'hurt': [(0,73,44,72,55,73,85,0),(2,67,45,70,56,72,84,-12),(4,64,45,69,56,71,83,-15)],
  'dead': [(0,72,44,72,56,73,85,0),(3,79,47,73,59,69,85,12),(5,98,95,85,95,64,99,-28),(6,103,107,91,110,68,113,-66),(10,104,112,92,116,69,117,-78)],
 },
}

# A design per array entry. Different materials also have different outlines.
DESIGNS = [
    ('rag', '#79634e', '#b69b75'), ('cloth', '#747c87', '#b4bcc4'),
    ('leather', '#714532', '#b58453'), ('flap', '#50392d', '#aa7450'),
    ('rivet', '#713d27', '#c69961'), ('silver', '#788b9b', '#e1e9ed'),
    ('horn', '#67717b', '#d5d7cc'), ('mithril', '#39758d', '#b3e7e8'),
    ('scale', '#427365', '#a5c6a3'), ('fire', '#872d22', '#ed9b35'),
    ('thunder', '#3d536f', '#8cbbe6'), ('frost', '#6297af', '#d6f5f5'),
    ('death', '#353641', '#c2baa5'), ('ward', '#553844', '#d2a870'),
    ('raijin', '#35456c', '#d8d871'), ('radiant', '#a28239', '#f6e8a1'),
    ('ruin', '#3d292b', '#d16b49'), ('extinction', '#242736', '#ae6ed3'),
    ('end', '#322330', '#e7a86c'), ('origin', '#697771', '#f0e8d4'),
    ('omega', '#242229', '#d7b950'), ('crystal', '#5a8599', '#b7dfec'),
    ('amethyst', '#69557d', '#cab1e5'), ('moon', '#7d8596', '#e9eef4'),
    ('ice', '#5a929b', '#c4f2ef'), ('magefire', '#8b3829', '#ffc16b'),
    ('storm', '#55586e', '#b0b8e1'), ('snow', '#7898ae', '#e5f1f7'),
    ('abyss', '#2b2f4c', '#7d72a8'), ('dream', '#81607c', '#e2b9d0'),
    ('sky', '#467d9c', '#c4e7f5'), ('star', '#464569', '#e4d090'),
    ('genesis', '#cec5a1', '#fff4d0'), ('void', '#302538', '#c485d9'),
    ('creation', '#a7bfc1', '#fff9e9'), ('dragon', '#654942', '#d6b087'),
    ('reddragon', '#732d30', '#edaa65'),
]


def rgba(c):
    return tuple(int(c[i:i + 2], 16) for i in (1, 3, 5)) + (255,)


def tone(c, factor):
    return tuple(max(0, min(255, round(v * factor))) for v in c[:3]) + (255,)


def mix(a, b, amount):
    return tuple(round(x * (1 - amount) + y * amount) for x, y in zip(a[:3], b[:3])) + (255,)


def mask(size, points):
    im = Image.new('L', size)
    ImageDraw.Draw(im).polygon([(round(x), round(y)) for x, y in points], fill=255)
    return np.asarray(im) > 0


def pose_data(gender, clip, frame, source):
    a = np.asarray(source)
    rgb = a[:, :, :3].astype(int)
    skin = (rgb[:, :, 0] > 185) & (rgb[:, :, 1] > 85) & (rgb[:, :, 2] > 45)
    skin &= (rgb[:, :, 0] > rgb[:, :, 1] * 1.08) & (rgb[:, :, 0] > rgb[:, :, 2] * 1.12) & (a[:, :, 3] > 0)
    lying = clip == 'dead' and frame >= 5
    row = next(p for p in ANCHORS[gender][clip] if p[0] == frame)
    _, cx, cy, nx, ny, wx, wy, roll = row
    yy, xx = np.indices(a.shape[:2])
    r, g, b = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    hair = ((r > 45) & (r < 185) & (r > g * 1.15) & (g > b * 1.4)) if gender == 'main_m' else ((rgb.mean(axis=2) < 110) & (b >= r * .95))
    hair &= (a[:, :, 3] > 0) & ~skin & (abs(xx - cx) <= 17) & (yy >= cy - 22) & (yy <= cy + 3)
    assert hair.sum() > 10, (gender, clip, frame, 'skull cluster missing')
    skull = (float(xx[hair].mean()), float(yy[hair].mean()))
    # The skull-to-face vector is the head's axis. A lying torso is not proof
    # that its head turned 90 degrees; the source death heads retain expression.
    roll = math.degrees(math.atan2(skull[0] - cx, cy - skull[1]))
    if not lying:
        roll = max(-20, min(20, roll))  # side-view perspective moves skull behind face
    th = math.radians(roll)
    def head_point(x, y):
        return cx + x * math.cos(th) - y * math.sin(th), cy + x * math.sin(th) + y * math.cos(th)
    face = mask(source.size, [head_point(x, y) for x, y in ((-10,-7),(10,-7),(10,9),(-10,9))])
    neck, waist = (nx, ny), (wx, wy)
    bbox = source.getbbox()
    if lying:
        feet = np.zeros_like(skin)
        feet[:, :bbox[0] + 17] = a[:, :bbox[0] + 17, 3] > 0
    else:
        feet = np.zeros_like(skin)
        feet[bbox[3] - 15:] = a[bbox[3] - 15:, :, 3] > 0
    # Preserve all exposed skin and a one-pixel contour around it. The face has
    # its own complete opening so eye/hair pixels keep the original expression.
    padded = np.pad(skin, 1)
    skin_edge = np.zeros_like(skin)
    for dy in range(3):
        for dx in range(3):
            skin_edge |= padded[dy:dy + skin.shape[0], dx:dx + skin.shape[1]]
    arms = skin_edge & ~face
    hand = json.loads((RES / 'Config/hand_table.json').read_text())['hands'].get(f'{gender}/{clip}/{frame}')
    if hand:
        h = Image.new('L', source.size)
        ImageDraw.Draw(h).ellipse((hand['x'] - 6, hand['y'] - 6, hand['x'] + 6, hand['y'] + 6), fill=255)
        arms |= (np.asarray(h) > 0) & (a[:, :, 3] > 0)
    return dict(head=(cx, cy), neck=neck, waist=waist, face=face, head_point=head_point,
                arms=arms, feet=feet, lying=lying, skin=skin, roll=roll, skull=skull, opaque=a[:,:,3]>0)


class Canvas:
    def __init__(self, size, transform, base, light):
        self.im = Image.new('RGBA', size)
        self.d = ImageDraw.Draw(self.im)
        self.transform = transform
        self.base, self.light = base, light
        self.dark, self.edge = tone(base, .55), (20, 14, 24, 255)

    def points(self, points):
        return [tuple(round(v) for v in self.transform(x, y)) for x, y in points]

    def poly(self, points, fill=None, edge=True):
        points = self.points(points)
        self.d.polygon(points, fill=fill or self.base)
        if edge:
            self.d.line(points + [points[0]], fill=self.edge, width=1)

    def line(self, points, color=None, width=1):
        self.d.line(self.points(points), fill=color or self.light, width=width)


def feathers(c, sign, count=4):
    for k in range(count):
        x, y = 18 + k * 3, -22 - k * 2
        c.poly([(sign*9,-11),(sign*(x-4),y+5),(sign*x,y),(sign*(x+1),y+5),
                (sign*(x-3),y+9),(sign*11,-7)], mix(c.base,c.light,.55))
        c.line([(sign*11,-11),(sign*(x-1),y+3)], c.light)


def curved_horns(c, length=0):
    for s in (-1,1):
        c.poly([(s*8,-15),(s*14,-18),(s*19,-23),(s*21,-29),(s*18,-34-length),
                (s*16,-28),(s*13,-24),(s*7,-21)], mix(c.base,c.light,.8))
        c.poly([(s*9,-19),(s*15,-22),(s*18,-28),(s*17,-24),(s*13,-20)], tone(c.light,.66), False)
        c.line([(s*18,-30),(s*17,-26),(s*12,-22)], c.light)


def helmet(size, pose, tier):
    name, b, l = DESIGNS[tier]
    c = Canvas(size, pose['head_point'], rgba(b), rgba(l))
    # Cap covers the original skull; the original face remains in an opening.
    cap_h = 19 + (tier % 3)
    cap_w = 13 + (tier % 2)
    if tier == 0:
        c.poly([(-12, -9), (9, -10), (12, -6), (-13, -4)])
        c.line([(-10, -8), (8, -8)], c.light)
    else:
        c.poly([(-cap_w, 2), (-cap_w - 1, -9), (-9, -cap_h), (0, -cap_h - 3),
                (10, -cap_h + 2), (cap_w, -10), (cap_w, 3), (9, 5), (-9, 5)])
        c.poly([(-cap_w + 1, -9), (-8, -cap_h + 1), (-2, -cap_h), (-3, -6), (-10, 3)], c.dark, False)
        c.line([(-8, -cap_h + 1), (-1, -cap_h - 1), (8, -cap_h + 3), (11, -11)])
        c.line([(-11, -5), (11, -5)], c.light)
    if tier == 0:
        pass
    elif tier == 1:
        c.poly([(-15, -12), (-13, 8), (-9, 11), (-10, -5)], c.dark)
    elif tier == 2:
        c.line([(-7, -17), (-3, -15), (-1, -7)], c.light)
    elif tier == 3:
        c.poly([(-14, -4), (-9, 9), (-13, 12), (-17, 4)], c.dark)
    elif tier == 4:
        for x in (-9, -4, 2, 7):
            c.line([(x, -6), (x, -5)], c.light)
        c.poly([(-2, -24), (3, -24), (5, -20), (-4, -20)], c.light)
    elif tier == 6:
        for s in (-1, 1):
            c.poly([(s*8,-16),(s*14,-18),(s*18,-24),(s*18,-31),(s*15,-36),
                    (s*15,-28),(s*11,-23),(s*6,-21)], c.light)
            c.line([(s*15,-31),(s*14,-26),(s*10,-22)], tone(c.light,.72))
        c.poly([(-4,-12),(0,-16),(4,-12),(0,-8)], c.light)
    elif tier == 8:
        c.poly([(-10,-15),(-20,-19),(-16,-12),(-12,-8)], c.light)
        c.poly([(10,-15),(18,-21),(17,-12),(12,-8)], c.light)
        for y in (-18,-14,-10):
            for x in (-6,0,6):
                c.poly([(x-3,y),(x,y+3),(x+3,y),(x,y-2)], tone(c.light,.8))
        c.poly([(-3,-21),(0,-28),(3,-21)], c.light)
    elif tier == 9:
        c.poly([(-11,-17),(-13,-24),(-9,-28),(-8,-36),(-3,-30),(0,-39),
                (4,-31),(7,-34),(8,-25),(13,-29),(11,-17)], rgba('#e06725'))
        c.poly([(-7,-18),(-6,-27),(-2,-23),(0,-33),(3,-25),(7,-28),(6,-18)], c.light)
        c.poly([(-2,-18),(0,-24),(3,-18)], rgba('#fff0ab'))
    elif tier == 10:
        for s in (-1,1):
            c.poly([(s*8,-14),(s*19,-23),(s*14,-24),(s*21,-34),(s*9,-23),
                    (s*13,-22),(s*5,-14)], c.light)
        c.poly([(-3,-20),(2,-30),(0,-22),(5,-22),(0,-14)], c.light)
    elif tier == 11:
        for x,h in ((-8,11),(0,19),(8,14)):
            c.poly([(x-3,-18),(x-3,-18-h),(x,-22-h),(x+3,-18-h),(x+3,-18)], c.light)
            c.line([(x,-20-h),(x,-18)], rgba('#91bfcf'))
        c.poly([(-13,-11),(-19,-16),(-15,-20),(-9,-15)], c.light)
    elif tier == 12:
        for s in (-1,1):
            c.poly([(s*8,-16),(s*15,-20),(s*16,-28),(s*9,-32),(s*8,-27),(s*12,-25),(s*12,-21)], c.light)
            c.poly([(s*11,-3),(s*15,5),(s*12,12),(s*5,13),(s*7,8)], c.dark)
            c.line([(s*12,2),(s*11,8),(s*6,10)], c.light)
        c.poly([(-5,-13),(0,-19),(5,-13),(3,-9),(-3,-9)], c.light)
        c.line([(-2,-13),(-2,-11)], c.edge)
        c.line([(2,-13),(2,-11)], c.edge)
    elif tier == 13:
        for s in (-1,1):
            c.poly([(s*9,-14),(s*19,-19),(s*23,-14),(s*21,-6),(s*12,-3)], c.base)
            c.poly([(s*11,-14),(s*18,-17),(s*20,-14),(s*17,-9),(s*12,-7)], mix(c.base,c.light,.35), False)
            c.line([(s*14,-15),(s*19,-14),(s*17,-9)], c.light)
        c.poly([(-3,-21),(0,-30),(3,-21),(0,-16)], c.light)
    elif tier == 14:
        for s in (-1,1):
            c.poly([(s*7,-16),(s*21,-27),(s*16,-27),(s*24,-34),(s*10,-23),(s*14,-23),(s*4,-14)], c.light)
            c.line([(s*9,-18),(s*15,-23)], mix(c.base,c.light,.4))
        c.poly([(-3,-19),(1,-31),(4,-26),(1,-23),(5,-21),(0,-15)], c.light)
    elif tier == 15:
        feathers(c,-1,4); feathers(c,1,4)
        c.poly([(-4,-20),(-3,-29),(0,-34),(3,-29),(4,-20)], c.light)
        c.poly([(-2,-22),(0,-28),(2,-22),(0,-18)], rgba('#f1f6ee'))
    elif tier == 16:
        curved_horns(c)
        c.poly([(-4,-18),(-7,-24),(-2,-30),(2,-25),(6,-28),(5,-18)], tone(c.base,.8))
        c.line([(-2,-25),(1,-23),(-1,-19),(3,-17)], c.light)
        c.line([(-9,-11),(-5,-9),(-7,-6)], c.light)
    elif tier == 17:
        for s in (-1,1):
            c.poly([(s*10,-13),(s*22,-22),(s*23,-15),(s*19,-10),(s*17,-2),(s*11,2)], c.dark)
            c.line([(s*12,-10),(s*18,-14),(s*20,-18)], c.light)
        c.poly([(-3,-20),(0,-31),(4,-24),(2,-18)], mix(c.base,c.light,.55))
        c.poly([(-5,-11),(0,-16),(5,-11),(0,-7)], c.light)
        c.poly([(-2,-11),(0,-13),(2,-11),(0,-9)], c.dark)
    elif tier == 18:
        curved_horns(c,1)
        for s in (-1,1):
            c.poly([(s*11,-4),(s*16,3),(s*13,10),(s*6,13),(s*5,9),(s*11,7)], mix(c.base,c.light,.3))
            c.line([(s*13,2),(s*12,7),(s*7,10)], c.light)
        c.poly([(-6,-15),(-2,-22),(3,-21),(7,-15),(4,-10),(-3,-10)], c.light)
        c.line([(-3,-14),(-1,-14)], c.dark)
        c.line([(2,-14),(4,-14)], c.dark)
    elif tier == 19:
        feathers(c,-1,3); feathers(c,1,3)
        for x in (-7,0,7):
            c.poly([(x-3,-19),(x-2,-27-abs(x)//2),(x,-30-abs(x)//2),(x+2,-27-abs(x)//2),(x+3,-19)], c.light)
            c.line([(x,-26),(x,-21)], tone(c.light,.65))
        c.poly([(-4,-12),(0,-17),(4,-12),(0,-8)], rgba('#d5e6de'))
    elif tier == 20:
        feathers(c,-1,5); feathers(c,1,5)
        curved_horns(c)
        c.poly([(-5,-20),(-3,-31),(0,-36),(3,-31),(5,-20)], c.light)
        c.poly([(-4,-15),(0,-20),(5,-15),(3,-10),(-3,-10)], mix(c.base,c.light,.55))
        c.line([(-2,-16),(0,-17),(3,-15)], rgba('#f5e1a2'))
        for s in (-1,1):
            c.poly([(s*12,-3),(s*16,4),(s*12,10),(s*8,10),(s*9,5)], c.base)
            c.line([(s*12,1),(s*12,7)], c.light)
    elif tier < 21:
        rank = tier - 5
        # Distinct high-tier outline: horns, wings, spikes and increasing crest.
        horn = 6 + rank // 2
        if tier in (6, 8, 10, 12, 14, 16, 18, 20):
            c.poly([(-9, -16), (-15 - rank // 3, -22), (-14 - rank // 3, -24 - horn), (-7, -20)], c.light)
            c.poly([(8, -15), (15 + rank // 4, -20), (17 + rank // 4, -24 - horn), (5, -21)], c.light)
        else:
            wing = 5 + rank // 2
            c.poly([(-11, -12), (-20 - wing, -24), (-17 - wing, -14), (-13, -8)], c.light)
            c.poly([(11, -12), (18 + wing, -23), (17 + wing, -12), (12, -7)], c.light)
        crest = 3 + rank // 2
        c.poly([(-4, -20), (-3, -25 - crest), (0, -23 - crest), (3, -27 - crest), (5, -20)], c.light)
        c.poly([(-4, -13), (0, -17), (5, -12), (0, -8)], tone(c.light, .8))
        c.line([(-7, -3), (-10, 6)], c.light)
        c.line([(10, -2), (11, 5)], c.light)
        if rank >= 10:
            for s in (-1, 1):
                for k in range(3):
                    c.line([(s * (13 + k * 2), -16 - k), (s * (18 + k * 3), -22 - k * 2)], c.light)
    else:
        rank = tier - 21
        if tier in (21, 22, 24, 27, 31, 32, 34):
            count = 3 + rank % 3
            for k in range(count):
                x = -10 + k * 20 / max(1, count - 1)
                h = 7 + ((k * 3 + rank) % 9)
                c.poly([(x - 3, -18), (x, -23 - h), (x + 3, -18)], c.light)
        elif tier in (25, 26, 29, 30):
            tip = -30 - rank % 7
            c.poly([(-14, -11), (-7, -25), (5, tip), (9 + rank % 5, -24), (13, -11)], c.base)
            brim = 18 + rank % 5
            c.poly([(-brim, -9), (-10, -13), (13, -12), (brim, -8), (12, -5), (-12, -5)], c.dark)
            c.line([(-12, -12), (11, -12)], c.light)
        else:
            c.poly([(-16, -12), (-19 - rank % 4, -19), (-10, -25), (0, -24), (11, -21), (15, -11)], c.dark)
            c.line([(-10, -23), (-4, -24), (5, -22)], c.light)
            c.poly([(-2, -18), (1, -23), (4, -18), (1, -14)], c.light)
        if rank >= 7:
            c.poly([(-13, -15), (-19, -22), (-21 - rank % 4, -30), (-12, -22)], c.light)
        if rank >= 11:
            c.poly([(10, -16), (18, -24), (20 + rank % 4, -32), (11, -24)], c.light)
        c.line([(-12, -3), (-11, 6)], c.light)
        if tier == 23:
            c.poly([(-10,-21),(-15,-27),(-12,-33),(-8,-35),(-10,-30),(-6,-26),(-4,-23)], c.light)
            c.line([(-11,-28),(-10,-31)], tone(c.light,.7))
        elif tier == 25:
            c.poly([(-6,-25),(-7,-32),(-3,-37),(0,-33),(2,-40),(5,-33),(5,-25)], rgba('#dc6428'))
            c.poly([(-2,-26),(0,-34),(3,-27)], c.light)
        elif tier == 26:
            c.poly([(7,-23),(18,-28),(13,-28),(22,-33),(10,-26),(14,-25)], c.light)
            c.line([(-8,-16),(0,-18),(7,-16)], c.light)
        elif tier == 28:
            c.poly([(-16,-9),(-19,3),(-15,12),(-10,8),(-11,-3)], c.dark)
            c.line([(-15,-5),(-16,2),(-13,7)], c.light)
        elif tier == 29:
            c.poly([(6,-27),(14,-33),(14,-29),(9,-27),(12,-24),(6,-22)], c.light)
            c.line([(-9,-8),(-5,-5)], c.light)
        elif tier == 30:
            feathers(c,-1,3)
        elif tier == 31:
            c.line([(-9,-24),(-4,-29),(2,-26),(8,-31)], c.light)
            for x,y in ((-9,-24),(-4,-29),(2,-26),(8,-31)):
                c.poly([(x-1,y),(x,y-2),(x+1,y),(x,y+2)], c.light, False)
        elif tier in (35,36):
            curved_horns(c)
            for y in (-19,-14,-9):
                for x in (-5,0,5):
                    c.poly([(x-3,y),(x,y+3),(x+3,y),(x,y-2)], mix(c.base,c.light,.5))
    if tier >= 5:
        c.poly([(-11,-12),(-7,-18),(-2,-20),(-2,-7),(-9,-5)], mix(c.base,c.light,.15), False)
        c.poly([(3,-17),(10,-13),(12,-7),(8,-5),(1,-7)], tone(c.base,.78), False)
        c.line([(-8,-16),(-4,-18),(-2,-14)], mix(c.base,c.light,.65))
        c.line([(-10,-7),(-5,-6)], c.light)
    a = np.array(c.im)
    a[:, :, 3][pose['face'] | pose['arms'] | pose['feet']] = 0
    a[a[:, :, 3] == 0] = 0
    return Image.fromarray(a)


def armor(size, pose, tier):
    name, b, l = DESIGNS[tier]
    nx, ny = pose['neck']; wx, wy = pose['waist']
    dx, dy = wx - nx, wy - ny
    length = math.hypot(dx, dy)
    ax, ay = dx / length, dy / length
    perspective = .7 if pose['lying'] else 1
    across = (ay * perspective, -ax * perspective)
    def transform(x, y):
        return nx + across[0] * x + ax * y * length / 30, ny + across[1] * x + ay * y * length / 30
    c = Canvas(size, transform, rgba(b), rgba(l))
    if tier >= 15:
        cape = Canvas(size, transform, tone(c.base,.6), tone(c.light,.6))
        extent = 23 if tier < 21 else 26 + (tier-21) % 4
        cape.poly([(-11,3),(-extent,13),(-extent+4,36),(-17,43),(-10,29)], cape.base)
        cape.line([(-extent+3,13),(-extent+7,32),(-16,37)], cape.light)
        ca=np.array(cape.im); ca[:,:,3][pose['opaque']]=0; ca[ca[:,:,3]==0]=0
        c.im.alpha_composite(Image.fromarray(ca))
    width = 11 + tier % 3
    if tier <= 4:
        c.poly([(-width, 1), (-4, -2), (2, 2), (7, -1), (width, 4), (width - 1, 27),
                (5 + tier % 2, 32 + tier), (-8, 31), (-width, 6)])
        c.poly([(-width + 1, 6), (-5, 7), (-4, 27), (-7, 30), (-width + 1, 25)], c.dark, False)
        c.line([(-6, 4), (-3, 24)], c.light)
        c.line([(-width, 25), (width - 1, 25)], c.dark, 2)
        if tier in (2, 3, 4):
            c.line([(-7, 3), (7, 23)], c.light, 2)
            c.poly([(0, 14), (4, 14), (4, 18), (0, 18)], c.dark)
        if tier == 0:
            c.poly([(-8, 26), (-5, 35), (-1, 29), (3, 33), (7, 29), (9, 26)], c.dark)
        if tier == 1:
            for y in (9, 15, 21):
                c.line([(-2, y), (2, y + 1)], c.light)
        if tier == 4:
            for y in (8, 14, 20):
                c.line([(-8, y), (-7, y)], c.light)
                c.line([(7, y), (8, y)], c.light)
    elif tier < 21:
        rank = tier - 5
        shoulder = 14 + rank // 2
        if rank >= 10:
            width = 13 + (rank - 10) // 2
        c.poly([(-width, 3), (-6, -4), (-1, 2), (5, -3), (width, 3), (width + 1, 12),
                (width, 24),(8,31),(1,33),(-8,31),(-width,24),(-width-1,12)])
        c.poly([(-width+1,8),(-3,6),(-3,19),(-7,25),(-width+1,22)], mix(c.base,c.light,.2), False)
        c.poly([(-3,6),(2,4),(8,8),(6,18),(2,24),(-5,23)], mix(c.base,c.light,.32), False)
        c.poly([(8,7),(width-1,5),(width,21),(4,26),(6,17)], tone(c.base,.65), False)
        c.line([(-width+1,9),(-width+2,20),(-8,26)], mix(c.base,c.light,.7))
        c.line([(-3,7),(1,6),(6,9)], c.light)
        c.line([(-2,11),(-3,17)], mix(c.base,c.light,.52))
        c.line([(4,21),(8,18)], c.dark)
        c.poly([(-7,-2),(-3,-5),(1,-2),(6,-4),(8,-1),(5,4),(-5,4)], mix(c.base,c.light,.22))
        c.line([(-5,-2),(-2,0),(4,-1)], c.light)
        for s in (-1, 1):
            c.poly([(s*7,1),(s*11,-3),(s*(shoulder-2),-4),(s*(shoulder+1),-1),
                    (s*(shoulder+3),4),(s*shoulder,8),(s*12,10),(s*8,7)], c.base)
            c.poly([(s*9,1),(s*12,-2),(s*(shoulder-2),-2),(s*shoulder,1),(s*(shoulder-2),4),(s*11,5)], mix(c.base,c.light,.38), False)
            c.line([(s*11,-2),(s*(shoulder-2),-2),(s*shoulder,1)], c.light)
            c.line([(s*12,7),(s*(shoulder-1),5)], c.dark)
            if rank >= 7:
                c.poly([(s*11,7),(s*(shoulder+1),6),(s*(shoulder+4),11),(s*(shoulder+2),15),(s*12,14)], tone(c.base,.8))
                c.line([(s*13,8),(s*shoulder,8),(s*(shoulder+2),11)], mix(c.base,c.light,.7))
            if tier in (6,9,11,16,18):
                c.poly([(s*shoulder,0),(s*(shoulder+3),-6),(s*(shoulder+2),4)], c.light)
            if tier == 20:
                c.poly([(s*(shoulder+1),7),(s*(shoulder+6),8),(s*(shoulder+5),14),
                        (s*(shoulder+2),17),(s*(shoulder+1),12)], mix(c.base,c.light,.22))
                c.line([(s*(shoulder+3),9),(s*(shoulder+4),12)], c.light)
        skirt = 5 + rank // 2
        for y in (24,28,32):
            w=width+(y-24)//4
            c.poly([(-w,y),(-w,y+3),(-3,y+5),(4,y+4),(w,y+2),(w,y-1),(1,y+1)], mix(c.base,c.light,.12))
            c.line([(-w+2,y),(-3,y+2),(3,y+1),(w-2,y-1)], mix(c.base,c.light,.58))
        c.poly([(-10,31),(-13,34+skirt),(-4,33+skirt),(-1,32)], tone(c.base,.75))
        c.poly([(1,32),(4,34+skirt),(13,33+skirt),(10,31)], mix(c.base,c.light,.14))
        c.line([(-11,33),(-6,34+skirt)], mix(c.base,c.light,.5))
        c.line([(5,33),(10,32+skirt)], mix(c.base,c.light,.65))
        if tier == 8:
            for y in (10,15,20):
                for x in (-5,0,5):
                    c.poly([(x-3,y),(x,y+3),(x+3,y),(x,y-1)], mix(c.base,c.light,.65))
        elif tier == 9:
            c.poly([(-4,19),(-6,13),(-2,15),(0,8),(3,14),(6,12),(4,21)], c.light)
            c.poly([(-1,19),(0,13),(2,18)], rgba('#f0d59a'))
        elif tier in (10,14):
            c.poly([(1,8),(-5,16),(-1,16),(-4,23),(5,14),(1,14)], c.light)
        elif tier == 11:
            c.poly([(-4,11),(0,7),(4,11),(4,18),(0,23),(-4,18)], c.light)
            c.poly([(0,8),(3,12),(3,17),(0,21)], tone(c.light,.72), False)
            c.line([(0,10),(0,19)], rgba('#f0ffff'))
        elif tier == 12:
            for y in (11,16,21):
                c.line([(-6,y),(-2,y+2),(2,y+2),(6,y)], c.light)
            c.line([(0,9),(0,23)], c.light)
        elif tier == 15:
            c.poly([(-4,13),(0,8),(4,13),(0,19)], c.light)
            c.poly([(-1,13),(0,11),(2,13),(0,16)], rgba('#fff7d8'))
            for x,y in ((-6,10),(6,10),(-7,19),(7,19)):
                c.line([(x,y),(x*1.2,y-2)], c.light)
        elif tier == 16:
            c.line([(-6,10),(-2,12),(-4,16),(1,18),(0,22)], c.light)
            c.line([(6,9),(3,14),(6,17)], c.light)
        elif tier == 17:
            c.poly([(-5,12),(0,8),(5,12),(3,21),(-3,21)], mix(c.base,c.light,.45))
            c.poly([(-2,12),(0,10),(2,12),(1,19),(-1,19)], c.dark)
        elif tier == 18:
            c.poly([(-5,12),(0,7),(5,12),(3,17),(-3,17)], c.light)
            c.line([(-2,12),(-2,14)], c.dark); c.line([(2,12),(2,14)], c.dark)
            for y in (19,23):
                c.line([(-6,y),(-2,y+1),(2,y+1),(6,y)], c.light)
        elif tier == 19:
            c.poly([(-4,12),(0,8),(4,12),(0,18)], c.light)
            c.line([(-6,9),(-8,15),(-5,21)], mix(c.base,c.light,.75))
            c.line([(6,9),(8,15),(5,21)], mix(c.base,c.light,.75))
        elif tier == 20:
            c.poly([(-6,12),(-3,8),(3,8),(6,12),(3,18),(-3,18)], mix(c.base,c.light,.72))
            c.poly([(-3,12),(0,10),(3,12),(1,15),(-1,15)], c.dark)
            c.line([(-5,19),(0,22),(5,19)], c.light)
            c.line([(-8,8),(-10,16),(-7,23)], c.light)
        else:
            c.line([(-5,12),(0,16),(5,12)], c.light)
            c.line([(-4,20),(0,22),(4,20)], mix(c.base,c.light,.7))
    else:
        rank = tier - 21
        robe = 49 + rank % 8
        hem = 17 + rank % 5
        c.poly([(-10,0),(-5,-3),(-1,2),(5,-2),(10,2),(7,24),(hem,robe-3),
                (6,robe+2),(0,35),(-7,robe+5),(-hem,robe-1),(-7,24)])
        c.poly([(-9,5),(-4,8),(-3,26),(-8,robe-2),(-hem+2,robe-3)], c.dark, False)
        c.poly([(2,4),(7,7),(4,25),(10,robe-3),(5,robe),(0,31)], mix(c.base,c.light,.24), False)
        c.poly([(-3,25),(1,25),(5,robe-5),(1,robe-8),(-2,33)], tone(c.base,.72), False)
        c.line([(-5,4),(-2,23),(-7,robe-4)], mix(c.base,c.light,.7))
        c.line([(5,4),(3,23),(10,robe-5)], mix(c.base,c.light,.75))
        c.line([(-hem+2,robe-2),(-8,robe+2)], c.light)
        c.line([(6,robe),(hem-1,robe-4)], c.light)
        shoulder = 11 + rank % 6
        c.poly([(-6, 0), (-shoulder, -4 - rank % 4), (-shoulder - 3, 12), (-9, 16)], c.dark)
        c.poly([(6, 0), (shoulder, -3 - rank % 3), (shoulder + 2, 10), (8, 13)], c.base)
        c.line([(-shoulder, -2 - rank % 4), (-shoulder - 1, 9)], c.light)
        c.line([(-8, 25), (8, 25)], c.light, 2)
        c.poly([(-2, 8), (1, 4), (4, 9), (1, 14)], c.light)
        if rank % 2:
            c.poly([(-3, 27), (-5, robe + 5), (-1, robe + 1), (1, 27)], c.light)
        else:
            for y in (18, 31, 36):
                c.line([(-4, y), (0, y - 2), (4, y)], c.light)
        if rank >= 8:
            c.poly([(-shoulder, 3), (-shoulder - 6, 16), (-shoulder - 2, 31), (-8, 27)], c.dark)
            c.line([(-shoulder - 4, 15), (-shoulder, 29)], c.light)
        if tier in (21,24,27):
            for x in (-8,0,8):
                c.poly([(x-3,robe-8),(x,robe-13),(x+3,robe-8),(x,robe-3)], mix(c.base,c.light,.65))
        elif tier in (25,36):
            for x in (-8,0,8):
                c.poly([(x-3,robe-3),(x-4,robe-10),(x-1,robe-8),(x+1,robe-15),(x+4,robe-6)], mix(c.base,c.light,.58))
        elif tier in (26,30):
            c.line([(-12,robe-8),(-3,robe-11),(0,robe-6),(9,robe-9)], c.light)
        elif tier in (28,33):
            c.poly([(-2,29),(-7,robe+4),(-2,robe-5),(3,robe-1),(5,31)], c.dark)
            c.line([(-9,robe-8),(-12,robe-4)], c.light)
        elif tier in (29,31):
            for x,y in ((-8,robe-8),(1,robe-12),(9,robe-7)):
                c.poly([(x-1,y),(x,y-2),(x+1,y),(x,y+2)], c.light, False)
        elif tier in (32,34):
            c.line([(-12,robe-6),(-6,robe-10),(0,robe-6),(6,robe-10),(12,robe-6)], c.light)
        elif tier == 35:
            for y in (13,18,23):
                for x in (-5,0,5):
                    c.poly([(x-3,y),(x,y+3),(x+3,y),(x,y-1)], mix(c.base,c.light,.6))
        if tier in (35,36):
            # Overlapping dragon scales step down the rear shoulder and mantle.
            for k in range(3):
                x, y = -shoulder-1-k, 3+k*7
                c.poly([(x+5,y),(x-2,y+1),(x-4,y+6),(x,y+10),(x+5,y+7)],
                       mix(c.base,c.light,.18+k*.08))
                c.poly([(x+4,y+1),(x-1,y+2),(x-2,y+5),(x+2,y+6)],
                       mix(c.base,c.light,.48), False)
                c.line([(x-2,y+6),(x,y+8),(x+3,y+7)], c.dark)
                c.line([(x-1,y+2),(x+3,y+1)], c.light)
        if tier == 33:
            # Unequal notches expose the original lower body through a torn hem.
            for points in ([(-hem-1,robe+6),(-12,robe-9),(-9,robe-2),(-5,robe+6)],
                           [(4,robe+6),(8,robe-7),(10,robe-2),(hem+1,robe+6)]):
                c.poly(points, (0,0,0,0), False)
            c.line([(-12,robe-8),(-10,robe-3)], mix(c.base,c.light,.6))
            c.line([(8,robe-6),(10,robe-2)], c.light)
    a = np.array(c.im)
    a[:, :, 3][pose['face'] | pose['arms'] | pose['feet']] = 0
    a[a[:, :, 3] == 0] = 0
    return Image.fromarray(a)


def gear_names(slot):
    source = (ROOT / 'Assets/Scripts/Data/GameData.Generated.cs').read_text()
    body = source.split(f' {slot} = new GearDef[] {{')[1].split('};')[0]
    return re.findall(r'name="([^"]+)"', body)


def pack_layers(layers):
    """Keep two transparent pixels inside each crop and another two between cells."""
    crops, unique, lookup = [], [], {}
    for layer in layers:
        x0, y0, x1, y1 = layer.getbbox()
        crop = (max(0, x0 - 2), max(0, y0 - 2), min(layer.width, x1 + 2), min(layer.height, y1 + 2))
        image = layer.crop(crop)
        key = (image.size, hashlib.sha256(image.tobytes()).digest())
        if key not in lookup:
            lookup[key] = len(unique)
            unique.append(image)
        crops.append((lookup[key], crop))
    order = sorted(range(len(unique)), key=lambda i: (unique[i].height, unique[i].width), reverse=True)
    candidates = []
    for limit in range(128, 513, 32):
        shelves, placed, used_width = [], {}, 0
        for i in order:
            image = unique[i]
            fitting = [s for s in shelves if s['x'] + image.width + 2 <= limit]
            if fitting:
                shelf = max(fitting, key=lambda s: s['x'])
            else:
                top = shelves[-1]['y'] + shelves[-1]['height'] + 2 if shelves else 2
                shelf = dict(x=2, y=top, height=image.height)
                shelves.append(shelf)
            placed[i] = (shelf['x'], shelf['y'])
            used_width = max(used_width, shelf['x'] + image.width + 2)
            shelf['x'] += image.width + 2
        width = (used_width + 3) // 4 * 4
        height = (shelves[-1]['y'] + shelves[-1]['height'] + 2 + 3) // 4 * 4
        candidates.append((width * height, width, height, placed))
    _, width, height, placed = min(candidates, key=lambda p: p[0])
    assert width <= 4096 and height <= 4096
    atlas = Image.new('RGBA', (width, height))
    for i, crop in enumerate(unique):
        atlas.paste(crop, placed[i])
    frames = []
    for original, (index, bounds) in zip(layers, crops):
        x, y = placed[index]
        crop = atlas.crop((x, y, x + unique[index].width, y + unique[index].height))
        rebuilt = Image.new('RGBA', original.size)
        rebuilt.paste(crop, (bounds[0], bounds[1]))
        assert np.array_equal(np.asarray(original), np.asarray(rebuilt)), 'crop registration lost original RGBA'
        frames.append(dict(x=x, y=height - y - crop.height, width=crop.width, height=crop.height,
                           cropX=bounds[0], cropY=bounds[1]))
    return atlas, frames


def build(round_number):
    OUT.mkdir(exist_ok=True)
    cfg = json.loads((RES / 'Config/clip_table.json').read_text())
    poses = []
    for gender in ('main_m', 'main_f'):
        for clip in CLIPS:
            spec = next(c for c in cfg['clips'] if c['key'] == f'{gender}/{clip}')
            for frame in spec['frames']:
                source = Image.open(RES / f'Sprites/CharactersBaked/{gender}/{clip}/frame_{frame:02}.png').convert('RGBA')
                pose = pose_data(gender, clip, frame, source)
                poses.append((gender, clip, frame, source, pose))
    assert len(poses) == 70
    manifest = dict(version=2, poses=[], helmets=[], armors=[])
    for i, (gender, clip, frame, source, pose) in enumerate(poses):
        manifest['poses'].append(dict(key=f'{gender}/{clip}/frame_{frame:02}', gender=gender, clip=clip, frame=frame,
                                      width=source.width, height=source.height))
    previews = {}
    stats = []
    edges = []
    for slot, prefix, draw in (('helmet', 'h', helmet), ('armor', 'a', armor)):
        hashes = []
        for tier in range(37):
            layers = []
            digest = hashlib.sha256()
            for i, (gender, clip, frame, source, pose) in enumerate(poses):
                layer = draw(source.size, pose, tier)
                arr = np.asarray(layer)
                assert np.any(arr[:, :, 3]), (slot, tier, gender, clip, frame, 'empty')
                protected = pose['face'] | pose['arms'] | pose['feet']
                assert not np.any(arr[:, :, 3][protected]), (slot, tier, gender, clip, frame, 'protected pixel')
                edge_count = int((arr[0,:,3]>0).sum()+(arr[-1,:,3]>0).sum()+(arr[:,0,3]>0).sum()+(arr[:,-1,3]>0).sum())
                if edge_count:
                    edges.append(dict(slot=slot,tier=tier,gender=gender,clip=clip,frame=frame,pixels=edge_count))
                if slot == 'helmet' and tier == 0:
                    occupied = np.argwhere(arr[:,:,3]>0)
                    angle = math.radians(pose['roll'])
                    dx = occupied[:,1] - pose['head'][0]
                    dy = occupied[:,0] - pose['head'][1]
                    local_y = -dx * math.sin(angle) + dy * math.cos(angle)
                    assert local_y.min() >= -11 and local_y.max() <= -3, 'cloth band received a high-tier horn'
                digest.update(arr[:, :, 3].tobytes())
                layers.append(layer)
                if tier in (0, 2, 6, 15, 20, 21, 29, 36):
                    previews[(slot, tier, i)] = layer
            path = f'Sprites/Wearables/{prefix}_{tier:02}'
            atlas, frames = pack_layers(layers)
            atlas.save(RES / (path + '.png'))
            manifest['helmets' if slot == 'helmet' else 'armors'].append(dict(path=path, width=atlas.width, height=atlas.height, frames=frames))
            hashes.append(digest.hexdigest())
            stats.append(dict(slot=slot, tier=tier, name=gear_names('Helmets' if slot == 'helmet' else 'Armors')[tier],
                              silhouette=hashes[-1], atlasWidth=atlas.width, atlasHeight=atlas.height,
                              rgbaBytes=atlas.width * atlas.height * 4,
                              uniqueRects=len({(f['x'],f['y'],f['width'],f['height']) for f in frames})))
        assert len(set(hashes)) == 37, (slot, 'duplicate tier silhouette')
    (RES / 'Config/wearable_manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    assert REVIEW.is_dir(), 'The existing review destination must be available'
    for gender in ('main_m', 'main_f'):
        sheet = Image.new('RGB', (8 * CELL, 7 * CELL), '#242424')
        d = ImageDraw.Draw(sheet)
        for i, (g, clip, frame, source, pose) in enumerate(poses):
            if g != gender:
                continue
            row = CLIPS.index(clip)
            indices = [j for j, p in enumerate(poses) if p[0] == gender and p[1] == clip]
            col = indices.index(i)
            im = source.copy()
            im.alpha_composite(previews[('armor', 20, i)])
            im.alpha_composite(previews[('helmet', 20, i)])
            ImageDraw.Draw(im).line([pose['neck'], pose['waist']], fill='#ff00ff', width=1)
            sheet.paste(im, (col * CELL, row * CELL), im)
            d.text((col * CELL, row * CELL), f'{clip}/{frame}', fill='white')
        sheet.save(REVIEW / f'wear_native_r{round_number}_pose_{gender}.png')
        masks = Image.new('RGB', sheet.size, '#242424')
        md = ImageDraw.Draw(masks)
        for i, (g, clip, frame, source, pose) in enumerate(poses):
            if g != gender:
                continue
            row = CLIPS.index(clip)
            indices = [j for j, p in enumerate(poses) if p[0] == gender and p[1] == clip]
            col = indices.index(i)
            arr = np.array(source)
            for region, color in ((pose['face'], (0, 230, 80)), (pose['arms'], (40, 145, 255)), (pose['feet'], (255, 90, 80))):
                visible = region & (arr[:, :, 3] > 0)
                arr[visible, :3] = ((arr[visible, :3].astype(int) + np.array(color)) // 2).astype(np.uint8)
            im = Image.fromarray(arr)
            ImageDraw.Draw(im).line([pose['neck'], pose['waist']], fill='#ff00ff', width=1)
            masks.paste(im, (col * CELL, row * CELL), im)
            md.text((col * CELL, row * CELL), f'{clip}/{frame}', fill='white')
        masks.save(REVIEW / f'wear_native_r{round_number}_mask_{gender}.png')
    sheet = Image.new('RGB', (10 * CELL, 8 * CELL), '#242424')
    d = ImageDraw.Draw(sheet)
    for slot_index, (slot, draw) in enumerate((('helmet', helmet), ('armor', armor))):
        for tier in range(37):
            gender, clip, frame, source, pose = poses[0]
            im = source.copy(); im.alpha_composite(draw(source.size, pose, tier))
            row, col = slot_index * 4 + tier // 10, tier % 10
            sheet.paste(im, (col * CELL, row * CELL), im)
            d.text((col * CELL, row * CELL), f'{slot[0]}{tier:02} {DESIGNS[tier][0]}', fill='white')
    sheet.save(REVIEW / f'wear_native_r{round_number}_tiers.png')
    memory = sum(s['rgbaBytes'] for s in stats)
    pose_stats = [dict(key=f'{g}/{clip}/frame_{frame:02}', head=p['head'], neck=p['neck'], waist=p['waist'], roll=p['roll'],
                       skull=p['skull'],
                       facePixels=int(p['face'].sum()), armPixels=int(p['arms'].sum()), footPixels=int(p['feet'].sum()))
                  for g, clip, frame, source, p in poses]
    (REVIEW / f'wear_native_r{round_number}_stats.json').write_text(json.dumps(dict(round=round_number, frames=70, layers=5180,
         rgbaBytes=memory, originalFullCellBytes=740 * 2072 * 74 * 4, stats=stats, poses=pose_stats,
         canvasEdgeTouches=edges, needsVisualReview=True), indent=2) + '\n')
    assert not edges, ('wearable touches canvas edge', edges)
    print(f'PASS: 74 packed atlases, 70 body poses, 5180 layers; exact crop RGBA; face/arms/feet protected; 37 silhouettes/slot; RGBA {memory / 1048576:.2f} MiB')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--round', type=int, default=1)
    build(parser.parse_args().round)
