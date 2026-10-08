#!/usr/bin/env python3
# raw/*.json 을 읽어 manifest.js 를 뽑는다.
# 스프라이트시트 애니 이름(사람이 붙인 자유문자열) → 엔진 5클립 regex 매핑.
# index.html 이 manifest.js 로드, game.js 가 window.ANIM 을 읽는다.
# raw/ 애셋 갱신하면 재실행: python3 field/anim/gen_manifest.py
import os, json, re, glob

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")
OUT = os.path.join(HERE, "manifest.js")


def pick(names, rx):
    return [n for n in names if re.search(rx, n, re.I)]


def clips(key, names):
    walk = pick(names, r"\bwalk|pant-walk")
    hurt = pick(names, r"hurt")
    dead = pick(names, r"death|die|\bburst|explode")
    excl = set(hurt) | set(dead) | set(walk)
    atk = [n for n in pick(names, r"attack|sword|swing|thrust|slam|kick|bite|ram|swipe|headbutt|combo|switch")
           if n not in excl]
    idle = pick(names, r"breathing idle|\bidle")
    w = walk[0] if walk else (names[0] if names else None)
    return {
        "idle": (idle[0] if idle else w),
        "walk": w,
        "attack": atk or ([w] if w else []),
        "hurt": hurt[0] if hurt else None,
        "dead": dead[0] if dead else None,
    }


entries = {}
for jf in sorted(glob.glob(RAW + "/*.json")):
    key = os.path.basename(jf)[:-5]
    lay = json.load(open(jf))
    if key.startswith("tiles_"):
        n = len(lay.get("tileset_data", {}).get("tiles", []))
        entries[key] = {"png": f"anim/raw/{key}.png", "json": f"anim/raw/{key}.json",
                        "kind": "tileset", "tiles": n}
        continue
    names = [r["animation"] for r in lay["spritesheet"]["rows"] if r.get("animation")]
    if key.startswith("obj_") or key.startswith("prop"):
        entries[key] = {"png": f"anim/raw/{key}.png", "json": f"anim/raw/{key}.json",
                        "anim": names[0] if names else None}
    else:
        c = clips(key, names)
        miss = [k for k, v in c.items() if not v]
        if miss:
            print("WARN", key, "missing", miss, "from", names)
        entries[key] = {"png": f"anim/raw/{key}.png", "json": f"anim/raw/{key}.json", "clips": c}

banner = "// 자동생성: field/anim/gen_manifest.py. 직접 수정 금지 — raw/ 고치고 재실행.\n"
open(OUT, "w").write(banner + "window.ANIM = " + json.dumps(entries, ensure_ascii=False, indent=2) + ";\n")
print("wrote", OUT, "-", len(entries), "keys")
