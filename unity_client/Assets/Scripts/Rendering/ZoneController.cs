// 존 배선(spec Stage 1): manifest 레이어 → 패럴랙스 + 가로 랩(레이어마다 2장), 카메라 추적/맵 경계 클램프,
// 시간대 틴트(SpriteRenderer.color 근사), 하늘 교체/그라데이션, 발광 레이어, 용암폭포 8프레임, 불씨 파티클.
// 좌표: 1유닛=40px, 카메라 y=0 → 화면 px y 는 월드 (360-y)/40. 카메라 좌측=맵 800px(camX=36)에서
// 모든 레이어 오프셋이 0 → art_v4 view_*_1x.png(맵 800~2080 크롭)과 동일 구도.
using System;
using System.Collections.Generic;
using UnityEngine;
using Game.Data;

namespace Game.Rendering {

public enum Zone { A, B, C }
public enum Tod { Day, Dawn, Night }

public class ZoneController : MonoBehaviour {
    public static ZoneController Current { get; private set; }
    // 런타임 액터가 읽는 현재 존/시간대 룩
    public static float ShadowAlphaMul = 1f, ShadowWidthMul = 1f;
    public static int ShadowOrder = -20;
    public static float SunWorldX = float.NaN;
    public static event Action<Zone> OnZoneChanged;
    public static float AnimTime = -1f;               // >=0: 캡처용 고정 시각
    public static float Now => AnimTime >= 0 ? AnimTime : Time.time;

    static readonly int[] FootPx = { 618, 596, 594 };
    const float REF_CAM_X = 36f;                      // (800 + 640) / 40
    const float HALF_VIEW = 16f;                      // 1280px / 2 / 40

    public Zone Zone { get; private set; } = Zone.A;
    public Tod Tod { get; private set; } = Tod.Day;
    public float CamX { get; private set; } = REF_CAM_X;
    public float? CameraXOverride;                    // 캡처: 클램프 없이 이 위치
    public Camera Cam;

    class Layer {
        public string name;
        public float p;
        public Transform[] copies;                    // 3200px 랩 레이어(2장)
        public Transform single;                      // 해/달 같은 단일 스프라이트
        public Vector2 mapPos;                        // single: 맵 px 좌상단
        public bool skyFixed;                         // 카메라 고정 + 화면폭으로 늘림
        public Tod[] tods;                            // null = 항상
    }
    readonly List<Layer> _layers = new List<Layer>();
    readonly List<(SpriteRenderer sr, int off)> _falls = new List<(SpriteRenderer, int)>();
    readonly List<Ember> _embers = new List<Ember>();
    Sprite[] _fallFrames;

    [Serializable] class ManLayer { public string name, file; public float parallax; public int sortingOrder; public string[] tod; }
    [Serializable] class Man { public ManLayer[] layers; }

    void Awake() { Current = this; }

    void Start() { if (_layers.Count == 0) SetZone(Zone.A, Tod.Day); }

    void Update() {
#if UNITY_EDITOR
        // Runtime map changes require an authoritative server response.
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (kb.f1Key.wasPressedThisFrame) SetZone(Zone.A, Tod);
        if (kb.f2Key.wasPressedThisFrame) SetZone(Zone.B, Tod);
        if (kb.f3Key.wasPressedThisFrame) SetZone(Zone.C, Tod);
        if (kb.tKey.wasPressedThisFrame) SetTod((Tod)(((int)Tod + 1) % 3));
#endif
    }

    void LateUpdate() {
        var player = Game.Gameplay.PlayerController.Local;
        Layout(player != null ? player.transform.position.x : (float?)null);
    }

    static float Snap(float u) => Mathf.Round(u * 40f) / 40f;

