// F안 베타 UI: 인벤토리·장비 / 강화 / 상점 / 보스 소환 + 서버 inv 응답 처리(f_round/server/API.md).
// uGUI, Canvas = Screen Space - Camera (1280x720 1:1 픽셀, 카메라 캡처에도 찍힘). 9-slice = MANIFEST §4 border(.meta).
// 서버 권위: 버튼은 요청만 보내고 화면은 항상 inv.state 로 다시 그린다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Game.Data;
using Game.Network;

namespace Game.Gameplay {

public class GameUI : MonoBehaviour {
    // Resources.Load 키(e_round/asset/MANIFEST.md §4) — 바뀌면 여기만. 강화 성공·실패 FX = A안(FINAL_r1), Resources 미반입이면 FX 없이 텍스트만
    const string KeyPanel = "Sprites/UI/shop_panel_9s", KeyBtn = "Sprites/UI/ui_btn_", KeySummonFrame = "Sprites/UI/ui_summon_frame_9s",
        KeyFxDestroy = "Sprites/FX/fx_enh_destroy", KeyFxSuccess = "Sprites/FX/fx_enh_success", KeyFxFail = "Sprites/FX/fx_enh_fail", KeyStone = "Sprites/UI/ui_enh_stone_basic", KeyPotion = "Sprites/UI/ui_potion", KeyFont = "Fonts/Galmuri11",
        KeyHudFrame = "Sprites/UI/ui_hud_bar_frame", KeyHudHp = "Sprites/UI/ui_hud_bar_fill_hp", KeyHudExp = "Sprites/UI/ui_hud_bar_fill_exp", KeyIcon = "Sprites/UI/ui_hud_icon_";
    const int BtnH = 30, FontSize = 12, PageSize = 10;   // Galmuri11 = 12px 그리드   // 버튼 최소 26px(MANIFEST 스크럼1) → 30

    PlayerController _pl; MonsterSpawner _sp; NetworkClient _net; Camera _cam;
    Font _font, _fontB; Sprite _panel, _btn, _btnDown, _btnOff;
    RectTransform _root, _skillHudRoot;
    readonly Dictionary<string, GameObject> _panels = new Dictionary<string, GameObject>();
    Text _toast, _invList, _invDetail, _enhInfo, _enhResult, _shopInfo, _sumInfo;
    readonly List<Button> _rows = new List<Button>();
    Button _bEquip, _bSell, _bEnh, _bS19, _bS20, _bSellMany, _bEnhTry, _bBuyOne, _bBuyTen;
    readonly List<Button> _checks = new List<Button>();
    readonly HashSet<int> _checked = new HashSet<int>();
    readonly Dictionary<string, Text> _cooldownHud = new Dictionary<string, Text>();
    Text _channel, _hpText, _expText, _lvText, _goldText, _potText; Image _hpFill, _expFill;
    Image _fx, _icon; Sprite[] _fxDestroy, _fxSuccess, _fxFail, _fxPlay; float _fxT = -1;
    int _sel, _page, _pendingSummon; float _toastT;
    bool _wasOnline, _summonAck, _summonSpawned;
    float _summonRetry;
    int _summonToken, _summonSeq, _summonAckSeq, _summonMonsterId;
    Button _bBreak, _bBreakMany, _bExpand, _bAuto, _bAutoBreak, _bBreakLevel;
    Action _savePreferences;
    InputField _chatInput;
    // WebGL: uGUI InputField 는 브라우저 IME(한글 조합)를 못 받고, 모달 패널이라 다른 메뉴를 열면 닫힘 → 채팅은 HTML 독(field/launcher.js gameChat)이 맡는다
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void WebChatToggle();
    [DllImport("__Internal")] static extern void WebChatLine(string line);
    const bool HtmlChat = true;
#else
    static void WebChatToggle() { }
    static void WebChatLine(string line) { }
    const bool HtmlChat = false;
#endif
    Text _chatHistory;
    Button _bPotionThreshold;
    readonly List<string> _chatLines = new List<string>();
    Text _bagInfo, _filterText, _autoText, _autoBreakText, _confirmText;
    readonly List<Button> _skillButtons = new List<Button>(), _mapButtons = new List<Button>();
    readonly Dictionary<string, Text> _statValues = new Dictionary<string, Text>();
    Text _allocationInfo, _growthDot;
    Button _allocateAuto, _resetStats;
    const int StatResetCost = 1000; // field/items.js STAT_RESET_COST
    readonly Dictionary<string, Text> _allocationValues = new Dictionary<string, Text>();
    readonly List<Button> _allocationButtons = new List<Button>();
    readonly List<RectTransform> _statCards = new List<RectTransform>();
    int _filterLevel = 10, _autoLevel, _autoBreakLevel;
    Action _confirmAction;
    float _skillHudTick;
    readonly List<Button> _menus = new List<Button>();
    readonly Dictionary<RectTransform, (Transform parent, Vector2 min, Vector2 max, Vector2 pivot, Vector2 pos, Vector2 size)> _desktop = new Dictionary<RectTransform, (Transform, Vector2, Vector2, Vector2, Vector2, Vector2)>();
    readonly Dictionary<string, ScrollRect> _mobileScroll = new Dictionary<string, ScrollRect>();
    int _screenWidth, _screenHeight;
    bool _mobile, _rowsChanged;

