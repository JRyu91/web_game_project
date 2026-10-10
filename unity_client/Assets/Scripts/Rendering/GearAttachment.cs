// Stage 3 무기 부착: 프레임별 손 테이블(Config/hand_table.json: 손 좌표 + 방향 인덱스 + 몸 앞/뒤) 조회, 보간 없음(프레임 바뀌면 즉시 스냅).
// 무기 스프라이트 = WeaponsDir/<이름>/d0..d7 (8방향 사전 생성, 5/6 nearest, 손잡이 = 피벗). 런타임 회전/스케일 없음.
// 착용 레이어는 원본 몸 프레임과 동기화한다. 원본 몸은 발 정렬·콜라이더 기준으로 유지한다.
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering {

public enum CarryStyle { Down, Sheath } // idle/walk 들기 방식 — 형 확정(2026-10-07): Sheath C안. Shoulder 폐기

public class GearAttachment : MonoBehaviour {
    struct Hand { public int x, y, dir; public bool back; }
    static Dictionary<string, Hand> _hands;
    // Sheath C안: 몸 뒤(order 5), 칼날 210°(위=0°, 시계방향), 폼멜(칼자루 끝) = 몸 캔버스(148px, 좌상 원점) m(70,80) / f(70,74).
    // 스프라이트 피벗은 grip → grip = 폼멜 + L·(sinθ, −cosθ) (캔버스 y 아래). L = 폼멜→grip 거리(구운 px) = (yb − gy)·factor(이름),
    // yb = 원본 96 캔버스 불투명 최하단 행, gy = weapon_grip.json, factor = 5/6(단검 ×0.7). L·θ는 에셋 담당이 Config/weapon_sheath.json 으로 제공.
    // 폼멜 앵커 mode 별(에셋 MANIFEST §1): hip / back. "staff" 는 2단계(형 결정)에서 폐기 → back 으로 처리, 그 외 없는 mode 는 hip
    static readonly Dictionary<string, Dictionary<string, Vector2Int>> SheathAnchor = new Dictionary<string, Dictionary<string, Vector2Int>> {
        { "hip", new Dictionary<string, Vector2Int> { { "main_m", new Vector2Int(70, 80) }, { "main_f", new Vector2Int(70, 74) } } },
        { "back", new Dictionary<string, Vector2Int> { { "main_m", new Vector2Int(68, 42) }, { "main_f", new Vector2Int(68, 40) } } },
    };
    // Resources.Load 키 모음(에셋 MANIFEST 확정 전 가정값 — 바뀌면 여기만 고친다)
    const string KeyFxJson = "Config/weapon_fx", KeySheathJson = "Config/weapon_sheath", SheathSprite = "d_sheath", SheathFx = "fx_sheath_";
    struct Fx { public int frames; public float fps; public bool front; }
    static Dictionary<string, Fx> _fxCfg;
    static Dictionary<string, (Vector2 lt, string mode)> _sheathCfg; // 이름 → ((L px, θ deg), mode)

    public CarryStyle Carry = CarryStyle.Sheath;
    public Transform HandSlot { get; private set; }
    SpriteRenderer _bodyRenderer, _weaponSr;
    FrameAnimator _anim;
    Sprite[] _dirs, _sheathFx;
    Sprite[][] _fx; // [dir][frame]
    Sprite _sheath;
    string _weaponName;
    int _weaponDir;
    [System.Serializable] class TipEntry { public string key; public float x, y; }
    [System.Serializable] class TipFile { public TipEntry[] tips; }
    static Dictionary<string, Vector2> _tips;
    static void LoadTips() {
        _tips = new Dictionary<string, Vector2>();
        var file = Resources.Load<TextAsset>("Config/weapon_tip");
        if (file != null) foreach (var tip in JsonUtility.FromJson<TipFile>(file.text).tips) _tips[tip.key] = new Vector2(tip.x, tip.y);
    }
    Fx _fxInfo; (Vector2 lt, string mode) _sheathCfgW;
    SpriteRenderer _fxSr;
    Material _mat, _fxMat;
    [System.Serializable] class WearPose { public string key, gender, clip; public int frame, x, y, width, height; }
    [System.Serializable] class WearRect { public int x, y, width, height, cropX, cropY; }
    [System.Serializable] class WearAtlas { public string path; public WearRect[] frames; }
    [System.Serializable] class WearFile { public WearPose[] poses; public WearAtlas[] helmets, armors; }
    [System.Serializable] class MaleWearFile { public WearPose[] poses; public WearAtlas defaultHead, defaultBody, staffDefaultBody, swordTrail; public WearAtlas[] helmets, armors; }
    struct WearFrame { public Sprite sprite; public Vector2 offset; }
    static WearFile _wearFile;
    static MaleWearFile _maleWearFile;
    static bool _maleWearLoaded;
    static readonly Dictionary<Sprite, int> MalePoseIndex = new Dictionary<Sprite, int>();
    static readonly Dictionary<Sprite, int> WearPoseIndex = new Dictionary<Sprite, int>();
    static readonly Dictionary<string, WearFrame[]> WearSprites = new Dictionary<string, WearFrame[]>();
    SpriteRenderer _displayBody, _armorSr, _helmetSr, _defaultHeadSr, _swordTrailSr;
    Material _displayMat, _armorMat, _helmetMat, _defaultHeadMat, _swordTrailMat;
    WearFrame[] _armorWear, _helmetWear;
    WearFrame[] _maleArmor, _maleHelmet, _maleHead, _maleBody, _maleStaffBody, _maleSwordTrail;
    int _helmetTier = -1, _armorTier = -1;
    bool _bodyWasEnabled;
    bool _staff;
    static readonly Dictionary<Sprite, Sprite> StaffBodies = new Dictionary<Sprite, Sprite>();
    static readonly HashSet<string> StaffBodySources = new HashSet<string>();

    static void LoadStaffBody(string gender) {
        if (!StaffBodySources.Add(gender)) return;
        foreach (string clip in new[] { "attack", "attack1", "attack2" })
        for (int i = 0; i < 11; i++) {
            var original = Resources.Load<Sprite>($"{ActorScale.PlayerRoot}/{gender}/{clip}/frame_{i:00}");
            var clean = Resources.Load<Sprite>($"{ActorScale.PlayerRoot}/{gender}/staff_{clip}/frame_{i:00}");
            if (original != null && clean != null) StaffBodies[original] = clean;
        }
    }

    static void LoadWear() {
        if (_wearFile != null) return;
        var file = Resources.Load<TextAsset>("Config/wearable_manifest");
        if (file == null) return;
        _wearFile = JsonUtility.FromJson<WearFile>(file.text);
        for (int i = 0; i < _wearFile.poses.Length; i++) {
            var p = _wearFile.poses[i];
            var body = Resources.Load<Sprite>($"{ActorScale.PlayerRoot}/{p.gender}/{p.clip}/frame_{p.frame:00}");
            if (body != null) WearPoseIndex[body] = i;
        }
    }

    static WearFrame[] WearFrames(WearAtlas[] paths, int tier) {
        if (paths == null || tier < 0 || tier >= paths.Length) return null;
        return WearFrames(paths[tier], _wearFile.poses);
    }

    static WearFrame[] WearFrames(WearAtlas entry, WearPose[] poses) {
        if (entry == null || string.IsNullOrEmpty(entry.path)) return null;
        string path = entry.path;
        if (WearSprites.TryGetValue(path, out var cached)) return cached;
        var atlas = Resources.Load<Texture2D>(path);
        if (atlas == null) { Debug.LogWarning($"[gear] no wearable atlas {path}"); return null; }
        if (entry.frames == null || entry.frames.Length != poses.Length) { Debug.LogError($"[gear] invalid wearable frame count {path}"); return null; }
        var frames = new WearFrame[poses.Length];
        for (int i = 0; i < frames.Length; i++) {
            var p = poses[i]; var r = entry.frames[i];
            if (r.width <= 0 || r.height <= 0 || r.x < 0 || r.y < 0 || r.x + r.width > atlas.width || r.y + r.height > atlas.height || r.cropX < 0 || r.cropY < 0 || r.cropX + r.width > p.width || r.cropY + r.height > p.height) {
                Debug.LogError($"[gear] invalid wearable rect {path}/{p.key}"); return null;
            }
            var sprite = Sprite.Create(atlas, new Rect(r.x, r.y, r.width, r.height), new Vector2(.5f, .5f), 40, 0, SpriteMeshType.FullRect);
            sprite.name = p.key;
            frames[i] = new WearFrame { sprite = sprite, offset = new Vector2((r.cropX + r.width * .5f - p.width * .5f) / 40f, (p.height * .5f - r.cropY - r.height * .5f) / 40f) };
        }
        return WearSprites[path] = frames;
    }

    public void SetWear(int helmetTier, int armorTier) {
        LoadWear();
        _helmetTier = helmetTier; _armorTier = armorTier;
        if (!_maleWearLoaded) {
            _maleWearLoaded = true;
            var file = Resources.Load<TextAsset>("Config/male_wearable_manifest");
            if (file != null) {
                _maleWearFile = JsonUtility.FromJson<MaleWearFile>(file.text);
                for (int i = 0; i < _maleWearFile.poses.Length; i++) {
                    var p = _maleWearFile.poses[i];
                    var body = Resources.Load<Sprite>($"{ActorScale.PlayerRoot}/{p.gender}/{p.clip}/frame_{p.frame:00}");
                    if (body != null) MalePoseIndex[body] = i;
                }
            }
        }
        if (_anim != null && _anim.SourceKey == "main_m" && _maleWearFile != null) {
            WearFrame[] Tier(WearAtlas[] entries, int tier) => entries != null && tier >= 0 && tier < entries.Length ? WearFrames(entries[tier], _maleWearFile.poses) : null;
            _maleHelmet = Tier(_maleWearFile.helmets, helmetTier); _maleArmor = Tier(_maleWearFile.armors, armorTier);
            _maleHead = WearFrames(_maleWearFile.defaultHead, _maleWearFile.poses);
            _maleBody = WearFrames(_maleWearFile.defaultBody, _maleWearFile.poses);
            _maleStaffBody = WearFrames(_maleWearFile.staffDefaultBody, _maleWearFile.poses);
            _maleSwordTrail = WearFrames(_maleWearFile.swordTrail, _maleWearFile.poses);
        }
        bool maleReady = _anim != null && _anim.SourceKey == "main_m" && _maleHead != null && _maleBody != null && _maleStaffBody != null &&
            (helmetTier < 0 || _maleHelmet != null) && (armorTier < 0 || _maleArmor != null);
        // shortcut: 새 남자 착용은 ClipTable 선택 35프레임만 지원한다. ClipTable 변경 시 자산을 다시 베이크한다.
        _helmetWear = maleReady || _wearFile == null ? null : WearFrames(_wearFile.helmets, helmetTier);
        _armorWear = maleReady || _wearFile == null ? null : WearFrames(_wearFile.armors, armorTier);
        ApplyWear();
    }

    void ApplyWear() {
        if (_displayBody == null) return;
        var body = _bodyRenderer.sprite;
        _defaultHeadSr.enabled = false;
        _swordTrailSr.enabled = false;
        int malePose = -1;
        bool maleReplacement = _bodyWasEnabled && _anim != null && _anim.SourceKey == "main_m" && body != null && MalePoseIndex.TryGetValue(body, out malePose) &&
            (_helmetTier >= 0 || _armorTier >= 0) && _maleHead != null && (_staff ? _maleStaffBody : _maleBody) != null &&
            (_helmetTier < 0 || _maleHelmet != null) && (_armorTier < 0 || _maleArmor != null);
        if (maleReplacement) {
            _bodyRenderer.enabled = false;
            _displayBody.enabled = _armorTier < 0; _armorSr.enabled = _armorTier >= 0;
            _defaultHeadSr.enabled = _helmetTier < 0; _helmetSr.enabled = _helmetTier >= 0;
            _swordTrailSr.enabled = !_staff && _maleSwordTrail != null && _maleWearFile.poses[malePose].clip.StartsWith("attack");
            void Part(SpriteRenderer sr, Material mat, WearFrame[] frames) {
                if (!sr.enabled) return;
                var frame = frames[malePose]; sr.sprite = frame.sprite; sr.flipX = _bodyRenderer.flipX; sr.color = _bodyRenderer.color;
                sr.transform.localPosition = new Vector3(sr.flipX ? -frame.offset.x : frame.offset.x, frame.offset.y, 0);
                if (mat != null) { mat.mainTexture = frame.sprite.texture; mat.SetFloat("_Flash", _bodyRenderer.sharedMaterial == null ? 0 : _bodyRenderer.sharedMaterial.GetFloat("_Flash")); }
                else sr.sharedMaterial = _bodyRenderer.sharedMaterial;
            }
            Part(_displayBody, _displayMat, _staff ? _maleStaffBody : _maleBody); Part(_armorSr, _armorMat, _maleArmor);
            Part(_defaultHeadSr, _defaultHeadMat, _maleHead); Part(_helmetSr, _helmetMat, _maleHelmet);
            Part(_swordTrailSr, _swordTrailMat, _maleSwordTrail);
            return;
        }
        Sprite display = body;
        if (_staff && _anim != null) {
            LoadStaffBody(_anim.SourceKey);
            if (body != null && StaffBodies.TryGetValue(body, out var clean)) display = clean;
        }
        int pose = -1;
        bool wearing = _bodyWasEnabled && body != null && WearPoseIndex.TryGetValue(body, out pose) && (_armorWear != null || _helmetWear != null);
        bool showingBody = _bodyWasEnabled && (wearing || display != body);
        _bodyRenderer.enabled = _bodyWasEnabled && !showingBody;
        _displayBody.enabled = showingBody;
        _armorSr.enabled = wearing && _armorWear != null;
        _helmetSr.enabled = wearing && _helmetWear != null;
        if (!showingBody) return;
        _displayBody.sprite = display;
        _displayBody.transform.localPosition = Vector3.zero;
        if (_displayMat != null) _displayMat.mainTexture = display.texture;
        else _displayBody.sharedMaterial = _bodyRenderer.sharedMaterial;
        _displayBody.flipX = _armorSr.flipX = _helmetSr.flipX = _bodyRenderer.flipX;
        _displayBody.color = _armorSr.color = _helmetSr.color = _bodyRenderer.color;
        void Layer(SpriteRenderer sr, Material mat, WearFrame[] frames) {
            if (!wearing || frames == null) return;
            var frame = frames[pose]; sr.sprite = frame.sprite;
            sr.transform.localPosition = new Vector3(_bodyRenderer.flipX ? -frame.offset.x : frame.offset.x, frame.offset.y, 0);
            if (mat != null) mat.mainTexture = sr.sprite.texture;
        }
        Layer(_armorSr, _armorMat, _armorWear); Layer(_helmetSr, _helmetMat, _helmetWear);
        var material = _bodyRenderer.sharedMaterial;
        if (material != null) {
            float flash = material.GetFloat("_Flash");
            _displayMat?.SetFloat("_Flash", flash); _armorMat?.SetFloat("_Flash", flash); _helmetMat?.SetFloat("_Flash", flash);
        }
    }

    static void LoadHands() {
        _hands = new Dictionary<string, Hand>();
        var t = Resources.Load<TextAsset>("Config/hand_table");
        if (t == null) return;
        int F(string body, string k, int d) { var m = Regex.Match(body, $"\"{k}\":\\s*(-?\\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : d; }
        foreach (Match m in Regex.Matches(t.text, "\"(main_[mf]/\\w+/\\d+)\":\\s*\\{([^}]*)\\}")) {
            var b = m.Groups[2].Value;
            _hands[m.Groups[1].Value] = new Hand { x = F(b, "x", 0), y = F(b, "y", 0), dir = F(b, "dir", -1), back = b.Contains("\"back\": true") || b.Contains("\"back\":true") };
        }
    }

    // weapon_fx.json: { "<이름>": { "frames": 4, "fps": 10, "order": "front", "behind_follow": true, ... } } (fx_layer_design §3)
    // weapon_sheath.json: { "<이름>": [L, deg, "hip"|"back"|"staff"] } (3번째 없으면 hip) (L = 폼멜→grip 구운 px, deg = 195~210 지면 관통 보정)
    static void LoadCfg() {
        _fxCfg = new Dictionary<string, Fx>(); _sheathCfg = new Dictionary<string, (Vector2, string)>();
        var t = Resources.Load<TextAsset>(KeyFxJson);
        if (t != null) foreach (Match m in Regex.Matches(t.text, "\"(L\\d+_[^\"]+)\":\\s*\\{([^}]*)\\}")) {
            var b = m.Groups[2].Value; float N(string k, float d) { var q = Regex.Match(b, $"\"{k}\":\\s*(-?[\\d.]+)"); return q.Success ? float.Parse(q.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : d; }
            _fxCfg[m.Groups[1].Value] = new Fx { frames = (int)N("frames", 0), fps = N("fps", 10), front = !Regex.IsMatch(b, "\"order\":\\s*\"back\"") };
        }
        t = Resources.Load<TextAsset>(KeySheathJson);
        if (t != null) foreach (Match m in Regex.Matches(t.text, "\"(L\\d+_[^\"]+)\":\\s*\\[\\s*(-?[\\d.]+)\\s*,\\s*(-?[\\d.]+)\\s*(?:,\\s*\"(\\w+)\")?"))
            _sheathCfg[m.Groups[1].Value] = (new Vector2(float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)), m.Groups[4].Value == "staff" ? "back" : m.Groups[4].Success ? m.Groups[4].Value : "hip");
    }

    public void Init(SpriteRenderer bodyRenderer, FrameAnimator anim = null) {
        _bodyRenderer = bodyRenderer; _anim = anim;
        _bodyWasEnabled = bodyRenderer.enabled;
        HandSlot = new GameObject("HandAnchor").transform;
        HandSlot.SetParent(transform, false);
        _weaponSr = new GameObject("Weapon").AddComponent<SpriteRenderer>();
        _weaponSr.transform.SetParent(HandSlot, false);
        var sh = Resources.Load<Shader>("Shaders/ActorSprite");
        if (sh != null) _weaponSr.sharedMaterial = _mat = new Material(sh); // 시간대 그레이딩·외곽선 몸과 동일
        _fxSr = new GameObject("WeaponFx").AddComponent<SpriteRenderer>(); // fx_layer_design §4: 같은 HandAnchor, localPosition 0
        _fxSr.transform.SetParent(HandSlot, false);
        if (sh != null) _fxSr.sharedMaterial = _fxMat = new Material(sh); // 텍스처가 달라 머티리얼은 따로(ActorVisual 배칭 주석 참고)
        var wearRoot = new GameObject("BodyWear"); wearRoot.transform.SetParent(transform, false);
        var group = wearRoot.AddComponent<SortingGroup>(); group.sortingOrder = bodyRenderer.sortingOrder; group.sortingLayerID = bodyRenderer.sortingLayerID;
        SpriteRenderer Layer(string name, int order) {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>(); sr.transform.SetParent(wearRoot.transform, false); sr.sortingOrder = order; sr.enabled = false; return sr;
        }
        _displayBody = Layer("Body", 0); _armorSr = Layer("Armor", 1); _defaultHeadSr = Layer("Head", 2); _helmetSr = Layer("Helmet", 2);
        _swordTrailSr = Layer("SwordTrail", 3);
        if (sh != null) { _displayBody.sharedMaterial = _displayMat = new Material(sh); _armorSr.sharedMaterial = _armorMat = new Material(sh); _helmetSr.sharedMaterial = _helmetMat = new Material(sh); _defaultHeadSr.sharedMaterial = _defaultHeadMat = new Material(sh); }
        if (sh != null) _swordTrailSr.sharedMaterial = _swordTrailMat = new Material(sh);
        SetFace(1);
    }

    // 파일명(확장자 없음, 예: "L000_낡은단검"). null = 숨김.
    public void SetWeapon(string name) {
        if (_weaponName == name) { Apply(); return; }
        _weaponName = name;
        _staff = !string.IsNullOrEmpty(name) && System.Array.Exists(Game.Data.GameData.Staves, w => System.IO.Path.GetFileName(w.spritePath) == name);
        _dirs = null; _fx = null; _sheath = null; _sheathFx = null;
        if (_fxCfg == null) LoadCfg();
        if (!string.IsNullOrEmpty(name)) {
            string dir = $"Sprites/WeaponsDir/{Folder(name)}/";
            _dirs = new Sprite[8];
            for (int k = 0; k < 8; k++) _dirs[k] = Resources.Load<Sprite>($"{dir}d{k}");
            if (_dirs[0] == null) { Debug.LogWarning($"[gear] no WeaponsDir sprites for {name}"); _dirs = null; }
            _sheath = Resources.Load<Sprite>(dir + SheathSprite);
            if (!_sheathCfg.TryGetValue(name, out _sheathCfgW)) _sheathCfgW = (new Vector2(0, 210), "hip"); // 데이터 없으면 grip 을 폼멜 자리에(검증 안 됨)
            if (_fxCfg.TryGetValue(name, out _fxInfo) && _fxInfo.frames > 0) {
                _fx = new Sprite[8][]; _sheathFx = new Sprite[_fxInfo.frames];
                for (int k = 0; k < 8; k++) { _fx[k] = new Sprite[_fxInfo.frames]; for (int j = 0; j < _fxInfo.frames; j++) _fx[k][j] = Resources.Load<Sprite>($"{dir}fx_d{k}_{j}"); }
                for (int j = 0; j < _fxInfo.frames; j++) _sheathFx[j] = Resources.Load<Sprite>($"{dir}{SheathFx}{j}");
            }
        }
        Apply();
    }

    // 한글 폴더명은 Resources.Load 실패(실측) → UTF-8 hex (Tools/bake_weapon_dirs.py folder() 와 같은 규칙)
    public static string Folder(string name) =>
        "w_" + System.BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(name.Normalize())).Replace("-", "");

    public void SetFace(int face) {
        _bodyRenderer.flipX = face < 0;
        Apply();
    }

    public Vector3 Muzzle {
        get {
            if (_tips == null) LoadTips();
            if (!_tips.TryGetValue($"{_weaponName}/{_weaponDir}", out var tip)) return HandSlot.position;
            return _weaponSr.transform.TransformPoint(new Vector3(_weaponSr.flipX ? -tip.x : tip.x, tip.y, 0));
        }
    }
    void LateUpdate() => Apply();
    void OnDestroy() { if (_mat != null) Destroy(_mat); if (_fxMat != null) Destroy(_fxMat); if (_displayMat != null) Destroy(_displayMat); if (_armorMat != null) Destroy(_armorMat); if (_helmetMat != null) Destroy(_helmetMat); if (_defaultHeadMat != null) Destroy(_defaultHeadMat); if (_swordTrailMat != null) Destroy(_swordTrailMat); }

    // 현재 몸 프레임 → 손 위치/방향/앞뒤. 테이블에 없는 프레임은 숨김(허공 무기 방지).
    public void Apply() {
        ApplyWear();
        if (_weaponSr == null) return;
        bool reacting = _anim != null && _anim.Reacting;
        if (_hands == null) LoadHands();
        bool show = false, sheath = false; float x = 0, y = 0; int dir = 0; bool back = false;
        if (_dirs != null && _anim != null && _anim.Clip != null) {
            string ch = _anim.SourceKey, clip = _anim.Clip;
            bool carryClip = clip == "idle" || clip == "walk";
            // 공격 클립 준비 프레임(hand_table 없음, 남녀 0~2) = idle 과 같은 시스 → 전환 때 무기 깜빡임 없음
            if (!carryClip && clip.StartsWith("attack") && !_hands.ContainsKey($"{ch}/{clip}/{_anim.SourceIndex}")) carryClip = true;
            if (carryClip && Carry == CarryStyle.Sheath && _sheath != null && ((SheathAnchor.TryGetValue(_sheathCfgW.mode, out var am) && am.TryGetValue(ch, out var a)) || SheathAnchor["hip"].TryGetValue(ch, out a))) {
                var lt = _sheathCfgW.lt; float th = lt.y * Mathf.Deg2Rad; // grip = 폼멜 + L·(sinθ, −cosθ), 캔버스 y 아래
                show = sheath = true; x = a.x + lt.x * Mathf.Sin(th); y = a.y - lt.x * Mathf.Cos(th); back = true;
            } else if (_hands.TryGetValue($"{ch}/{clip}/{_anim.SourceIndex}", out var h)) {
                show = true; x = h.x; y = h.y; back = h.back;
                dir = carryClip ? 2 : h.dir; // Down(또는 d_sheath 없음): 135°는 칼끝이 땅을 뚫어 90°(앞으로 수평)
                if (carryClip) back = true;
                if (dir < 0) show = false;
            }
        }
        _weaponSr.enabled = show && !reacting;
        _fxSr.enabled = false;
        if (!show) return;
        var body = _bodyRenderer.sprite;
        bool flip = _bodyRenderer.flipX;
        // 구운 손잡이 픽셀 자체가 피벗에서 (+.5,-.5)px: 앵커에 다시 반 픽셀을 더하면 좌우 Point 샘플링이 달라진다.
        float lx = Mathf.Round(x - body.pivot.x) / 40f, ly = Mathf.Round(body.rect.height - y - body.pivot.y) / 40f;
        HandSlot.localPosition = new Vector3(flip ? -lx : lx, ly, 0);
        _weaponSr.sprite = sheath ? _sheath : _dirs[dir];
        _weaponDir = dir;
        _weaponSr.flipX = flip;
        _weaponSr.sortingOrder = back ? 5 : 7; // 몸 6
        if (_mat != null) _mat.mainTexture = _weaponSr.sprite.texture;
        if (reacting || _fx == null) return;
        var fs = sheath ? _sheathFx : _fx[dir];
        var f = fs[(int)(ZoneController.Now * _fxInfo.fps) % _fxInfo.frames];
        if (f == null) return;
        _fxSr.enabled = true; _fxSr.sprite = f; _fxSr.flipX = flip;
        // 몸 6 을 건드리지 않게 무기·fx 를 같은 쪽 두 칸에: 뒤 = 4/5, 앞 = 7/8 (front fx 가 위 칸)
        int lo = back ? 4 : 7;
        _weaponSr.sortingOrder = _fxInfo.front ? lo : lo + 1;
        _fxSr.sortingOrder = _fxInfo.front ? lo + 1 : lo;
        if (_fxMat != null) _fxMat.mainTexture = f.texture;
    }
}
}
