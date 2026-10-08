#!/usr/bin/env python3
# v3.0 게임 데이터(몬스터21·무기42·투구/갑옷74·스킬10·강화·XP) 생성 + 장비 스프라이트 유니티로 복사.
# 소스: 옵시디언 설계문서(아트_전면개편_계획/장비80_컨셉/검지팡이42종/투구방어구_포맷지침) 수치를 그대로 포팅.
# 밸런스 수치는 "AI 초안"으로 명시된 항목 — 1차 드래프트, 나중에 sweep 스크립트로 튜닝 대상.
import json, os, shutil, math

HERE = os.path.dirname(os.path.abspath(__file__))
FIELD = os.path.dirname(HERE)
GEAR_RAW = os.path.join(FIELD, "gear_raw")
UNITY = os.path.join(FIELD, "..", "unity_client")
SPRITES = os.path.join(UNITY, "Assets", "Resources", "Sprites")
CS_OUT = os.path.join(UNITY, "Assets", "Scripts", "Data", "GameData.Generated.cs")

def lerp_log_table(anchors, steps):
    """anchors: [(level, value), ...] 오름차순. steps: 요청 레벨 리스트. 로그공간 선형보간 + 마지막 구간 비율로 외삽."""
    out = {}
    for lv in steps:
        if lv <= anchors[0][0]:
            out[lv] = anchors[0][1]; continue
        if lv >= anchors[-1][0]:
            (l0, v0), (l1, v1) = anchors[-2], anchors[-1]
            ratio = (math.log(v1) - math.log(v0)) / (l1 - l0)
            out[lv] = round(math.exp(math.log(v1) + ratio * (lv - l1)))
            continue
        for i in range(len(anchors) - 1):
            l0, v0 = anchors[i]; l1, v1 = anchors[i + 1]
            if l0 <= lv <= l1:
                if l0 == l1: out[lv] = v0; break
                t = (lv - l0) / (l1 - l0)
                out[lv] = round(math.exp(math.log(v0) + t * (math.log(v1) - math.log(v0))))
                break
    return out

LEVELS21 = list(range(0, 101, 5))  # 0,5,...,100 (21개)