    public void Init(Camera cam, PlayerController pl, MonsterSpawner sp, NetworkClient net, Action savePreferences = null, bool isAdmin = false) {
        _cam = cam; _pl = pl; _sp = sp; _net = net; _savePreferences = savePreferences;
#if UNITY_WEBGL && !UNITY_EDITOR
        WebGLInput.captureAllKeyboardInput = false; // 게임은 키보드를 안 씀. 켜 두면 HTML 채팅 입력의 키를 Unity가 가로챔
#endif
        if (net != null) { net.OnInv += OnInv; net.OnChat += AddChat; net.OnSpawn += OnSummonSpawn; }
        // 한글 픽셀 폰트 Galmuri11(OFL, Resources/Fonts/OFL.txt). 없으면 OS 폰트
        _font = Resources.Load<Font>(KeyFont) ?? Font.CreateDynamicFontFromOSFont(new[] { "Apple SD Gothic Neo", "Malgun Gothic", "Noto Sans CJK KR", "Arial" }, FontSize);
        _fontB = Resources.Load<Font>(KeyFont + "-Bold") ?? _font;
        _panel = Resources.Load<Sprite>(KeyPanel);
        _btn = Resources.Load<Sprite>(KeyBtn + "normal"); _btnDown = Resources.Load<Sprite>(KeyBtn + "pressed"); _btnOff = Resources.Load<Sprite>(KeyBtn + "disabled");
        _fxDestroy = Frames(KeyFxDestroy); _fxSuccess = Frames(KeyFxSuccess); _fxFail = Frames(KeyFxFail);
        // 픽셀 아트와 맞추기: 동적 폰트 아틀라스 Point 필터(재생성될 때마다) + 정수 크기 + 1px 외곽선
        Font.textureRebuilt += PointFont; PointFont(_font); PointFont(_fontB);

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam; canvas.planeDistance = 1; canvas.sortingOrder = 200; canvas.pixelPerfect = true;
        var cs = gameObject.AddComponent<CanvasScaler>(); cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; cs.referenceResolution = new Vector2(1280, 720); cs.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand; cs.referencePixelsPerUnit = 40; // 스프라이트 PPU 40 과 맞춰야 9-slice 테두리가 1:1(기본 100이면 2.5배로 부풀어 가운데가 뭉개짐)
        gameObject.AddComponent<GraphicRaycaster>();
        if (FindAnyObjectByType<EventSystem>() == null) { var es = new GameObject("EventSystem"); es.AddComponent<EventSystem>(); es.AddComponent<InputSystemUIInputModule>(); }
        _root = (RectTransform)transform;

        // 우상단 메뉴
        string[] menu = { "inv:가방", "shop:상점", "sum:소환", "map:맵", "stats:성장", "chat:채팅" };
        if (isAdmin) menu = menu.Concat(new[] { "admin:관리" }).ToArray();
        for (int i = 0; i < menu.Length; i++) {
            var kv = menu[i].Split(':'); string id = kv[0];
            _menus.Add(Btn(_root, kv[1], new Vector2(-10 - (menu.Length - 1 - i) * 78, -10), new Vector2(74, BtnH), () => { if (id == "chat" && HtmlChat) WebChatToggle(); else Toggle(id); }, new Vector2(1, 1)));
        }
        _growthDot = Label(_menus[4].transform, "●", new Vector2(-6, -2), new Vector2(12, 16), TextAnchor.MiddleCenter, new Vector2(1, 1));
        _growthDot.color = Color.red; _growthDot.gameObject.SetActive(false);
        BuildHud();
        _channel = Label(_root, "", new Vector2(-10 - menu.Length * 78, -10), new Vector2(120, BtnH), TextAnchor.MiddleRight, new Vector2(1, 1), true); // 채널: 메뉴 왼쪽
        Label(_root, $"v{Application.version}", new Vector2(-10, 10), new Vector2(100, 20), TextAnchor.MiddleRight, new Vector2(1, 0), true);
        _toast = Label(_root, "", new Vector2(0, -64), new Vector2(600, 24), TextAnchor.MiddleCenter, new Vector2(0.5f, 1), true); // 상단 중앙, 보스 HP 바(위 8px + 48px) 아래

        // 인벤토리·장비
        var inv = Panel("inv", "가방 / 장비", new Vector2(600, 600));
        for (int r = 0; r < PageSize; r++) {
            int k = r;
            _checks.Add(Btn(inv, "", new Vector2(20, -50 - r * 32), new Vector2(30, BtnH), () => Check(k), new Vector2(0, 1)));   // 다중 선택 체크
            _rows.Add(Btn(inv, "", new Vector2(54, -50 - r * 32), new Vector2(226, BtnH), () => Select(k), new Vector2(0, 1), TextAnchor.MiddleLeft));
        }
        Btn(inv, "◀", new Vector2(20, -380), new Vector2(60, BtnH), () => { _page = Mathf.Max(0, _page - 1); Redraw(); }, new Vector2(0, 1));
        Btn(inv, "▶", new Vector2(220, -380), new Vector2(60, BtnH), () => { _page++; Redraw(); }, new Vector2(0, 1));
        _invList = Label(inv, "", new Vector2(86, -380), new Vector2(128, BtnH), TextAnchor.MiddleCenter, new Vector2(0, 1));
        _invDetail = Label(inv, "", new Vector2(292, -50), new Vector2(132, 200), TextAnchor.UpperLeft, new Vector2(0, 1));
        _bEquip = Btn(inv, "장착", new Vector2(292, -260), new Vector2(128, BtnH), () => ItemReq("equip"), new Vector2(0, 1));
        _bSell = Btn(inv, "판매", new Vector2(292, -296), new Vector2(128, BtnH), () => ItemReq("sell"), new Vector2(0, 1));
        _bEnh = Btn(inv, "강화하기", new Vector2(292, -332), new Vector2(128, BtnH), () => Toggle("enh"), new Vector2(0, 1));
        _bSellMany = Btn(inv, "선택 판매", new Vector2(292, -368), new Vector2(128, BtnH), SellChecked, new Vector2(0, 1));

        _bBreak = Btn(inv, "분해", new Vector2(440, -260), new Vector2(140, BtnH), () => Confirm("선택 장비를 분해할까? 복구할 수 없어.", () => ItemReq("disassemble")), new Vector2(0, 1));
        _bBreakMany = Btn(inv, "선택 분해", new Vector2(440, -296), new Vector2(140, BtnH), () => Confirm($"체크한 {_checked.Count}개를 분해할까? 장착 장비는 제외돼.", () => { if (Online) _net.DisassembleMany(_checked.ToArray()); }), new Vector2(0, 1));
        _bagInfo = Label(inv, "", new Vector2(20, -422), new Vector2(560, 28), TextAnchor.MiddleLeft, new Vector2(0, 1));
        _bExpand = Btn(inv, "가방 확장 +1", new Vector2(440, -416), new Vector2(140, BtnH), () => Send(new InvReq { type = "expand" }), new Vector2(0, 1));
        _filterText = Label(inv, "", new Vector2(20, -454), new Vector2(155, BtnH), TextAnchor.MiddleLeft, new Vector2(0, 1));
        Btn(inv, "−", new Vector2(174, -454), new Vector2(44, BtnH), () => { _filterLevel = Mathf.Max(0, _filterLevel - 10); Redraw(); }, new Vector2(0, 1));
        Btn(inv, "+", new Vector2(220, -454), new Vector2(44, BtnH), () => { _filterLevel = Mathf.Min(100, _filterLevel + 10); Redraw(); }, new Vector2(0, 1));
        _bBreakLevel = Btn(inv, "레벨 이하 분해", new Vector2(280, -454), new Vector2(144, BtnH), () => Confirm($"Lv{_filterLevel} 이하 장비를 전부 분해할까? 장착 장비는 제외돼.", () => Send(new InvReq { type = "disassemble", level = _filterLevel })), new Vector2(0, 1));
        _bAuto = Btn(inv, "", new Vector2(20, -492), new Vector2(155, BtnH), () => Send(new InvReq { type = "auto_sell", enabled = !(_pl.Inv?.autoSell ?? false), level = _autoLevel }), new Vector2(0, 1));
        _autoText = Label(inv, "", new Vector2(180, -492), new Vector2(185, BtnH), TextAnchor.MiddleLeft, new Vector2(0, 1));
        Btn(inv, "−", new Vector2(366, -492), new Vector2(44, BtnH), () => SetAutoLevel(-10), new Vector2(0, 1));
        Btn(inv, "+", new Vector2(412, -492), new Vector2(44, BtnH), () => SetAutoLevel(10), new Vector2(0, 1));

        _bAutoBreak = Btn(inv, "", new Vector2(20, -530), new Vector2(155, BtnH), () => {
            if (_pl.Inv?.autoDisassemble == true) Send(new InvReq { type = "auto_disassemble", enabled = false, level = _autoBreakLevel });
            else Confirm($"새 드롭과 보류 장비 중 Lv{_autoBreakLevel} 이하를 자동 분해해. 복구할 수 없고 자동 판매는 꺼져.",
                () => Send(new InvReq { type = "auto_disassemble", enabled = true, level = _autoBreakLevel }));
        }, new Vector2(0, 1));
        _autoBreakText = Label(inv, "", new Vector2(180, -530), new Vector2(185, BtnH), TextAnchor.MiddleLeft, new Vector2(0, 1));
        Btn(inv, "−", new Vector2(366, -530), new Vector2(44, BtnH), () => SetAutoBreakLevel(-10), new Vector2(0, 1));
        Btn(inv, "+", new Vector2(412, -530), new Vector2(44, BtnH), () => SetAutoBreakLevel(10), new Vector2(0, 1));
        Label(inv, "자동 처리는 새 드롭·보류 장비만 적용 (판매 / 분해 중 하나)", new Vector2(20, -568), new Vector2(560, 24), TextAnchor.MiddleLeft, new Vector2(0, 1));

        if (isAdmin) {
            var admin = Panel("admin", "관리 / 채널", new Vector2(420, 470));
            Label(admin, "봇은 부하 시험용이며 실제 플레이어가 아니야.", new Vector2(20, -50), new Vector2(380, 40), TextAnchor.MiddleLeft, new Vector2(0, 1));
            Btn(admin, "봇 +1", new Vector2(20, -100), new Vector2(180, 40), () => { if (Online) _net.SendBot(1); }, new Vector2(0, 1));
            Btn(admin, "봇 −1", new Vector2(220, -100), new Vector2(180, 40), () => { if (Online) _net.SendBot(-1); }, new Vector2(0, 1));
            Btn(admin, "홈 채널", new Vector2(20, -160), new Vector2(180, 40), () => { if (Online) _net.SwitchChannel(0); }, new Vector2(0, 1));
            Btn(admin, "채널 2", new Vector2(220, -160), new Vector2(180, 40), () => { if (Online) _net.SwitchChannel(1); }, new Vector2(0, 1));
            // 관리자 도구(서버가 관리자 세션을 매번 확인). 결과는 inv 응답으로 와서 OnInv 가 그린다.
            Btn(admin, "골드 +1,000", new Vector2(20, -210), new Vector2(180, 40), () => Send(new InvReq { type = "admin_gold", qty = 1000 }), new Vector2(0, 1));
            Btn(admin, "골드 +100,000", new Vector2(220, -210), new Vector2(180, 40), () => Send(new InvReq { type = "admin_gold", qty = 100000 }), new Vector2(0, 1));
            Btn(admin, "경험치 +10%", new Vector2(20, -260), new Vector2(180, 40), () => Send(new InvReq { type = "admin_exp", qty = 10 }), new Vector2(0, 1));
            Btn(admin, "레벨 +1", new Vector2(220, -260), new Vector2(180, 40), () => Send(new InvReq { type = "admin_exp", qty = 100 }), new Vector2(0, 1));
            Btn(admin, "레벨 +10", new Vector2(20, -310), new Vector2(180, 40), () => Send(new InvReq { type = "admin_level", qty = 10 }), new Vector2(0, 1));
            Btn(admin, "강화석 +100", new Vector2(220, -310), new Vector2(180, 40), () => Send(new InvReq { type = "admin_item", item = "stone", qty = 100 }), new Vector2(0, 1));
            Btn(admin, "포션 +100", new Vector2(20, -360), new Vector2(180, 40), () => Send(new InvReq { type = "admin_item", item = "potion", qty = 100 }), new Vector2(0, 1));
            Btn(admin, "일반 몬스터", new Vector2(220, -360), new Vector2(180, 40), () => AdminSpawn(_pl.Inv?.zone == "C" ? 13 : _pl.Inv?.zone == "B" ? 7 : 1), new Vector2(0, 1));
            Btn(admin, "드래곤", new Vector2(20, -410), new Vector2(120, 40), () => AdminSpawn(19), new Vector2(0, 1));
            Btn(admin, "염제", new Vector2(150, -410), new Vector2(120, 40), () => AdminSpawn(20), new Vector2(0, 1));
            Btn(admin, "히든", new Vector2(280, -410), new Vector2(120, 40), () => AdminSpawn(21), new Vector2(0, 1));
        }

        var confirm = Panel("confirm", "확인", new Vector2(440, 160));
        _confirmText = Label(confirm, "", new Vector2(20, -48), new Vector2(400, 46), TextAnchor.MiddleCenter, new Vector2(0, 1));
        Btn(confirm, "취소", new Vector2(60, -108), new Vector2(140, BtnH), () => { _confirmAction = null; Toggle("confirm"); }, new Vector2(0, 1));
        Btn(confirm, "진행", new Vector2(240, -108), new Vector2(140, BtnH), () => { var action = _confirmAction; _confirmAction = null; Toggle("confirm"); action?.Invoke(); }, new Vector2(0, 1));

        // 강화
        var enh = Panel("enh", "강화", new Vector2(360, 300));
        _enhInfo = Label(enh, "", new Vector2(20, -50), new Vector2(320, 110), TextAnchor.UpperLeft, new Vector2(0, 1));
        _bEnhTry = Btn(enh, "강화 시도", new Vector2(20, -170), new Vector2(150, BtnH), () => ItemReq("enhance"), new Vector2(0, 1));
        _enhResult = Label(enh, "", new Vector2(20, -210), new Vector2(200, 60), TextAnchor.UpperLeft, new Vector2(0, 1));
        // 오른쪽 빈 칸: 대상 장비 아이콘(원본 1x) + 그 위 결과 FX(같은 중심)
        _icon = new GameObject("EnhIcon").AddComponent<Image>(); _icon.transform.SetParent(enh, false); _icon.raycastTarget = false;
        var ir = _icon.rectTransform; ir.anchorMin = ir.anchorMax = new Vector2(1, 1); ir.pivot = new Vector2(0.5f, 0.5f); ir.anchoredPosition = new Vector2(-90, -190);
        _fx = new GameObject("EnhFx").AddComponent<Image>(); _fx.transform.SetParent(enh, false);
        var fr = _fx.rectTransform; fr.anchorMin = fr.anchorMax = new Vector2(1, 1); fr.pivot = new Vector2(0.5f, 0.5f); fr.anchoredPosition = ir.anchoredPosition;
        _fx.enabled = false; _fx.raycastTarget = false;

        // 상점
        var shop = Panel("shop", "상점 / 스킬북", new Vector2(600, 550));
        _shopInfo = Label(shop, "", new Vector2(20, -50), new Vector2(280, 60), TextAnchor.UpperLeft, new Vector2(0, 1));
        _bBuyOne = Btn(shop, "포션 1개", new Vector2(20, -120), new Vector2(130, BtnH), () => Buy(1), new Vector2(0, 1));
        _bBuyTen = Btn(shop, "포션 10개", new Vector2(166, -120), new Vector2(130, BtnH), () => Buy(10), new Vector2(0, 1));

        for (int i = 0; i < GameData.Skills.Length; i++) {
            string key = GameData.Skills[i].key;
            _skillButtons.Add(Btn(shop, "", new Vector2(20, -168 - i * 34), new Vector2(560, BtnH), () => LearnSkill(key), new Vector2(0, 1), TextAnchor.MiddleLeft));
        }

        var map = Panel("map", "맵 선택", new Vector2(360, 230));
        for (int i = 0; i < 3; i++) {
            string zone = ((Game.Rendering.Zone)i).ToString();
            _mapButtons.Add(Btn(map, "", new Vector2(20, -54 - i * 48), new Vector2(320, 40), () => Send(new InvReq { type = "map", zone = zone }), new Vector2(0, 1)));
        }
        var stats = Panel("stats", "캐릭터 능력치", new Vector2(640, 640));
        stats.anchoredPosition = Vector2.zero;
        StatCard(stats, "기본 정보", new Vector2(20, -52), 294, new[] {
            "class:직업", "level:레벨", "hp:HP", "exp:경험치", "gold:골드", "stones:강화석", "potions:포션" });
        StatCard(stats, "전투 능력치", new Vector2(326, -52), 294, new[] {
            "attack:기본 공격력", "fixed:스킬 고정 공격력", "defense:방어력", "reduction:피해 경감", "range:공격 사거리", "critical:치명타 확률", "threshold:포션 사용 기준" });
        StatCard(stats, "스킬 상태", new Vector2(20, -302), 600,
            GameData.Skills.Select(skill => "skill_" + skill.key + ":" + skill.name).ToArray());

        Btn(stats, "능력치 배분", new Vector2(-52, -12), new Vector2(128, BtnH), () => Show("allocate"), new Vector2(1, 1));
        var allocate = Panel("allocate", "능력치 배분", new Vector2(480, 420));
        _allocationInfo = Label(allocate, "", new Vector2(20, -50), new Vector2(440, 56), TextAnchor.UpperLeft, new Vector2(0, 1));
        string[] attributes = { "str:STR · 검 공격", "dex:DEX · 방어력", "int:INT · 지팡이 공격", "luk:LUK · 치명타" };
        for (int i = 0; i < attributes.Length; i++) {
            var kv = attributes[i].Split(':'); string key = kv[0];
            var row = Img(allocate, null, new Vector2(20, -110 - i * 46), new Vector2(440, 44));
            row.color = new Color(0.94f, 0.92f, 0.85f); _statCards.Add(row.rectTransform);
            Label(row.transform, kv[1], new Vector2(8, 0), new Vector2(130, 44), TextAnchor.MiddleLeft, new Vector2(0, 1));
            _allocationValues[key] = Label(row.transform, "", new Vector2(-136, 0), new Vector2(44, 44), TextAnchor.MiddleRight, new Vector2(1, 1));
            _allocationButtons.Add(Btn(row.transform, "+1", new Vector2(-72, 0), new Vector2(60, 44), () => Allocate(key, 1), new Vector2(1, 1)));
            _allocationButtons.Add(Btn(row.transform, "+10", new Vector2(-8, 0), new Vector2(60, 44), () => Allocate(key, 10), new Vector2(1, 1)));
        }
        _allocateAuto = Btn(allocate, "직업에 맞춰 남은 포인트 전부 배분", new Vector2(20, -306), new Vector2(440, 44), () => Confirm("남은 포인트를 현재 무기의 주 능력치에 전부 배분해.", () => Send(new InvReq { type = "allocate_auto" })), new Vector2(0, 1));
        _resetStats = Btn(allocate, $"능력치 초기화 ({StatResetCost:N0}G)", new Vector2(20, -356), new Vector2(440, 44), () => Confirm($"배분한 능력치를 전부 되돌려. {StatResetCost:N0}G가 들어.", () => Send(new InvReq { type = "reset_stats" })), new Vector2(0, 1));

        var chat = Panel("chat", HtmlChat ? "포션 설정" : "채팅 / 포션 설정", new Vector2(480, HtmlChat ? 120 : 390));
        if (!HtmlChat) {
            _chatHistory = Label(chat, "", new Vector2(20, -50), new Vector2(440, 180), TextAnchor.UpperLeft, new Vector2(0, 1));
            var input = new GameObject("ChatInput", typeof(RectTransform), typeof(Image), typeof(InputField));
            input.transform.SetParent(chat, false);
            var inputRect = (RectTransform)input.transform;
            inputRect.anchorMin = inputRect.anchorMax = inputRect.pivot = new Vector2(0, 1);
            inputRect.anchoredPosition = new Vector2(20, -240); inputRect.sizeDelta = new Vector2(320, 44);
            input.GetComponent<Image>().color = new Color(1, 0.95f, 0.86f);
            _chatInput = input.GetComponent<InputField>(); _chatInput.characterLimit = 120; // 서버 상한(server.js chat)
            _chatInput.textComponent = Label(input.transform, "", Vector2.zero, new Vector2(300, 44), TextAnchor.MiddleLeft, new Vector2(0.5f, 0.5f));
            _chatInput.placeholder = Label(input.transform, "메시지 입력", Vector2.zero, new Vector2(300, 44), TextAnchor.MiddleLeft, new Vector2(0.5f, 0.5f));
            _chatInput.onSubmit.AddListener(_ => { SendChat(); _chatInput.ActivateInputField(); });
            Btn(chat, "보내기", new Vector2(350, -240), new Vector2(110, 44), SendChat, new Vector2(0, 1));
        }
        _bPotionThreshold = Btn(chat, "", new Vector2(20, HtmlChat ? -56 : -302), new Vector2(440, 44), () => { _pl.PotionHpThresholdPercent = _pl.PotionHpThresholdPercent >= 60 ? 20 : _pl.PotionHpThresholdPercent + 10; _savePreferences?.Invoke(); Redraw(); }, new Vector2(0, 1));

        // 보스 소환
        var sum = Panel("sum", "보스 소환", new Vector2(320, 200), KeySummonFrame);
        _sumInfo = Label(sum, "", new Vector2(20, -50), new Vector2(280, 60), TextAnchor.UpperLeft, new Vector2(0, 1));
        _bS19 = Btn(sum, "드래곤 소환", new Vector2(20, -120), new Vector2(130, BtnH), () => Summon(19), new Vector2(0, 1));
        _bS20 = Btn(sum, "염제 소환", new Vector2(166, -120), new Vector2(130, BtnH), () => Summon(20), new Vector2(0, 1));
        Redraw();
        CaptureLayout();
        ResponsiveLayout();
    }