    // ── 존 전환 ─────────────────────────────────────────────
    public void SetZone(Zone zone, Tod tod) {
        Current = this;
        if (Cam == null) Cam = Camera.main;
        Zone = zone;
        for (int i = transform.childCount - 1; i >= 0; i--) Kill(transform.GetChild(i).gameObject);
        _layers.Clear(); _falls.Clear(); _embers.Clear();
        WorldConfig.GroundY = (360 - FootPx[(int)zone] - 1) / 40f; // 발 최하단 불투명 픽셀 행 = 발라인 y (spec 1-11 #4)

        string root = $"Sprites/Zones/{zone}";
        var man = JsonUtility.FromJson<Man>(Resources.Load<TextAsset>($"{root}/manifest").text);
        foreach (var m in man.layers) {
            if (string.IsNullOrEmpty(m.file)) continue; // A "characters" = 런타임 액터
            string key = m.name;
            int order = m.sortingOrder;
            if (zone == Zone.C) order = key switch { "ground" => -5, "shadows" => -4, "props_back" => -3, _ => order }; // spec 1-2 C 재배치
            if (key == "shadows") ShadowOrder = order;
            Tod[] tods = m.tod != null && m.tod.Length > 0 ? Array.ConvertAll(m.tod, t => (Tod)Enum.Parse(typeof(Tod), t, true)) : null;
            if (zone == Zone.A && key == "sun") tods = new[] { Tod.Dawn };
            if (zone == Zone.A && key == "moon") tods = new[] { Tod.Night };

            var L = new Layer { name = key, p = m.parallax, tods = tods };
            if (key == "sky") {
                L.skyFixed = true;
                L.single = MakeSr(key, Resources.Load<Sprite>($"{root}/{System.IO.Path.GetFileNameWithoutExtension(m.file)}"), order).transform;
            } else if (key == "sun" || key == "moon") {
                // 맵 좌표 좌상단 px — A: manifest note, B: compose_final.py 실제 합성값(W*.47,300)/(W*.30,90)
                L.mapPos = zone == Zone.A ? (key == "sun" ? new Vector2(1330, 330) : new Vector2(1340, 150))
                                          : (key == "sun" ? new Vector2(1504, 300) : new Vector2(960, 90));
                L.single = MakeSr(key, Resources.Load<Sprite>($"{root}/{key}"), order).transform;
            } else {
                var spr = key == "embers" ? null : Resources.Load<Sprite>($"{root}/{key}"); // C 불씨 = 파티클로 대체
                L.copies = new Transform[2];
                for (int k = 0; k < 2; k++) {
                    var sr = MakeSr($"{key}_{k}", spr, order);
                    L.copies[k] = sr.transform;
                    if (key == "lavafalls") AddFalls(sr.transform, order + 1);
                    if (key == "embers") AddEmbers(sr.transform, order, k);
                }
            }
            _layers.Add(L);
        }
        if (zone == Zone.A) { // 별: manifest "procedural" → 1px 점 50개, 하늘 고정
            var st = new Layer { name = "stars", p = 0, skyFixed = true, tods = new[] { Tod.Night } };
            st.single = MakeSr("stars", StarsSprite(), -95).transform;
            _layers.Add(st);
        }
        if (Cam != null) Cam.backgroundColor = Color.black;
        SetTod(zone == Zone.C ? Tod.Day : tod);
        OnZoneChanged?.Invoke(zone);
    }