# ---------------- 무기 42 (검21+지팡이21, 260911_검지팡이-42종-생성-handoff 확정 이름/ID) ----------------
SWORD = [
    (0,  "낡은단검",  "ed75959b-7026-4a21-be48-c6c3376db076"),
    (5,  "청동단검",  "b6dd204f-8038-42fa-99d1-fde9d11fd197"),
    (10, "청동검",    "3e0c1dbb-b9ec-40e6-8237-9a9796a8b998"),
    (15, "강철검",    "940c9f36-b10b-4221-b64b-4dc14005861f"),
    (20, "기사검",    "d15da375-44f4-4e1f-82cc-e694c0fe02ab"),
    (25, "십자검",    "e3ab83bf-518b-44f4-8cbd-ca21665dbe49"),
    (30, "대검",      "26db5c9a-e138-49cb-b462-513b71dd4436"),
    (35, "미스릴검",  "12d13ab9-a86e-490d-94d9-49e11262782d"),
    (40, "용아검",    "db982c9a-7949-4d8e-b60d-366c03a89e49"),
    (45, "화염검",    "06504e51-c700-40ee-a574-69852016de02"),
    (50, "뇌명검",    "033d3f7d-fbcf-413e-af8b-70197a3e090a"),
    (55, "서리검",    "1908491e-4370-4ed3-b1bb-c1e6eba29df6"),
    (60, "명왕검",    "14c7e0c4-e9e9-4f17-8c00-b3498032e0c5"),
    (65, "파사검",    "198aa60c-38c9-431d-9dd3-68bc4b06e5a2"),
    (70, "뇌신검",    "df13020e-8ad1-45da-9112-a8f3af23613f"),
    (75, "광휘검",    "9141c8c5-75e3-4a4f-a8d3-4f7b9164dc51"),
    (80, "파멸검",    "8e3ea8d6-2095-4e88-9a60-28142ae5ca84"),
    (85, "절멸검",    "ba1c71b1-db51-4e78-a6b1-cf4059f7c9d7"),
    (90, "종말의낫",  "1f83ac88-f36f-4d47-a9d6-53471cbe341d"),
    (95, "최후의검",  "cc927ba6-eb86-476a-961e-bd68cbf81054"),
    (100,"오메가",    "074bfda8-28d1-434c-b086-7d789d4d6b06"),
]
STAFF = [
    (0,  "나무지팡이",   "0cad1c67-df82-455c-ac20-1e0eb992992a"),
    (5,  "도토리지팡이",  "faae29ad-1adf-4b9a-8a58-b0dc481a57de"),
    (10, "참나무지팡이",  "6df7ac3a-9307-4ef8-bc67-ca2d3dc3317f"),
    (15, "무쇠지팡이",    "df57af80-1bf8-46b0-aa91-c34cae847cb3"),
    (20, "룬지팡이",      "907d46e2-2f2c-4b19-9e87-d6fdbfc3af10"),
    (25, "수정지팡이",    "081537da-829b-4cdb-8099-9c939277eb70"),
    (30, "자수정지팡이",  "1606ce9f-44b5-4403-9030-7b100d6945a2"),
    (35, "은빛지팡이",    "abf7eeec-d878-4b9c-9080-d255060133c6"),
    (40, "얼음지팡이",    "9dc36ccf-4c23-4d54-8660-695f35ebfee9"),
    (45, "화염지팡이",    "cf8a62d4-a203-4398-a7da-6b75abea9306"),
    (50, "폭풍지팡이",    "92ae2ae0-4627-41a9-8984-8ea7d3c3c6d9"),
    (55, "서리지팡이",    "3308f34e-34c7-4b10-804b-57f713e8cf36"),
    (60, "심연지팡이",    "f0652f4b-0de4-45c9-8035-657fb42ef081"),
    (65, "몽환지팡이",    "3c710946-45bb-497f-ae61-3dc9e85b58ef"),
    (70, "천공지팡이",    "f938b762-0e81-49e2-a28e-dde0d66fa6fa"),
    (75, "성좌지팡이",    "c3a5527a-9059-40dc-8f54-98d3439cd4bc"),
    (80, "창세지팡이",    "aca48446-537e-4ffc-afbf-1d9e64add2dd"),
    (85, "공허지팡이",    "7cee8af7-c189-4a66-9c23-e2f3fe87f26f"),
    (90, "창조의아침",    "bfbc225d-6a35-412d-846d-ddb7844d736d"),
    (95, "드래곤스태프",  "49e5bb08-4c82-49e8-985d-93ea50be6123"),
    (100,"적룡스태프",    "e388de28-4a07-471f-a004-7207b2135938"),
]
SWORD_DMG = {0:(4,6),5:(6,9),10:(9,13),15:(14,19),20:(20,28),25:(30,41),30:(45,61),35:(66,90),
             40:(98,132),45:(145,196),50:(214,290),55:(317,429),60:(469,635),65:(695,941),
             70:(1029,1392),75:(1522,2060),80:(2253,3048),85:(3334,4510),90:(4934,6676),95:(7302,9880)}
STAFF_DMG = {0:(4,6),5:(6,8),10:(9,12),15:(13,18),20:(19,26),25:(29,39),30:(43,58),35:(63,86),
             40:(93,125),45:(138,186),50:(203,276),55:(301,408),60:(446,603),65:(660,894),
             70:(978,1322),75:(1446,1957),80:(2140,2896),85:(3167,4285),90:(4687,6342),95:(6937,9386)}
def extrap100(tbl):
    lo0,hi0 = tbl[90]; lo1,hi1 = tbl[95]
    r_lo = lo1/lo0; r_hi = hi1/hi0
    tbl[100] = (round(lo1*r_lo), round(hi1*r_hi))
extrap100(SWORD_DMG); extrap100(STAFF_DMG)

# ---------------- 투구/갑옷 74 (260911_투구방어구_포맷지침 — 리스트 순서 역산으로 확정) ----------------
MAGE_LINE = ["수정","자수정","은빛","얼음","화염","폭풍","서리","심연","몽환","천공","성좌","창세","공허","창조","드래곤","적룡"]  # t6..t21
WARRIOR_LINE = ["은","뿔","미스릴","용린","화염","뇌명","서리","명왕","파사","뇌신","광휘","파멸","절멸","종언","근원","오메가"]  # t6..t21
COMMON_LINE = ["해진천","무명","가죽","무두질가죽","리벳가죽"]  # t1..t5

