#!/usr/bin/env python3
# raw/*.json 스프라이트시트를 유니티 Assets/Sprites 에 프레임별 PNG로 슬라이스한다.
# character(main_m/main_f/t1~21) + object(obj_*, propA/B/C) 대상. tileset(kind:tileset)은 별도 처리.
import json, os, re
from PIL import Image

RAW = os.path.join(os.path.dirname(os.path.abspath(__file__)), "raw")
UNITY_SPRITES = os.path.abspath(os.path.join(RAW, "../../../unity_client/Assets/Resources/Sprites"))

CLIP_MAP = [
    (re.compile(r"walk|walking", re.I), "walk"),
    (re.compile(r"idle|breathing", re.I), "idle"),
    (re.compile(r"hurt|taking", re.I), "hurt"),
    (re.compile(r"death|dead", re.I), "dead"),
    (re.compile(r"attack|swing|thrust|chop|slash|stab|punch|bite|claw|kick|straight|snap|combo|slam|ram|headbutt|paw|stance|breath|palm", re.I), "attack"),
]

def clip_name(anim_text, fallback):
    for rx, name in CLIP_MAP:
        if rx.search(anim_text or ""):
            return name
    return fallback

def slice_sheet(json_path, out_dir):
    d = json.load(open(json_path))
    top = d.get("character") or d.get("object")
    if not top or "spritesheet" not in d:
        return None
    ss = d["spritesheet"]
    png_path = os.path.join(RAW, ss["path"])
    if not os.path.exists(png_path):
        print("MISSING PNG", png_path)
        return None
    im = Image.open(png_path)
    cw, ch = ss["cell_size"]["width"], ss["cell_size"]["height"]
    cols = ss["columns"]
    clips = {}
    used = {}
    for row in ss["rows"]:
        r = row["row"]
        n = row["frame_count"]
        rtype = row["type"]
        if rtype == "rotations":
            name = "rotations"
        else:
            name = clip_name(row.get("animation", ""), f"anim{r}")
        if name in used:
            used[name] += 1
            name = f"{name}{used[name]}"
        else:
            used[name] = 0
        clip_dir = os.path.join(out_dir, name)
        os.makedirs(clip_dir, exist_ok=True)
        frame_paths = []
        for i in range(n):
            col = i % cols
            x, y = col * cw, r * ch
            frame = im.crop((x, y, x + cw, y + ch))
            fp = os.path.join(clip_dir, f"frame_{i:02d}.png")
            frame.save(fp)
            frame_paths.append(fp)
        clips[name] = {"count": n, "direction": row.get("direction") or (row.get("directions") or [None])[0], "label": row.get("animation", "")}
    return {"key": os.path.splitext(os.path.basename(json_path))[0], "clips": clips, "size": ss["cell_size"]}

def run(subset, dest_subdir):
    out_root = os.path.join(UNITY_SPRITES, dest_subdir)
    os.makedirs(out_root, exist_ok=True)
    manifest = {}
    for key in subset:
        jp = os.path.join(RAW, f"{key}.json")
        if not os.path.exists(jp):
            print("no json for", key); continue
        out_dir = os.path.join(out_root, key)
        res = slice_sheet(jp, out_dir)
        if res:
            manifest[key] = res
            print("sliced", key, "->", dest_subdir, list(res["clips"].keys()))
    return manifest

if __name__ == "__main__":
    import sys
    chars = ["main_m", "main_f"] + [f"t{i}" for i in range(1, 22)]
    objs = ["obj_bomb","obj_energybolt","obj_firebolt","obj_firebreath","obj_firepillar","obj_heal",
            "obj_hitspark","obj_levelup","obj_nuke","obj_poof","obj_stone",
            "propA_bush","propA_fireflies","propA_log","propA_mushroom","propA_reeds","propA_tree",
            "propB_building","propB_car","propB_lamp","propB_light","propB_rubble","propB_sign",
            "propC_cart","propC_coal","propC_frame","propC_lantern","propC_lava","propC_vent"]

    m1 = run(["main_m", "main_f"], "Characters")
    m2 = run([f"t{i}" for i in range(1, 22)], "Monsters")
    m3 = run(objs, "FX")
    allm = {**m1, **m2, **m3}
    with open(os.path.join(UNITY_SPRITES, "slice_manifest.json"), "w") as f:
        json.dump(allm, f, indent=1)
    print("TOTAL keys sliced:", len(allm))