    static Sprite[] Frames(string dir) => Resources.LoadAll<Sprite>(dir).OrderBy(s => s.name).ToArray();

    void PointFont(Font f) { if ((f == _font || f == _fontB) && f.material.mainTexture != null) f.material.mainTexture.filterMode = FilterMode.Point; }

    void OnDestroy() {
        Font.textureRebuilt -= PointFont;
        if (_net != null) { _net.OnInv -= OnInv; _net.OnChat -= AddChat; _net.OnSpawn -= OnSummonSpawn; }
    }

    // ── 서버 응답 ──
    public void OnInv(InvMsg m) {
        if (m == null) return;
        if (m.req == "summon" && (_pendingSummon <= 0 || m.seq != _summonSeq)) return;
        if (m.req == "summon_ack" && (!_summonAck || m.seq != _summonAckSeq)) return;
        if (m.state != null) _pl.ApplyState(m.state);
        if (m.state != null) { _autoLevel = m.state.autoSellLevel; _autoBreakLevel = m.state.autoDisassembleLevel; }
        _checked.RemoveWhere(uid => _pl.Inv?.inv?.Any(it => it.uid == uid && !Equipped(it)) != true);
        if (!m.ok) { if (m.req == "summon") _pendingSummon = 0; if (m.req == "summon_ack") { if (m.code == "save_failed") _summonRetry = 3; else { _pendingSummon = 0; _summonMonsterId = 0; _summonAck = false; _summonRetry = 0; } } Toast(ErrText(m.code), new Color(1f, 0.5f, 0.4f)); Redraw(); return; }
        switch (m.req) {
            case "kill":
                if (m.drop == null) break;
                var parts = new List<string> { $"+{m.drop.gold}G", $"+{m.drop.exp}exp" };
                if (m.drop.stones > 0) parts.Add($"강화석 +{m.drop.stones}");
                if (m.drop.sold > 0) parts.Add($"자동 판매 +{m.drop.sold:N0}G");
                else if (m.drop.disassembled > 0) parts.Add($"자동 분해 · 강화석 +{m.drop.disassembled}");
                else if (m.drop.item != null && m.drop.item.uid > 0) parts.Add($"{(m.state?.HasPendingDrop == true && m.state.pendingDrop.uid == m.drop.item.uid ? "보류" : "획득")}: {m.drop.item.name}");
                if (m.drop.levelUp > 0) parts.Add($"레벨 업! Lv{m.state.level}");
                Toast(string.Join("  ", parts), Color.white);
                break;
            case "potion": _pl.DrinkPotion(); break;
            case "bot": Toast($"봇 {m.bots}마리 · 접속 {m.pop}/{m.cap}", Color.white); break;
            case "admin_gold": case "admin_item": case "admin_level": Toast("관리자 지급 완료", Color.white); break;
            case "admin_exp": Toast(m.drop != null && m.drop.levelUp > 0 ? $"레벨 업! Lv{m.state.level}" : "경험치 지급 완료", Color.white); break;
            case "admin_spawn": Toast(_sp != null && _sp.SpawnTier(_adminTier, _pl.transform.position.x + 3f) ? "몬스터 소환 완료" : "지금은 소환할 수 없습니다", Color.white); break;
            case "enhance": ShowEnh(m.enh); break;
            case "learn": Toast("스킬 습득 완료", Color.white); break;
            case "disassemble": Toast($"{m.disassemble?.removed?.Length ?? 0}개 분해 · 강화석 +{m.disassemble?.stones ?? 0}" + ((m.disassemble?.skipped?.Length ?? 0) > 0 ? $" · 제외 {m.disassemble.skipped.Length}개" : ""), Color.white); _checked.Clear(); break;
            case "expand": Toast("가방 확장 완료", Color.white); break;
            case "auto_sell": Toast("자동 판매 설정 저장" + (m.drop?.sold > 0 ? $" · 보류 장비 판매 +{m.drop.sold:N0}G" : ""), Color.white); break;
            case "auto_disassemble": Toast("자동 분해 설정 저장" + (m.drop?.disassembled > 0 ? $" · 보류 장비 분해 +{m.drop.disassembled}강화석" : ""), Color.white); break;
            case "map": Toast("맵 변경 완료", Color.white); Toggle("map"); break;
            case "allocate_stat": case "allocate_auto": Toast("능력치 배분 완료", Color.white); break;
            case "reset_stats": Toast($"능력치 초기화 완료 · -{StatResetCost:N0}G", Color.white); break;
            case "buy": Toast("구매 완료", Color.white); break;
            case "sell":
                int n = m.sell?.sold?.Length ?? 1; foreach (var u in m.sell?.sold ?? new int[0]) _checked.Remove(u);
                Toast(n > 1 || (m.sell?.skipped?.Length ?? 0) > 0 ? $"{n}개 판매 +{m.sell.gold}G" + (m.sell.skipped.Length > 0 ? $" (건너뜀 {m.sell.skipped.Length})" : "") : "판매 완료", Color.white); break;
            case "summon":
                if (_pendingSummon <= 0) break;
                _summonToken = m.state?.pendingSummon?.token ?? 0;
                _summonSpawned = false;
                if (_sp != null && _sp.SpawnSummoned(_pendingSummon)) {
                    _summonMonsterId = _sp.Boss.RegistrationId;
                    if (_summonMonsterId == 0) SendSummonAck(false);
                } else SendSummonAck(false);
                break;
            case "summon_ack":
                _pendingSummon = 0; _summonMonsterId = 0; _summonAck = false; _summonRetry = 0;
                Toast(_summonSpawned ? "보스 소환 완료" : "소환하지 못해 처치 수를 보존했습니다", Color.white);
                break;
        }
        Redraw();
    }