MAGE_ARMOR_IDS_DESC = ["9f7ede8f-95bf-41a0-b766-77aba5f878fc","5b15c745-77ed-441d-98af-81df24b1bc10","b1ba4c68-beb6-4071-b6d3-f5a1d0bafa3a","55ed8235-9554-4672-8ac3-3f7e484f5ac5","61034894-4f28-4f67-88c7-321bc7e66ada","ac3cd7bd-64fb-4011-9904-16ef253373bb","cf1f56b7-b45c-47e2-ad7f-e4c93212e3eb","18bd49b1-61ed-4332-9718-a1b65b99c50b","fe50ebbd-9d89-45f9-8fd5-0ca0c92a6fff","8831984f-39b2-4483-b99a-13007f0434b8","94820a78-921d-4cb7-9f2b-0a1c3d0a1819","af0eb105-0d82-4278-9662-951f17b01e7a","e4483608-259b-43b9-ba0c-489fc7ef9164","8ad5b0fb-c00f-4232-b6f6-025a576305e1","4831b72d-89c1-4c19-a411-3fbf9eb06133","23a0253f-0f8f-4c97-9c99-af33e6e25626"]  # t21..t6 desc
WARRIOR_ARMOR_IDS_DESC = ["2be84ab3-9caa-4e93-ad89-e73d26996389","80397d7c-fa1e-4d95-a359-c894e457c4af","8f540ab5-d249-49e7-ba48-dcbc5813f653","66cb60cc-18d8-464e-acf6-8b516e0f60d7","8bf91134-b614-4d83-a9ac-9899663f246d","b894a006-4e0a-4f2b-bbb3-096f89a4942d","f9421cfc-bcf6-4e79-b389-2aa1e18e2782","34123c20-5b87-4bd9-b61d-a823f675f704","9130e764-0336-4dd9-8eb5-619c00d84553","7e48e1f6-ecb1-4fbf-badb-e9da459ab415","97459b63-8872-48a6-8111-454f3ae017f2","f4046656-5d88-4a32-aa4c-fbe94162b6cf","16fb1337-7937-4b47-aa51-68ce4292da72","6c729735-c032-4e0b-ad4e-3ce687e9431b","9f8d9857-58df-45ab-ac9f-d83866f7ac3e","4c356ba7-416c-4980-a050-c0621b031788"]  # t21..t6 desc
COMMON_ARMOR_IDS_DESC = ["4f247c1a-56cf-481f-a6e9-dabb41d1d90a","e8c16095-b6a2-4600-a043-102976a5864b","43c80186-ca74-46b5-9aad-f3c8228e0f2a","2e6082d0-86ce-41ba-b161-56580a3f1a7e","fcbf3cfd-33c0-4ab0-abcf-44605b34b620"]  # t5..t1 desc

MAGE_HELM_IDS_DESC = ["f7c3ed2a-0c0b-4bfe-a793-d8c7f740e834","6bcfdbde-0f0e-4ee5-87bc-f6ffa8914cfa","3e15c90d-8511-46ab-9758-6f09e41433e1","052cebfe-a460-4086-9d1a-484179771031","fbd19482-cd04-4b33-b965-cb7db8b9f929","a4d8d6a6-b69b-4361-a698-3ca318cb8108","6d580367-918f-445b-b791-e0cac2ea1a7f","ee418df2-2d63-47db-b41f-903b93161b06","a356b95e-62e8-414b-b4be-5b272432093f","fb1c9ae0-971f-4a33-9b9e-b9853dfe46e7","4e09b787-d143-4e53-a88a-cd066e37c1d4","075bbede-2daa-412d-9d29-d74aa217d15a","58d2a632-564b-422d-965e-4875a2e413d0","6618e2cc-3a55-4182-9c99-004cbe3aab0a","ef024cb7-07b9-4e2c-930f-ea39c365c3ec","d791314b-3ad3-49f8-9d8d-8bfc093bdf79"]  # t21..t6 desc
WARRIOR_HELM_IDS_DESC = ["4a3bb3b7-8669-4001-8566-95d8537a1f1a","ab76e127-1e20-48ba-914d-8a826d6b9d10","79abe94f-3d39-4081-bdcb-bad660864c35","90791ce0-8bf7-44a3-9689-4d2ef8214a66","d1354330-7683-4fae-bec7-1699138ba853","4a37208a-684e-4358-82f8-d7d43ea8ad42","d8850a54-6509-45af-8239-129af4bd78c2","b4511c14-c851-464d-aa16-08ef3c2bbbba","58aeb143-1289-462a-a32e-47ef8f458a62","7f250fe3-3143-408f-9e20-035094293d1e","678b809c-0275-4ea2-aa03-6ee294ecbb98","c1c53c60-e2e0-493f-816f-c5e8853ed317","dcc22843-2b11-4f99-8f30-343834eb4883","3b88c1e4-8b67-4ac1-bc11-6c935dd9760b","4fac4150-4397-4b90-9fd9-937a23f356f6","4313156e-cfcb-4346-9246-7a4643b5bb77"]  # t21..t6 desc
COMMON_HELM_IDS_DESC = ["2fabf377-4285-416d-b5bc-d7c2122aaf4d","d2aedbd3-2705-4628-8e13-f622389e0ca6","be2eb973-334d-427c-a367-20fdcf6bc6d9","84fd0c16-f339-4582-915f-d7b1c52df0c6","c8cd3ea7-65e0-4f5e-859c-fcc85356be28"]  # t5..t1 desc