    public void SetTod(Tod tod) {
        Tod = Zone == Zone.C ? Tod.Day : tod;
        foreach (var L in _layers) {
            bool on = L.tods == null || Array.IndexOf(L.tods, Tod) >= 0;
            foreach (var sr in Renderers(L)) {
                sr.gameObject.SetActive(on);
                ApplyGrade(sr, GradeOf(L.name));
                sr.color = new Color(1, 1, 1, Zone == Zone.B && L.name == "clouds" && Tod == Tod.Night ? 0.3f : 1f); // compose: 밤 구름 alpha x.3
            }
            if (L.name == "sky") L.single.GetComponent<SpriteRenderer>().sprite = SkySprite();
        }
        var cg = GradeOf("char");
        Shader.SetGlobalVector("_CharGrade", cg == null ? Vector4.zero : new Vector4(cg.sat, cg.br, 0, 0));
        Shader.SetGlobalVector("_CharTint", cg == null ? Vector4.zero : new Vector4(cg.tint[0], cg.tint[1], cg.tint[2], cg.a));
        ShadowAlphaMul = Tod == Tod.Dawn ? 0.85f : Tod == Tod.Night ? 0.5f : 1f;
        ShadowWidthMul = Tod == Tod.Dawn ? 1.35f : 1f;
        Color ol = Zone == Zone.C ? new Color32(230, 140, 90, 110)
                 : Zone == Zone.B && Tod == Tod.Night ? new Color(150 / 255f, 180 / 255f, 230 / 255f, 0.8f)
                 : Color.clear;
        Shader.SetGlobalColor("_ActorOutline", ol);
        Layout(null);
    }

    IEnumerable<SpriteRenderer> Renderers(Layer L) {
        if (L.single != null) yield return L.single.GetComponent<SpriteRenderer>();
        if (L.copies != null) foreach (var t in L.copies) yield return t.GetComponent<SpriteRenderer>();
    }

    // ── 시간대 그레이딩 (USER 결정 b): compose grade 와 같은 식을 셰이더로. 값 = Config/tod_grade.json
    // (Tools/gen_tod_grade.py). 표에 없는 레이어 = 원본 그대로(발광/하늘/그림자/낮).
    [Serializable] class GradeEntry { public string k; public float[] tint; public float a, sat, br; }
    [Serializable] class GradeTable { public GradeEntry[] entries; }
    static Dictionary<string, GradeEntry> _grades;
    static Shader _gradeShader;
    static Material _defaultMat;

    GradeEntry GradeOf(string layer) {
        if (_grades == null) {
            _grades = new Dictionary<string, GradeEntry>();
            foreach (var e in JsonUtility.FromJson<GradeTable>(Resources.Load<TextAsset>("Config/tod_grade").text).entries) _grades[e.k] = e;
        }
        return _grades.TryGetValue($"{Zone}/{Tod.ToString().ToLower()}/{layer}", out var g) ? g : null;
    }

    void ApplyGrade(SpriteRenderer sr, GradeEntry g) {
        if (_defaultMat == null) _defaultMat = sr.sharedMaterial;          // 최초 = 기본 스프라이트 머티리얼
        if (g == null) { if (sr.sharedMaterial != _defaultMat) sr.sharedMaterial = _defaultMat; return; }
        if (_gradeShader == null) _gradeShader = Resources.Load<Shader>("Shaders/GradeSprite");
        var m = sr.sharedMaterial;
        if (m == null || m.shader != _gradeShader) sr.sharedMaterial = m = new Material(_gradeShader); // 레이어마다 1개(텍스처 섞임 방지)
        m.mainTexture = sr.sprite != null ? sr.sprite.texture : null;
        m.SetColor("_Tint", new Color(g.tint[0], g.tint[1], g.tint[2], g.a));
        m.SetFloat("_Sat", g.sat); m.SetFloat("_Br", g.br);
    }

    // A: 파일 교체(틴트 금지). B: 낮 = sky.png, 새벽/밤 = compose 가 다시 칠하는 그라데이션을 런타임 생성.
    Sprite SkySprite() {
        string root = $"Sprites/Zones/{Zone}";
        if (Zone == Zone.A) return Resources.Load<Sprite>($"{root}/sky_{Tod.ToString().ToLower()}");
        if (Tod == Tod.Day) return Resources.Load<Sprite>($"{root}/sky");
        Color32 top = Tod == Tod.Dawn ? new Color32(74, 104, 178, 255) : new Color32(7, 10, 34, 255);
        Color32 bot = Tod == Tod.Dawn ? new Color32(255, 180, 116, 255) : new Color32(30, 40, 86, 255);
        const int H = 596;
        var t = new Texture2D(1, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < H; y++) { // compose sky_strip: t = (y/(H-1))^1.4, int 절삭
            float k = Mathf.Pow(y / (float)(H - 1), 1.4f);
            byte L(byte a, byte b) => (byte)(int)(a + (b - a) * k);
            t.SetPixel(0, H - 1 - y, new Color32(L(top.r, bot.r), L(top.g, bot.g), L(top.b, bot.b), 255));
        }
        t.Apply();
        return Sprite.Create(t, new Rect(0, 0, 1, H), new Vector2(0.5f, 0.5f), 40);
    }