    void OnSummonSpawn(SpawnMsg message) {
        if (_pendingSummon <= 0 || _summonAck || _summonMonsterId == 0 || message.monsterId != _summonMonsterId) return;
        SendSummonAck(message.ok && !string.IsNullOrEmpty(message.receipt) && message.tier == _pendingSummon);
    }

    void SendSummonAck(bool spawned) {
        _summonSpawned = spawned; _summonAck = true;
        _summonAckSeq = Send(new InvReq { type = "summon_ack", tier = _pendingSummon, token = _summonToken, spawned = spawned });
    }

    static string ErrText(string c) => c switch {
        "no_stones" => "강화석이 부족합니다", "no_gold" => "골드가 부족합니다", "max" => "이미 +25", "level" => "레벨이 부족합니다",
        "equipped" => "장착 중인 장비는 판매·분해할 수 없습니다", "class" => "현재 무기와 다른 직업의 방어구입니다", "owned" => "이미 습득한 스킬입니다", "unavailable" => "아직 구매할 수 없습니다", "no_potion" => "포션이 없습니다", "not_enough" => "처치 수가 부족합니다",
        "bag_full" => "가방을 정리하면 보관된 드롭을 회수합니다",
        "save_failed" => "저장에 실패해 요청을 취소했습니다",
        "bad_stat" => "잘못된 능력치입니다", "no_stats" => "초기화할 능력치가 없습니다", "no_points" => "배분할 포인트가 부족합니다", "bad_qty" => "잘못된 배분 수량입니다",
        "bad_slot" => "무기는 해제할 수 없습니다", "none_sold" => "팔 수 있는 장비가 없습니다(장착 중 제외)", "empty" => "선택한 장비가 없습니다", "rate" => "처치 보고가 너무 빠름",
        "forbidden" => "관리자만 쓸 수 있습니다", "full" => "채널이 가득 차서 봇을 못 늘립니다", "no_bot" => "지울 봇이 없습니다", "wrong_zone" => "이 존에서는 소환할 수 없습니다", "too_strong" => "레벨이 낮아 소환할 수 없습니다", _ => $"실패: {c}",
    };

    void ShowEnh(InvEnh e) {
        if (e == null) return;
        string t = e.result switch { "success" => $"성공! +{e.from} → +{e.to}", "keep" => $"실패 (+{e.from} 유지)", "down" => $"실패 +{e.from} → +{e.to}", "destroy" => "파괴되었습니다", _ => e.result };
        _enhResult.text = t;
        _enhResult.color = e.result == "success" ? new Color(0.1f, 0.45f, 0.1f) : e.result == "destroy" ? new Color(0.7f, 0.08f, 0.05f) : e.result == "down" ? new Color(0.6f, 0.3f, 0.05f) : Ink;
        _fxPlay = e.result == "success" ? _fxSuccess : e.result == "down" ? _fxFail : e.result == "destroy" ? _fxDestroy : null; // keep = FX 없음
        _fxT = _fxPlay != null && _fxPlay.Length > 0 ? 0 : -1; _fx.enabled = false;
        if (_fxPlay == _fxSuccess) _fx.transform.SetSiblingIndex(_icon.transform.GetSiblingIndex()); else _fx.transform.SetAsLastSibling(); // 성공 = 아이콘 뒤, 실패·파괴 = 아이콘 위
        if (e.result == "destroy") _sel = 0;
    }