HELM_DEF_ANCHORS = [(0,3),(10,5),(20,9),(30,16),(40,28),(50,48),(60,84),(70,146),(80,255),(90,444),(95,586)]
ARMOR_DEF_ANCHORS = [(0,4),(10,7),(20,13),(30,22),(40,39),(50,69),(60,123),(70,217),(80,384),(90,678),(95,902)]
HELM_DEF = lerp_log_table(HELM_DEF_ANCHORS, LEVELS21)
ARMOR_DEF = lerp_log_table(ARMOR_DEF_ANCHORS, LEVELS21)

def build_gear_line(common_ids_desc, warrior_ids_desc, mage_ids_desc, common_names, warrior_names, mage_names):
    """desc(내림차순, t21..t1 또는 t21..t6/t5..t1)를 승차순으로 뒤집어 (level,name,class,id) 리스트로."""
    out = []
    for i, name in enumerate(common_names):  # t1..t5
        lvl = i * 5
        gid = common_ids_desc[len(common_names) - 1 - i]
        out.append((lvl, name, "common", gid))
    for cls, ids_desc, names in [("warrior", warrior_ids_desc, warrior_names), ("mage", mage_ids_desc, mage_names)]:
        for i, name in enumerate(names):  # t6..t21 ascending
            lvl = (i + 5) * 5
            gid = ids_desc[len(names) - 1 - i]
            out.append((lvl, name, cls, gid))
    return out

HELMETS = build_gear_line(COMMON_HELM_IDS_DESC, WARRIOR_HELM_IDS_DESC, MAGE_HELM_IDS_DESC, COMMON_LINE, WARRIOR_LINE, MAGE_LINE)
ARMORS  = build_gear_line(COMMON_ARMOR_IDS_DESC, WARRIOR_ARMOR_IDS_DESC, MAGE_ARMOR_IDS_DESC, COMMON_LINE, WARRIOR_LINE, MAGE_LINE)

# ---------------- 몬스터 21 (260910 pixellab 감사표 이름 + 260911 리스크레지스터 tier 분류 초안) ----------------
MONSTERS = [
    (1,  "rat",     "쥐",            "basic",   1),
    (2,  "slimeman","슬라임맨",      "basic",   5),
    (3,  "deer",    "사슴",          "basic",   10),
    (4,  "boar",    "멧돼지",        "basic",   14),
    (5,  "bear",    "곰",            "basic",   19),
    (6,  "tiger",   "호랑이",        "basic",   24),
    (7,  "zombie",  "좀비",          "rare",    29),
    (8,  "ghostwoman","귀신여자",    "rare",    33),
    (9,  "cultzombie","천주교좀비",  "rare",    38),
    (10, "thug",    "칼든강도",      "rare",    43),
    (11, "madongseok","마동석",      "rare",    48),
    (12, "monk",    "선재스님",      "rare",    52),
    (13, "werewolf","늑대인간",      "unique",  57),
    (14, "orc",     "오크",          "unique",  62),
    (15, "sharkleg","상어다리",      "unique",  67),
    (16, "logbat",  "통나무배트",    "unique",  71),
    (17, "ancientwolf","고대의늑대인간","unique",76),
    (18, "orcberserk","오크광전사",  "unique",  81),
    (19, "dragon",  "드래곤",        "midboss", 60),
    (20, "flameemperor","염제",     "boss",    85),
    (21, "elder",   "수령동지",      "hidden",  95),
]
def mstat(i):
    hp  = round(20 * 1.62 ** (i - 1))
    atk = round(4  * 1.44 ** (i - 1))
    dfn = round(1  * 1.40 ** (i - 1))
    exp = round(6  * 1.42 ** (i - 1))
    glo = round(3  * 1.40 ** (i - 1))
    return hp, atk, dfn, exp, glo