    static Sprite StarsSprite() {
        var t = new Texture2D(1280, 400, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        t.SetPixels32(new Color32[1280 * 400]);
        var rng = new System.Random(7);
        for (int i = 0; i < 50; i++)
            t.SetPixel(rng.Next(1280), rng.Next(400), rng.Next(3) == 0 ? new Color32(190, 215, 255, 255) : new Color32(255, 255, 255, 230));
        t.Apply();
        return Sprite.Create(t, new Rect(0, 0, 1280, 400), new Vector2(0.5f, 0.5f), 40);
    }

    // ── 카메라 + 패럴랙스 배치 ─────────────────────────────
    // follow: 플레이어 x. 플레이어를 화면 좌측 1/3 에 두고 맵 [0,80] 안으로 클램프.
    public void Layout(float? followX) {
        float halfHeight = Cam != null ? Cam.orthographicSize : 9f;
        if (Cam != null) {
            var pixel = Cam.GetComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();
            if (pixel != null && pixel.enabled && pixel.cropFrame == UnityEngine.Rendering.Universal.PixelPerfectCamera.CropFrame.None && pixel.gridSnapping != UnityEngine.Rendering.Universal.PixelPerfectCamera.GridSnapping.UpscaleRenderTexture) {
                // URP updates this projection during rendering, after LateUpdate; predict its current viewport size.
                int zoom = Mathf.Max(1, Mathf.Min(Cam.pixelWidth / pixel.refResolutionX, Cam.pixelHeight / pixel.refResolutionY));
                halfHeight = Cam.pixelHeight / (2f * zoom * pixel.assetsPPU);
                Cam.orthographicSize = halfHeight;
            }
        }
        float halfView = Cam != null ? Mathf.Min(WorldConfig.MapWidth / 2, halfHeight * Cam.aspect) : HALF_VIEW;
        // Keep each zone's footline at its original 720px proportion, including short landscape viewports.
        float cameraY = WorldConfig.GroundY * (1f - halfHeight / 9f);
        if (CameraXOverride.HasValue) CamX = Snap(CameraXOverride.Value);
        else if (followX.HasValue) CamX = Snap(Mathf.Clamp(followX.Value + halfView / 3f, halfView, WorldConfig.MapWidth - halfView));
        if (Cam != null) Cam.transform.position = new Vector3(CamX, Snap(cameraY), -10);
        SunWorldX = float.NaN;

        foreach (var L in _layers) {
            float off = Snap((CamX - REF_CAM_X) * (1 - L.p)); // p=1 월드 고정, p=0 화면 고정
            if (L.skyFixed) {
                var sr = L.single.GetComponent<SpriteRenderer>();
                if (sr.sprite == null) continue;
                var r = sr.sprite.rect;
                L.single.localScale = new Vector3(halfView * 80f / r.width, 1, 1);
                L.single.position = new Vector3(CamX, Snap(cameraY) + halfHeight - r.height / 80f, 0);
            } else if (L.single != null) {
                var r = L.single.GetComponent<SpriteRenderer>().sprite.rect;
                float cx = off + (L.mapPos.x + r.width / 2f) / 40f;
                L.single.position = new Vector3(cx, (360 - L.mapPos.y - r.height / 2f) / 40f, 0);
                if (L.name == "sun" && L.single.gameObject.activeSelf) SunWorldX = cx;
            } else {
                float n = Mathf.Floor((CamX - halfView - off) / WorldConfig.MapWidth);
                for (int k = 0; k < 2; k++)
                    L.copies[k].position = new Vector3(off + WorldConfig.MapWidth * (n + k) + 40f, 0, 0);
            }
        }
        AnimateFx();
    }

    // ── C: 용암폭포 8프레임(10fps) + 불씨 ─────────────────
    static readonly int[] FallX = { 300, 1270, 2620 };

    void AddFalls(Transform copy, int order) {
        if (_fallFrames == null) {
            _fallFrames = Resources.LoadAll<Sprite>("Sprites/Zones/C/lavafall_frames");
            Array.Sort(_fallFrames, (a, b) => string.CompareOrdinal(a.name, b.name));
        }
        int[] offs = { 0, 3, 5 };
        for (int i = 0; i < FallX.Length; i++) {
            var sr = MakeSr($"fall{i}", _fallFrames[0], order);
            sr.transform.SetParent(copy, false);
            // 프레임 170x420: 바닥(웅덩이)이 lavafalls.png 폭포 bbox 하단(y=590)과 일치 → top=170
            sr.transform.localPosition = new Vector3((FallX[i] - 1600) / 40f, (360 - (170 + 210)) / 40f, 0);
            _falls.Add((sr, offs[i]));
        }
    }

    class Ember { public Transform t; public SpriteRenderer sr; public float x, y, speed, life, phase, sway; }
    static Sprite[] _emberSprites;

    void AddEmbers(Transform copy, int order, int seed) {
        if (_emberSprites == null) {
            Color32[] cores = { new Color32(255, 120, 40, 255), new Color32(240, 80, 30, 255), new Color32(255, 150, 56, 255) };
            _emberSprites = Array.ConvertAll(cores, c => {
                var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                var halo = new Color32(200, 56, 22, 70);
                var px = new Color32[16];
                for (int i = 0; i < 16; i++) { int x = i % 4, y = i / 4; bool core = x is 1 or 2 && y is 1 or 2, corner = (x is 0 or 3) && (y is 0 or 3); px[i] = core ? c : corner ? default : halo; }
                t.SetPixels32(px); t.Apply();
                return Sprite.Create(t, new Rect(0, 0, 4, 4), Vector2.zero, 40);
            });
        }
        var rng = new System.Random(11 + seed);
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        for (int i = 0; i < 62; i++) {   // 폭포 3곳 x±40 에 16개씩 + 맵 전체 14개
            float x = i < 48 ? FallX[i / 16] + R(-40, 40) : R(0, 3200);
            var sr = MakeSr("ember", _emberSprites[rng.Next(3)], order);
            sr.transform.SetParent(copy, false);
            _embers.Add(new Ember { t = sr.transform, sr = sr, x = x, y = R(290, 430), speed = R(8, 20), life = R(2, 4), phase = R(0, 4), sway = R(0, 6.28f) });
        }
    }

    void AnimateFx() {
        float now = Now;
        if (_fallFrames != null && _fallFrames.Length > 0)
            foreach (var (sr, off) in _falls) sr.sprite = _fallFrames[(Mathf.FloorToInt(now * 10f) + off) % _fallFrames.Length];
        foreach (var e in _embers) {
            float age = (now + e.phase) % e.life;
            float px = Mathf.Round(e.x + 3f * Mathf.Sin(now * 1.7f + e.sway));
            float py = Mathf.Round(e.y - e.speed * age);          // 위로 (px y 감소), 최대 80px → 밴드 200~430
            e.t.localPosition = new Vector3((px - 1600) / 40f, (360 - py) / 40f, 0);
            e.sr.color = new Color(1, 1, 1, Mathf.Clamp01(Mathf.Min(age / 0.3f, (e.life - age) / 0.8f)));
        }
    }

    SpriteRenderer MakeSr(string name, Sprite s, int order) {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s; sr.sortingOrder = order;
        return sr;
    }

    static void Kill(GameObject go) { if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }
}
}