    // ── 요청 ──
    bool Online => _net != null && _net.Connected && _net.Joined;
    int Send(InvReq r) { if (r.type == "map" && _pl.PendingKillRewards) { Toast("처치 보상 저장이 끝난 뒤 이동할 수 있습니다", Color.white); return 0; } if (Online) { if (r.type == "map") _pl.GameplayReady = false; return _net.Request(r); } Toast(_net != null && _net.Connected ? "서버 접속 중" : "서버 미접속", new Color(1f, 0.5f, 0.4f)); return 0; }
    InvItem Sel => _pl.Inv?.inv == null ? null : _pl.Inv.inv.FirstOrDefault(x => x.uid == _sel);
    bool Equipped(InvItem it) { var e = _pl.Inv?.equip; return e != null && it != null && (e.weapon == it.uid || e.helmet == it.uid || e.armor == it.uid); }
    void ItemReq(string type) {
        var it = Sel; if (it == null) { Toast("장비를 고르세요", Color.white); return; }
        if (type == "equip") Send(new InvReq { type = "equip", slot = it.slot, uid = Equipped(it) ? 0 : it.uid }); // 장착 중 방어구 = 해제
        else Send(new InvReq { type = type, uid = it.uid });
    }
    void LearnSkill(string key) {
        var skill = GameData.Skills.First(s => s.key == key);
        Action learn = () => Send(new InvReq { type = "learn", item = key });
        if (skill.weapon != _pl.WeaponKind) Confirm("현재 무기와 다른 스킬이야. 해당 무기를 장착해야 발동해. 구매할까?", learn);
        else learn();
    }
    void AddChat(string name, string text) {
        if (HtmlChat) { WebChatLine($"{name}: {text}"); return; }
        _chatLines.Add($"{name}: {text}"); if (_chatLines.Count > 8) _chatLines.RemoveAt(0); _chatHistory.text = string.Join("\n", _chatLines);
    }
    void SendChat() { if (SendChatText(_chatInput.text)) _chatInput.text = ""; }
    bool SendChatText(string text) { if (!Online || string.IsNullOrWhiteSpace(text)) return false; _net.SendChat(text); return true; }
    // HTML 채팅 독 → SendMessage('GameUI','OnHtmlChat',text)
    public void OnHtmlChat(string text) { if (!SendChatText(text)) Toast("오프라인이라 보낼 수 없습니다", new Color(1f, 0.5f, 0.4f)); }
    void Confirm(string text, Action action) { _confirmText.text = text; _confirmAction = action; Show("confirm"); }
    void SetAutoLevel(int delta) { _autoLevel = Mathf.Clamp(_autoLevel + delta, 0, 100); Send(new InvReq { type = "auto_sell", enabled = _pl.Inv?.autoSell ?? false, level = _autoLevel }); }
    void SetAutoBreakLevel(int delta) {
        int level = Mathf.Clamp(_autoBreakLevel + delta, 0, 100);
        if (_pl.Inv?.autoDisassemble == true) Confirm($"자동 분해 기준을 Lv{level}로 변경해. 보류 장비도 적용돼.", () => Send(new InvReq { type = "auto_disassemble", enabled = true, level = level }));
        else Send(new InvReq { type = "auto_disassemble", enabled = false, level = level });
    }
    void Allocate(string key, int qty) => Send(new InvReq { type = "allocate_stat", item = key, qty = qty });
    void Buy(int n) => Send(new InvReq { type = "buy", item = "potion", qty = n });
    int _adminTier;
    void AdminSpawn(int tier) { _adminTier = tier; Send(new InvReq { type = "admin_spawn", tier = tier }); }

    void Summon(int tier) {
        if (_pendingSummon > 0) return;
        if (_sp != null && !_sp.CanSummon(tier)) { Toast("지금 이 존에서는 소환할 수 없습니다", new Color(1f, 0.5f, 0.4f)); return; }
        _summonMonsterId = 0; _pendingSummon = tier; _summonSeq = Send(new InvReq { type = "summon", tier = tier });
    }
    void Check(int row) { var list = Items(); int i = _page * PageSize + row; if (i >= list.Length || Equipped(list[i])) return; int u = list[i].uid; if (!_checked.Remove(u)) _checked.Add(u); Redraw(); }
    void SellChecked() { if (_checked.Count == 0) { Toast("체크한 장비가 없습니다", Color.white); return; } if (Online) _net.SellMany(_checked.ToArray()); else Toast("서버 미접속", new Color(1f, 0.5f, 0.4f)); }
    void Select(int row) { var list = Items(); int i = _page * PageSize + row; if (i < list.Length) _sel = list[i].uid; Redraw(); }
    InvItem[] Items() => (_pl.Inv?.inv ?? Array.Empty<InvItem>()).OrderByDescending(Equipped).ToArray();

    public void Toggle(string id) { foreach (var kv in _panels) kv.Value.SetActive(kv.Key == id && !kv.Value.activeSelf); Redraw(); }
    public void Show(string id) { foreach (var kv in _panels) kv.Value.SetActive(kv.Key == id); Redraw(); }
    public void SelectUid(int uid) { _sel = uid; Redraw(); }
    void Toast(string s, Color c) { _toast.text = s; _toast.color = c; _toastT = 3f; }