# ---------------- 스킬 10 (아트_전면개편_계획 §7) ----------------
SKILLS = [
    ("qi_sword",   20, 10, "참격파",     "sword", 2.0, "line"),
    ("qi_staff",   20, 10, "마력탄",     "staff", 2.2, "line"),
    ("rain_sword", 40, 20, "반월참",     "sword", 2.8, "area"),
    ("rain_staff", 40, 20, "화염구",     "staff", 3.0, "area"),
    ("volc_sword", 60, 30, "대지가르기", "sword", 5.0, "area"),
    ("volc_staff", 60, 30, "연쇄낙뢰",   "staff", 5.5, "area"),
    ("king_sword", 80, 60, "검왕강림",   "sword", 8.5, "facing"),
    ("king_staff", 80, 60, "블리자드",   "staff", 9.0, "facing"),
    ("end_sword",  100,120,"천지개벽",   "sword", 19,  "map"),
    ("end_staff",  100,120,"메테오",     "staff", 20,  "map"),
]

# ---------------- 강화 +25 (§5) ----------------
def enh_pct_and_succ():
    pct = {}; succ = {}
    for g in range(1, 5): pct[g] = g * 1
    for g in range(5, 8): pct[g] = pct[4] + (g - 4) * 2
    for g in range(8, 11): pct[g] = pct[7] + (g - 7) * 3
    for g in range(11, 15): pct[g] = pct[10] + (g - 10) * 4
    for g in range(15, 26): pct[g] = pct[14] + (g - 14) * 5
    s1 = [100, 95, 90, 85]
    s2 = [70, 58, 48]
    s3 = [40, 34, 28]
    s4 = [23, 18, 15, 12]
    s5 = [9, 7, 5, 4, 3, 2.5, 2, 1.5, 1.2, 0.9, 0.6]
    allrates = s1 + s2 + s3 + s4 + s5
    for g in range(1, 26): succ[g] = allrates[g - 1]
    return pct, succ
ENH_PCT, ENH_SUCC = enh_pct_and_succ()

# ================= 스프라이트 복사 =================
def slug(name): return name.replace(" ", "").replace("/", "-")

def copy_gear_sprites():
    os.makedirs(os.path.join(SPRITES, "Weapons"), exist_ok=True)
    os.makedirs(os.path.join(SPRITES, "Helmets"), exist_ok=True)
    os.makedirs(os.path.join(SPRITES, "Armors"), exist_ok=True)
    n = 0
    for lvl, name, gid in SWORD + STAFF:
        src = os.path.join(GEAR_RAW, "weapon", gid, "rotations", "unknown.png")
        dst = os.path.join(SPRITES, "Weapons", f"L{lvl:03d}_{slug(name)}.png")
        if os.path.exists(src): shutil.copyfile(src, dst); n += 1
    for lvl, name, cls, gid in HELMETS:
        src = os.path.join(GEAR_RAW, "armor", gid, "rotations", "south-east.png")
        dst = os.path.join(SPRITES, "Helmets", f"L{lvl:03d}_{cls}_{slug(name)}.png")
        if os.path.exists(src): shutil.copyfile(src, dst); n += 1
    for lvl, name, cls, gid in ARMORS:
        src = os.path.join(GEAR_RAW, "armor", gid, "rotations", "south-east.png")
        dst = os.path.join(SPRITES, "Armors", f"L{lvl:03d}_{cls}_{slug(name)}.png")
        if os.path.exists(src): shutil.copyfile(src, dst); n += 1
    print("gear sprites copied:", n)

# ================= C# 코드 생성 =================
def csstr(s): return '"' + s.replace('"', '\\"') + '"'

def gen_cs():
    lines = []
    lines.append("// 자동 생성 파일 — field/anim/gen_gamedata.py 로 재생성. 직접 수정 금지.")
    lines.append("// 소스: 옵시디언 Projects/Personal_Project 설계문서(아트 전면개편 계획 v3.0). 밸런스는 1차 드래프트.")
    lines.append("namespace Game.Data {")

    lines.append("public struct WeaponDef { public int level; public string name; public string kind; public int dmgLo; public int dmgHi; public string spritePath; }")
    lines.append("public struct GearDef { public int level; public string name; public string cls; public int def; public string spritePath; }")
    lines.append("public struct MonsterDef { public int tier; public string key; public string name; public string rank; public int minLv; public int hp; public int atk; public int def; public int exp; public int gold; public float weight; }")
    lines.append("public struct SkillDef { public string key; public int lv; public float cd; public string name; public string weapon; public float mult; public string kind; }")

    lines.append("public static class GameData {")
    lines.append(f"  public const int LEVEL_MAX = 100;")
    lines.append(f"  public const int CHANNEL_CAP = 10;")
    lines.append("  // xpToLevel(lv) = round(30 * lv^2 * (lv<=70 ? 1 : 1.05^(lv-70)))")
    lines.append("  public static long XpToLevel(int lv) { double v = 30.0 * lv * lv; if (lv > 70) v *= System.Math.Pow(1.05, lv - 70); return (long)System.Math.Round(v); }")

    lines.append("  public static readonly WeaponDef[] Swords = new WeaponDef[] {")
    for lvl, name, gid in SWORD:
        lo, hi = SWORD_DMG[lvl]
        lines.append(f"    new WeaponDef {{ level={lvl}, name={csstr(name)}, kind=\"sword\", dmgLo={lo}, dmgHi={hi}, spritePath={csstr('Weapons/L%03d_%s' % (lvl, slug(name)))} }},")
    lines.append("  };")
    lines.append("  public static readonly WeaponDef[] Staves = new WeaponDef[] {")
    for lvl, name, gid in STAFF:
        lo, hi = STAFF_DMG[lvl]
        lines.append(f"    new WeaponDef {{ level={lvl}, name={csstr(name)}, kind=\"staff\", dmgLo={lo}, dmgHi={hi}, spritePath={csstr('Weapons/L%03d_%s' % (lvl, slug(name)))} }},")
    lines.append("  };")

    lines.append("  public static readonly GearDef[] Helmets = new GearDef[] {")
    for lvl, name, cls, gid in HELMETS:
        lines.append(f"    new GearDef {{ level={lvl}, name={csstr(name)}, cls={csstr(cls)}, def={HELM_DEF[lvl]}, spritePath={csstr('Helmets/L%03d_%s_%s' % (lvl, cls, slug(name)))} }},")
    lines.append("  };")
    lines.append("  public static readonly GearDef[] Armors = new GearDef[] {")
    for lvl, name, cls, gid in ARMORS:
        lines.append(f"    new GearDef {{ level={lvl}, name={csstr(name)}, cls={csstr(cls)}, def={ARMOR_DEF[lvl]}, spritePath={csstr('Armors/L%03d_%s_%s' % (lvl, cls, slug(name)))} }},")
    lines.append("  };")

    lines.append("  public static readonly MonsterDef[] Monsters = new MonsterDef[] {")
    for i, key, name, rank, minlv in MONSTERS:
        hp, atk, dfn, exp, glo = mstat(i)
        weight = 0.0 if rank in ("midboss", "boss", "hidden") else max(1.0, 32.0 - i * 1.4)
        lines.append(f"    new MonsterDef {{ tier={i}, key={csstr(key)}, name={csstr(name)}, rank={csstr(rank)}, minLv={minlv}, hp={hp}, atk={atk}, def={dfn}, exp={exp}, gold={glo}, weight={weight}f }},")
    lines.append("  };")

    lines.append("  public static readonly SkillDef[] Skills = new SkillDef[] {")
    for key, lv, cd, name, weapon, mult, kind in SKILLS:
        lines.append(f"    new SkillDef {{ key={csstr(key)}, lv={lv}, cd={cd}f, name={csstr(name)}, weapon={csstr(weapon)}, mult={mult}f, kind={csstr(kind)} }},")
    lines.append("  };")

    lines.append("  // 강화 +1~+25: 누적 %(무기=데미지, 방어구=방어력), 성공률(%)")
    lines.append("  public static readonly int[] EnhPct = new int[] { 0, " + ", ".join(str(ENH_PCT[g]) for g in range(1, 26)) + " };")
    lines.append("  public static readonly float[] EnhSucc = new float[] { 100f, " + ", ".join(f"{ENH_SUCC[g]}f" for g in range(1, 26)) + " };")
    lines.append("  public const int ENH_MAX = 25;")

    lines.append("}}")
    os.makedirs(os.path.dirname(CS_OUT), exist_ok=True)
    with open(CS_OUT, "w") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote", CS_OUT)

if __name__ == "__main__":
    copy_gear_sprites()
    gen_cs()
    print("SWORD entries:", len(SWORD), "STAFF:", len(STAFF), "HELMETS:", len(HELMETS), "ARMORS:", len(ARMORS), "MONSTERS:", len(MONSTERS))