    // ── 그리기(항상 state 에서) ──
    void Redraw() {
        var st = _pl.Inv; var items = Items();
        bool online = Online;
        int pages = Mathf.Max(1, (items.Length + PageSize - 1) / PageSize); _page = Mathf.Clamp(_page, 0, pages - 1);
        for (int r = 0; r < PageSize; r++) {
            int i = _page * PageSize + r; var b = _rows[r]; bool has = i < items.Length;
            if (b.gameObject.activeSelf != has) _rowsChanged = true;
            b.gameObject.SetActive(has); _checks[r].gameObject.SetActive(has); if (!has) continue;
            var it = items[i];
            _checks[r].GetComponentInChildren<Text>(true).text = _checked.Contains(it.uid) ? "V" : "";
            _checks[r].image.color = _checked.Contains(it.uid) ? new Color(1f, 0.72f, 0.35f) : Color.white;
            b.GetComponentInChildren<Text>(true).text = $"{(Equipped(it) ? "[E] " : "")}{it.name} +{it.enh}  Lv{it.level}";
            b.image.color = Equipped(it) ? new Color(0.65f, 0.9f, 0.65f) : it.uid == _sel ? new Color(1f, 0.72f, 0.35f) : Color.white;
            _checks[r].interactable = online && !Equipped(it);
        }
        _invList.text = $"{_page + 1}/{pages}  ({items.Length}/{(st != null ? st.invCap : 0)})";
        var s = Sel;
        _invDetail.text = s == null ? "장비를 고르세요" : $"{s.name} +{s.enh}\n{SlotName(s.slot)} Lv{s.level}\n{ItemStats(s)}\n판매가 {s.sell}G\n{(Equipped(s) ? "장착 중" : "")}";
        _bEquip.GetComponentInChildren<Text>(true).text = s != null && Equipped(s) && s.slot != "weapon" ? "해제" : "장착";
        _bEquip.interactable = online && s != null && !(Equipped(s) && s.slot == "weapon");
        _bSell.interactable = online && s != null && !Equipped(s);
        _bBreak.interactable = online && s != null && !Equipped(s);
        _bBreakMany.interactable = online && _checked.Count > 0;
        _bBreakLevel.interactable = online && items.Any(it => !Equipped(it) && it.level <= _filterLevel);
        _bExpand.interactable = online && st != null && st.invCap < 200 && st.gold >= st.expandCost;
        _bagInfo.text = $"가방 {items.Length}/{st?.invCap ?? 60}칸 · 강화석 {st?.stones ?? 0} · 확장 {st?.expandCost ?? 0}G";
        _filterText.text = $"분해 기준 Lv{_filterLevel}";
        _bAuto.GetComponentInChildren<Text>(true).text = "자동 판매 " + (st?.autoSell == true ? "켜짐" : "꺼짐");
        _bAuto.interactable = online;
        _autoText.text = $"드롭 장비 Lv{_autoLevel} 이하";
        _bAutoBreak.GetComponentInChildren<Text>(true).text = "자동 분해 " + (st?.autoDisassemble == true ? "켜짐" : "꺼짐");
        _bAutoBreak.interactable = online;
        _autoBreakText.text = $"드롭 장비 Lv{_autoBreakLevel} 이하";
        for (int i = 0; i < _mapButtons.Count; i++) {
            string zone = ((Game.Rendering.Zone)i).ToString();
            int min = st?.mapMinLevels != null && i < st.mapMinLevels.Length ? st.mapMinLevels[i] : -1;
            _mapButtons[i].GetComponentInChildren<Text>(true).text = $"맵 {zone}" + (st?.zone == zone ? " · 현재" : min < 0 ? " · 조건 확인 중" : $" · Lv{min}");
            _mapButtons[i].interactable = online && min >= 0 && _pl.Level >= min && st?.zone != zone;
        }
        _allocationInfo.text = $"남은 포인트 {st?.statPoints ?? 0} · 레벨당 5포인트\nSTR/INT 공격 +2% · DEX 방어 +1 · LUK 치명타 +0.1%";
        _allocationValues["str"].text = (st?.str ?? 0).ToString();
        _allocationValues["dex"].text = (st?.dex ?? 0).ToString();
        _allocationValues["int"].text = (st?.intelligence ?? 0).ToString();
        _allocationValues["luk"].text = (st?.luk ?? 0).ToString();
        for (int i = 0; i < _allocationButtons.Count; i++) _allocationButtons[i].interactable = online && (st?.statPoints ?? 0) >= (i % 2 == 0 ? 1 : 10);
        _growthDot.gameObject.SetActive((st?.statPoints ?? 0) > 0);
        _allocateAuto.interactable = online && (st?.statPoints ?? 0) > 0;
        _resetStats.interactable = online && st != null && st.str + st.dex + st.intelligence + st.luk > 0 && _pl.Gold >= StatResetCost;
        RedrawSkills();
        _bEnh.interactable = s != null;
        _bEnhTry.interactable = online && s != null && s.enh < GameData.ENH_MAX;
        _bBuyOne.interactable = _bBuyTen.interactable = online;
        _checked.RemoveWhere(u => !items.Any(x => x.uid == u));
        _bSellMany.GetComponentInChildren<Text>(true).text = _checked.Count > 0 ? $"선택 판매 ({_checked.Count})" : "선택 판매";
        _bSellMany.interactable = online && _checked.Count > 0;
        var ic = s == null ? null : Resources.Load<Sprite>("Sprites/" + IconPath(s));
        _icon.enabled = ic != null; if (ic != null) { _icon.sprite = ic; _icon.rectTransform.sizeDelta = ic.rect.size; } // 정수 1배
        _enhInfo.text = s == null ? "가방에서 장비를 고르세요" :
            $"{s.name} +{s.enh}\n" + (s.cost == null || s.enh >= GameData.ENH_MAX ? "최대 강화" :
            $"비용: 강화석 {s.cost.stones} / {s.cost.gold}G\n보유: 강화석 {st?.stones ?? 0} / {st?.gold ?? 0}G\n성공 시 효과 {GameData.EnhPct[Mathf.Min(s.enh + 1, GameData.ENH_MAX)]}%");
        _bPotionThreshold.GetComponentInChildren<Text>(true).text = $"포션 자동 사용: HP {_pl.PotionHpThresholdPercent}% 이하 (누르면 변경)";
        _shopInfo.text = $"포션 {st?.potions ?? 0}개 보유\n가격 {st?.potionPrice ?? 0}G / 보유 {st?.gold ?? 0}G";
        int c19 = st?.killCountT19 ?? 0, c20 = st?.killCountT20 ?? 0;
        _sumInfo.text = $"드래곤 (B·C 처치) {c19}/{MonsterSpawner.Need(19)}\n염제 (C 처치) {c20}/{MonsterSpawner.Need(20)}";
        _bS19.interactable = online && _pendingSummon == 0 && _sp != null && _sp.CanSummon(19) && c19 >= MonsterSpawner.Need(19); _bS20.interactable = online && _pendingSummon == 0 && _sp != null && _sp.CanSummon(20) && c20 >= MonsterSpawner.Need(20);
    }
    void RedrawSkills() {
        for (int i = 0; i < GameData.Skills.Length; i++) {
            var skill = GameData.Skills[i];
            var book = _pl.Inv?.skillbooks?.FirstOrDefault(b => b.key == skill.key);
            bool owned = _pl.OwnsSkill(skill.key);
            string status = owned ? _pl.CanCast(skill) ? $"대기 {_pl.SkillCooldown(skill.key):0.0}초" : "레벨·무기 조건 확인" : book?.available == true ? $"{book.price:N0}G" : "가격 확인 중";
            _skillButtons[i].GetComponentInChildren<Text>(true).text = $"{skill.name} · Lv{skill.lv} · {(skill.weapon == "staff" ? "지팡이" : "검")} · {status}";
            _skillButtons[i].interactable = Online && !owned && book?.available == true && _pl.Level >= skill.lv && _pl.Gold >= book.price;
        }
        var weapon = _pl.CurrentWeapon;
        int defense = _pl.Defense;
        float mult = 1f + GameData.EnhPct[Mathf.Clamp(_pl.WeaponEnhance, 0, GameData.ENH_MAX)] / 100f;
        _statValues["class"].text = _pl.WeaponKind == "staff" ? "마법사" : "전사";
        _statValues["level"].text = $"{_pl.Level} / {GameData.LEVEL_MAX}";
        _statValues["hp"].text = $"{_pl.Hp:N0} / {_pl.MaxHp:N0}";
        long need = GameData.XpToLevel(_pl.Level);
        _statValues["exp"].text = _pl.Level >= GameData.LEVEL_MAX ? "MAX" : $"{_pl.Exp:N0} / {need:N0} ({100.0 * _pl.Exp / Math.Max(1, need):0.0}%)";
        _statValues["gold"].text = $"{_pl.Gold:N0} G";
        _statValues["stones"].text = $"{_pl.Inv?.stones ?? 0:N0} 개";
        _statValues["potions"].text = $"{_pl.PotionCount:N0} 개";
        _statValues["attack"].text = $"{Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(1, Mathf.RoundToInt(weapon.dmgLo * mult)) * _pl.AttackMultiplier))} ~ {Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(1, Mathf.RoundToInt(weapon.dmgHi * mult)) * _pl.AttackMultiplier))}";
        _statValues["fixed"].text = $"+{CombatMath.PlayerFixedAtk(_pl.Level)}";
        _statValues["defense"].text = defense.ToString("N0");
        _statValues["reduction"].text = $"{100f * defense / (100f + defense):0.0}%";
        _statValues["range"].text = $"{_pl.Range * 40f:0.#} px";
        _statValues["critical"].text = $"{_pl.CriticalChance * 100f:0.#}% (×{_pl.CriticalDamageMultiplier:0.#})";
        _statValues["threshold"].text = $"HP {_pl.PotionHpThresholdPercent}% 이하";
        foreach (var skill in GameData.Skills) {
            float cooldown = _pl.SkillCooldown(skill.key);
            _statValues["skill_" + skill.key].text = !_pl.OwnsSkill(skill.key) ? $"Lv{skill.lv} · 미습득" : !_pl.CanCast(skill) ? "레벨·무기 조건 미충족" : cooldown > 0 ? $"대기 {cooldown:0.0}초 / {_pl.SkillBaseCooldown(skill):0}초" : "자동 시전 준비";
        }
    }
    string ItemStats(InvItem item) {
        float mult = 1f + GameData.EnhPct[Mathf.Clamp(item.enh, 0, GameData.ENH_MAX)] / 100f;
        if (item.slot == "weapon") {
            var weapons = item.kind == "staff" ? GameData.Staves : GameData.Swords;
            if (item.tier < 0 || item.tier >= weapons.Length) return "";
            var w = weapons[item.tier];
            return $"공격 {Mathf.RoundToInt(w.dmgLo * mult)}~{Mathf.RoundToInt(w.dmgHi * mult)}\n{(item.kind == "staff" ? "마법사" : "전사")}";
        }
        var gear = item.slot == "helmet" ? GameData.Helmets : GameData.Armors;
        if (item.tier < 0 || item.tier >= gear.Length) return "";
        var g = gear[item.tier];
        int previous = item.slot == "helmet" ? _pl.HelmetTierIdx : _pl.ArmorTierIdx;
        int enh = item.slot == "helmet" ? _pl.HelmetEnhance : _pl.ArmorEnhance;
        int before = previous >= 0 && previous < gear.Length ? CombatMath.GearDefWithEnhance(gear[previous], enh) : 0;
        int after = CombatMath.GearDefWithEnhance(g, item.enh);
        return $"방어 {after} ({after - before:+0;-0;0})\n{(g.cls == "mage" ? "마법사" : g.cls == "warrior" ? "전사" : "공용")}";
    }
    // 서버 tier = GameData 배열 인덱스(API.md)
    static string IconPath(InvItem it) {
        if (it.slot == "weapon") { var a = it.kind == "staff" ? GameData.Staves : GameData.Swords; return it.tier >= 0 && it.tier < a.Length ? a[it.tier].spritePath : ""; }
        var g = it.slot == "helmet" ? GameData.Helmets : GameData.Armors; return it.tier >= 0 && it.tier < g.Length ? g[it.tier].spritePath : "";
    }
    static string SlotName(string s) => s == "weapon" ? "무기" : s == "helmet" ? "투구" : "갑옷";

    void Update() => Tick(Time.deltaTime);
    // 캡처 툴이 고정 dt 로 호출
    public void Tick(float dt) {
        if (_rowsChanged) { _rowsChanged = false; if (_mobile) ResponsiveLayout(); } // 모바일 가방 줄 수 바뀌면 다시 쌓기
        if (_screenWidth != (_cam != null ? _cam.pixelWidth : Screen.width) || _screenHeight != (_cam != null ? _cam.pixelHeight : Screen.height)) ResponsiveLayout();
        if (_wasOnline != Online) { _wasOnline = Online; if (!Online) { _pendingSummon = 0; _summonMonsterId = 0; _summonAck = false; _summonRetry = 0; } Redraw(); }
        if (_summonAck && _summonRetry > 0 && (_summonRetry -= dt) <= 0) {
            if (Online) _summonAckSeq = Send(new InvReq { type = "summon_ack", tier = _pendingSummon, token = _summonToken, spawned = _summonSpawned });
            else _summonRetry = 3;
        }
        UpdateHud();
        _skillHudTick -= dt;
        if (_skillHudTick <= 0) { RedrawSkills(); UpdateSkillHud(); _skillHudTick = 0.2f; }
        if (_toastT > 0 && (_toastT -= dt) <= 0) _toast.text = "";
        if (_fxT >= 0 && _fxPlay == null) _fxT = -1;
        if (_fxT >= 0) {
            int f = (int)(_fxT * 10); // 10fps (MANIFEST 기본값)
            _fx.enabled = f < _fxPlay.Length;
            if (_fx.enabled) { _fx.sprite = _fxPlay[f]; _fx.rectTransform.sizeDelta = _fx.sprite.rect.size * (_fxPlay == _fxSuccess ? 1 : 2); } else _fxT = -1; // 성공 1배(형 결정), 그 외 정수 2배
            if (_fxT >= 0) _fxT += dt;
        }
    }
    public void SetFxTime(float t) { if (_fxPlay == null) return; _fxT = t; Tick(0); }

    // ── HUD(좌상단, B/C안 HUD 바: frame 120x14 border L8 R8, fill 2x8) ──
    void BuildHud() {
        _skillHudRoot = new GameObject("SkillCooldownHud").AddComponent<RectTransform>();
        _skillHudRoot.SetParent(_root, false); _skillHudRoot.anchorMin = Vector2.zero; _skillHudRoot.anchorMax = Vector2.one;
        _skillHudRoot.offsetMin = _skillHudRoot.offsetMax = Vector2.zero;
        var root = new GameObject("Hud").AddComponent<RectTransform>(); root.SetParent(_root, false);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0, 1); root.anchoredPosition = new Vector2(10, -10); root.sizeDelta = new Vector2(220, 80);
        var badge = Img(root, null, new Vector2(0, 0), new Vector2(40, 14)); badge.color = new Color32(0x7A, 0x4A, 0x1E, 255); // Lv 배지(make_c3d lvbadge 색)
        var bo = badge.gameObject.AddComponent<Outline>(); bo.effectColor = new Color32(0x1C, 0x12, 0x0C, 255); bo.effectDistance = new Vector2(1, -1);
        _lvText = Label(badge.transform, "", new Vector2(0, 0), new Vector2(40, 14), TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), true);
        (_hpFill, _hpText) = Bar(root, KeyHudHp, "hp", -18);
        (_expFill, _expText) = Bar(root, KeyHudExp, "exp", -36);
        Img(root, Resources.Load<Sprite>(KeyIcon + "gold"), new Vector2(0, -54), Vector2.zero);
        _goldText = Label(root, "", new Vector2(18, -54), new Vector2(90, 14), TextAnchor.MiddleLeft, new Vector2(0, 1), true);
        Img(root, Resources.Load<Sprite>(KeyPotion), new Vector2(110, -48), Vector2.zero);
        _potText = Label(root, "", new Vector2(144, -54), new Vector2(60, 14), TextAnchor.MiddleLeft, new Vector2(0, 1), true);
    }
    void UpdateSkillHud() {
        _skillHudRoot.gameObject.SetActive(!_panels.Values.Any(panel => panel.activeSelf));
        var skills = GameData.Skills.Where(s => _pl.OwnsSkill(s.key) && s.weapon == _pl.WeaponKind).ToArray();
        float width = Mathf.Min(100, (_cam.pixelWidth / GetComponent<CanvasScaler>().scaleFactor - 20) / Mathf.Max(1, skills.Length));
        foreach (var entry in _cooldownHud) entry.Value.transform.parent.gameObject.SetActive(false);
        for (int i = 0; i < skills.Length; i++) {
            var skill = skills[i];
            if (!_cooldownHud.TryGetValue(skill.key, out var text)) {
                var card = Img(_skillHudRoot, _panel, Vector2.zero, new Vector2(width - 4, 30)); card.type = Image.Type.Sliced;
                card.gameObject.name = "Cooldown_" + skill.key;
                card.rectTransform.anchorMin = card.rectTransform.anchorMax = card.rectTransform.pivot = new Vector2(.5f, 0);
                text = Label(card.transform, "", Vector2.zero, new Vector2(width - 8, 26), TextAnchor.MiddleCenter, new Vector2(.5f, .5f));
                text.rectTransform.pivot = new Vector2(.5f, .5f); text.fontSize = 12;
                _cooldownHud.Add(skill.key, text);
            }
            var rect = (RectTransform)text.transform.parent;
            rect.gameObject.SetActive(true); rect.sizeDelta = new Vector2(width - 4, 30);
            rect.anchoredPosition = new Vector2((i - (skills.Length - 1) * .5f) * width, 2); // 한 줄·바닥 흙 띠: 발밑 드롭/피격 가리지 않게
            text.rectTransform.sizeDelta = new Vector2(width - 8, 26); text.fontSize = 12;
            float remaining = _pl.SkillCooldown(skill.key);
            text.text = skill.name + " " + (!_pl.CanCast(skill) ? "불가" : remaining > 0 ? $"{Mathf.CeilToInt(remaining)}초" : "준비");
            text.color = remaining > 0 || !_pl.CanCast(skill) ? new Color(.45f, .35f, .22f) : new Color(.15f, .4f, .12f);
        }
    }
    (Image, Text) Bar(RectTransform root, string fillKey, string icon, float y) {
        Img(root, Resources.Load<Sprite>(KeyIcon + icon), new Vector2(0, y), Vector2.zero);
        var fr = Img(root, Resources.Load<Sprite>(KeyHudFrame), new Vector2(16, y), new Vector2(120, 14)); fr.type = Image.Type.Sliced;
        var fill = Img(fr.transform, Resources.Load<Sprite>(fillKey), new Vector2(3, -3), new Vector2(114, 8)); // 프레임 안쪽 114x8 (실측 근사)
        var t = Label(root, "", new Vector2(140, y), new Vector2(110, 14), TextAnchor.MiddleLeft, new Vector2(0, 1), true);
        return (fill, t);
    }
    Image Img(Transform parent, Sprite s, Vector2 pos, Vector2 size) {
        var i = new GameObject("Img").AddComponent<Image>(); i.transform.SetParent(parent, false); i.raycastTarget = false;
        i.sprite = s; var rt = i.rectTransform; rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1); rt.anchoredPosition = pos;
        rt.sizeDelta = size == Vector2.zero && s != null ? s.rect.size : size; // 아이콘 = 원본 1배
        return i;
    }
    void UpdateHud() {
        if (_pl == null || _hpFill == null) return;
        long need = GameData.XpToLevel(_pl.Level);
        _lvText.text = $"Lv{_pl.Level}";
        _hpFill.rectTransform.sizeDelta = new Vector2(Mathf.Round(114f * Mathf.Clamp01((float)_pl.Hp / Mathf.Max(1, _pl.MaxHp))), 8);
        _expFill.rectTransform.sizeDelta = new Vector2(Mathf.Round(114f * Mathf.Clamp01((float)_pl.Exp / Mathf.Max(1, need))), 8);
        _hpText.text = $"{_pl.Hp}/{_pl.MaxHp}";
        _expText.text = $"{100.0 * _pl.Exp / Mathf.Max(1, need):0.0}%";
        _goldText.text = $"{_pl.Gold:N0}";
        _potText.text = $"x{_pl.PotionCount}";
        _channel.text = Online ? $"채널 {_net.Channel}" : _net != null && _net.Connected ? "접속 중" : "오프라인";
    }

    void CaptureLayout() {
        foreach (var rect in GetComponentsInChildren<RectTransform>(true))
            _desktop[rect] = (rect.parent, rect.anchorMin, rect.anchorMax, rect.pivot, rect.anchoredPosition, rect.sizeDelta);
    }
    void ResponsiveLayout() {
        _screenWidth = _cam != null ? _cam.pixelWidth : Screen.width;
        _screenHeight = _cam != null ? _cam.pixelHeight : Screen.height;
        int zoom = Game.Rendering.ZoneController.PixelZoom(_cam);
        float width = (float)_screenWidth / zoom, height = (float)_screenHeight / zoom;
        _mobile = Application.isMobilePlatform || width < 1000 || height < 640;
        var scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = zoom;
        foreach (var pair in _desktop) {
            if (pair.Key == null) continue;
            var d = pair.Value; pair.Key.SetParent(d.parent, false);
            pair.Key.anchorMin = d.min; pair.Key.anchorMax = d.max; pair.Key.pivot = d.pivot;
            pair.Key.anchoredPosition = d.pos; pair.Key.sizeDelta = d.size;
        }
        foreach (var scroll in _mobileScroll.Values) scroll.gameObject.SetActive(false);
        foreach (var text in GetComponentsInChildren<Text>(true)) text.fontSize = _mobile && text.rectTransform.sizeDelta.y >= 20 ? 16 : FontSize;
        foreach (var card in _statCards)
            foreach (var text in card.GetComponentsInChildren<Text>(true)) text.fontSize = 14;
        if (!_mobile) return;
        int columns = Mathf.Max(1, Mathf.Min(_menus.Count, Mathf.FloorToInt((width - 20) / 120)));
        float menuWidth = (width - 20f) / columns;
        for (int i = 0; i < _menus.Count; i++) {
            var rect = (RectTransform)_menus[i].transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(10 + i % columns * menuWidth, -94 - i / columns * 48);
            rect.sizeDelta = new Vector2(menuWidth - 4, 44);
            ResizeButtonText(_menus[i], menuWidth - 4, 44);
        }
        _channel.rectTransform.anchoredPosition = new Vector2(-10, -10);
        _toast.rectTransform.anchoredPosition = new Vector2(0, -98 - Mathf.CeilToInt((float)_menus.Count / columns) * 48);
        _toast.rectTransform.sizeDelta = new Vector2(width - 20, 52);
        _toast.horizontalOverflow = HorizontalWrapMode.Wrap;
        foreach (var panel in _panels) {
            var rect = (RectTransform)panel.Value.transform;
            rect.sizeDelta = new Vector2(Mathf.Min(width - 20, 620), Mathf.Min(height - 20, 620));
            if (!_mobileScroll.TryGetValue(panel.Key, out var scroll)) {
                var viewport = new GameObject("MobileScroll", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
                viewport.transform.SetParent(rect, false);
                viewport.GetComponent<Image>().color = new Color(0, 0, 0, 0.001f);
                scroll = viewport.GetComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.viewport = (RectTransform)viewport.transform;
                var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
                content.SetParent(viewport.transform, false); scroll.content = content;
                _mobileScroll[panel.Key] = scroll;
            }
            scroll.gameObject.SetActive(true);
            var vr = (RectTransform)scroll.transform;
            vr.anchorMin = Vector2.zero; vr.anchorMax = Vector2.one; vr.offsetMin = new Vector2(10, 10); vr.offsetMax = new Vector2(-10, -54);
            var contentRect = scroll.content;
            contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = new Vector2(1, 1); contentRect.pivot = new Vector2(0.5f, 1);
            float y = 0, contentWidth = rect.sizeDelta.x - 20;
            // Reuse the desktop widgets, stacked in a touch-scrollable column. No separate mobile UI state.
            var widgets = _desktop.Where(pair => pair.Value.parent == rect).Select(pair => pair.Key).ToArray();
            for (int i = 0; i < widgets.Length; i++) {
                var child = widgets[i];
                if (i < 2) continue; // Fixed title and close button.
                if (!_statCards.Contains(child) && child.GetComponent<Image>() != null && child.GetComponent<Button>() == null && child.GetComponent<InputField>() == null) continue;
                child.SetParent(contentRect, false); child.anchorMin = child.anchorMax = child.pivot = new Vector2(0, 1);
                var button = child.GetComponent<Button>();
                float h = button != null ? 48 : child == _allocationInfo.rectTransform ? 80 : child.GetComponent<Text>() != null ? Mathf.Clamp(_desktop[child].size.y, 28, 96) : Mathf.Max(28, _desktop[child].size.y); // 긴 설명칸 여백 컷
                int checkIndex = button != null ? _checks.IndexOf(button) : -1;
                bool itemRow = button != null && _rows.Contains(button);
                if ((itemRow || checkIndex >= 0) && !child.gameObject.activeSelf) continue; // 빈 아이템 줄은 자리 차지 안 함(빈 가방 큰 여백)
                float childWidth = checkIndex >= 0 ? 44 : itemRow ? contentWidth - 50 : contentWidth;
                bool allocationInfo = child == _allocationInfo.rectTransform;
                child.anchoredPosition = new Vector2(itemRow ? 50 : allocationInfo ? 6 : 0, -y); child.sizeDelta = new Vector2(allocationInfo ? childWidth - 12 : childWidth, h);
                if (button != null) ResizeButtonText(button, childWidth, h);
                var input = child.GetComponent<InputField>();
                if (input != null) foreach (var text in input.GetComponentsInChildren<Text>(true)) text.rectTransform.sizeDelta = new Vector2(contentWidth - 20, h);
                var label = child.GetComponent<Text>(); if (label != null) label.horizontalOverflow = HorizontalWrapMode.Wrap;
                if (checkIndex < 0) y += h + 6;
            }
            contentRect.sizeDelta = new Vector2(0, y); contentRect.anchoredPosition = Vector2.zero;
            widgets[0].sizeDelta = new Vector2(rect.sizeDelta.x - 80, 28);
            var close = widgets[1].GetComponent<Button>();
            ((RectTransform)close.transform).sizeDelta = new Vector2(44, 44); ResizeButtonText(close, 44, 44);
        }
    }
    void ResizeButtonText(Button button, float width, float height) {
        var text = button.GetComponentInChildren<Text>(true);
        text.rectTransform.sizeDelta = new Vector2(Mathf.Max(20, width - 20), height);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
    }

    void StatCard(RectTransform parent, string title, Vector2 pos, float width, string[] rows) {
        var card = Img(parent, null, pos, new Vector2(width, 38 + rows.Length * 28));
        card.gameObject.name = "Stats_" + title;
        card.color = new Color(0.97f, 0.94f, 0.85f);
        _statCards.Add(card.rectTransform);
        var heading = Label(card.transform, title, new Vector2(12, -6), new Vector2(width - 24, 26), TextAnchor.MiddleLeft, new Vector2(0, 1));
        heading.font = _fontB;
        for (int i = 0; i < rows.Length; i++) {
            var kv = rows[i].Split(':');
            var band = Img(card.transform, null, new Vector2(8, -34 - i * 28), new Vector2(-16, 26));
            band.rectTransform.anchorMax = new Vector2(1, 1);
            band.color = i % 2 == 0 ? new Color(0.88f, 0.86f, 0.78f) : new Color(0.94f, 0.92f, 0.85f);
            var label = Label(band.transform, kv[1], Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Vector2(0, 1));
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = new Vector2(0.48f, 1);
            label.rectTransform.offsetMin = new Vector2(6, 0); label.rectTransform.offsetMax = Vector2.zero;
            var value = Label(band.transform, "", Vector2.zero, Vector2.zero, TextAnchor.MiddleRight, new Vector2(1, 1));
            value.rectTransform.anchorMin = new Vector2(0.48f, 0); value.rectTransform.anchorMax = Vector2.one;
            value.rectTransform.offsetMin = Vector2.zero; value.rectTransform.offsetMax = new Vector2(-6, 0);
            value.resizeTextForBestFit = true; value.resizeTextMinSize = 10; value.resizeTextMaxSize = 14;
            value.horizontalOverflow = HorizontalWrapMode.Wrap;
            _statValues[kv[0]] = value;
        }
    }

    // ── 위젯 ──
    RectTransform Panel(string id, string title, Vector2 size, string key = null) {
        var img = new GameObject("Panel_" + id).AddComponent<Image>();
        img.transform.SetParent(_root, false);
        img.sprite = key != null ? Resources.Load<Sprite>(key) : _panel; img.type = key != null ? Image.Type.Sliced : Image.Type.Tiled; // shop_panel = Tiled(MANIFEST)
        var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = size; rt.anchoredPosition = new Vector2(0, 10);
        Label(rt, title, new Vector2(0, -16), new Vector2(size.x - 40, 24), TextAnchor.MiddleCenter, new Vector2(0.5f, 1)).font = _fontB;
        Btn(rt, "X", new Vector2(-12, -12), new Vector2(34, BtnH), () => img.gameObject.SetActive(false), new Vector2(1, 1));
        img.gameObject.SetActive(false);
        _panels[id] = img.gameObject;
        return rt;
    }

    static readonly Color Ink = new Color(0.22f, 0.13f, 0.08f); // 양피지·버튼 위 글자
    Text Label(Transform parent, string s, Vector2 pos, Vector2 size, TextAnchor a, Vector2 anchor, bool onWorld = false) {
        var t = new GameObject("Text").AddComponent<Text>();
        t.transform.SetParent(parent, false);
        t.supportRichText = false;
        t.font = _font; t.fontSize = FontSize; t.text = s; t.alignment = a; t.color = onWorld ? Color.white : Ink; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        var o = t.gameObject.AddComponent<Outline>(); o.effectColor = onWorld ? Color.black : new Color(1f, 0.93f, 0.8f, 0.6f); o.effectDistance = new Vector2(1, -1); // 1px
        var rt = t.rectTransform; rt.anchorMin = rt.anchorMax = rt.pivot = anchor; rt.anchoredPosition = pos; rt.sizeDelta = size;
        return t;
    }

    Button Btn(Transform parent, string s, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick, Vector2 anchor, TextAnchor a = TextAnchor.MiddleCenter) {
        var img = new GameObject("Btn").AddComponent<Image>();
        img.transform.SetParent(parent, false);
        img.sprite = _btn; img.type = Image.Type.Sliced;
        var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = rt.pivot = anchor; rt.anchoredPosition = pos; rt.sizeDelta = size;
        var b = img.gameObject.AddComponent<Button>();
        b.targetGraphic = img; // Hidden panels have not run Selectable.OnEnable yet.
        b.transition = Selectable.Transition.SpriteSwap;
        b.spriteState = new SpriteState { pressedSprite = _btnDown, disabledSprite = _btnOff, highlightedSprite = _btn, selectedSprite = _btn };
        b.onClick.AddListener(onClick);
        var t = Label(img.transform, s, Vector2.zero, new Vector2(Mathf.Max(size.x - 2 * (14 + 6), 14), size.y), a, new Vector2(0.5f, 0.5f)); // 9-slice 좌우 border 14 + 여백 6
        t.rectTransform.anchoredPosition = new Vector2(0, 1);
        return b;
    }
}
}
