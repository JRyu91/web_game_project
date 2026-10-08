// 런타임 액터(플레이어·몬스터·유령) 공통 표시: 정수 배율(데이터), 불투명 bbox 하단 발 정렬(프레임마다),
// 접촉 블롭 그림자, 시간대 char 틴트, 1px 외곽선 셰이더, bbox 기준 콜라이더/반폭(사거리 판정용).
using System.Collections.Generic;
using UnityEngine;
using Game.Data;

namespace Game.Rendering {

// 스프라이트 불투명 bbox(스프라이트 로컬 유닛, 피벗 기준). 텍스처 Readable 필요(PixelImport).
public static class SpriteBBox {
    static readonly Dictionary<Sprite, Rect> _cache = new Dictionary<Sprite, Rect>();

    public static Rect Get(Sprite s) {
        if (s == null) return default;
        if (_cache.TryGetValue(s, out var r)) return r;
        var tr = s.textureRect;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        if (s.texture.isReadable) {
            var px = s.texture.GetPixels32();
            int tw = s.texture.width;
            for (int y = (int)tr.yMin; y < (int)tr.yMax; y++)
                for (int x = (int)tr.xMin; x < (int)tr.xMax; x++)
                    if (px[y * tw + x].a > 0) {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
        }
        if (x1 < 0) {
            var b = s.bounds; r = Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y);
        } else {
            float ppu = s.pixelsPerUnit, ox = tr.xMin + s.pivot.x, oy = tr.yMin + s.pivot.y;
            r = Rect.MinMaxRect((x0 - ox) / ppu, (y0 - oy) / ppu, (x1 + 1 - ox) / ppu, (y1 + 1 - oy) / ppu);
        }
        _cache[s] = r;
        return r;
    }
}

// Resources/Config/actor_scale.json — 정수 배율 표. 사용자가 존별로 보고 조정(USER 결정 대기, 기본 전부 1).
public static class ActorScale {
    [System.Serializable] class Cfg { public int player = 1; public float[] tiers = new float[0]; public string playerRoot = "Sprites/Characters", weaponRoot = "Sprites/Weapons", monsterBakedRoot = "Sprites/MonstersBaked"; }
    static Cfg _cfg;
    static Cfg C => _cfg ??= JsonUtility.FromJson<Cfg>(Resources.Load<TextAsset>("Config/actor_scale")?.text ?? "{}");
    public static int Player => Mathf.Max(1, C.player);
    public static string PlayerRoot => C.playerRoot;   // 미리 리샘플한 플레이어 스프라이트(bake_player_scale.py)
    public static string WeaponRoot => C.weaponRoot;
    static float Raw(int tier) => tier >= 1 && tier <= C.tiers.Length ? C.tiers[tier - 1] : 1f;
    static bool IsInt(float f) => Mathf.Abs(f - Mathf.Round(f)) < 1e-4f && f >= 1f;
    // 정수 배율 = 런타임 localScale(Point 라 NxN 블록), 비정수 = bake_player_scale.py 가 미리 구운 폴더를 1x 로
    public static int Tier(int tier) => IsInt(Raw(tier)) ? Mathf.RoundToInt(Raw(tier)) : 1;
    public static string MonsterRoot(int tier) => IsInt(Raw(tier)) ? $"Sprites/Monsters/t{tier}" : $"{C.monsterBakedRoot}/t{tier}";
}

public class ActorVisual : MonoBehaviour {
    public static readonly List<ActorVisual> All = new List<ActorVisual>();
    static Shader _shader;
    Material _mat;
    static readonly Dictionary<int, Sprite> _shadowSprites = new Dictionary<int, Sprite>();

    public SpriteRenderer Body;
    public int Scale = 1;
    public bool IsMonster;
    public float Lift;                               // 점프 등 발라인 기준 상대 오프셋(유닛)
    public float HalfWidth { get; private set; }     // 월드 유닛, bbox 폭 × 배율 / 2
    BoxCollider2D _collider;
    float _centerOff;                                // bbox 중심 - 피벗 (로컬 x × 배율, 반전 전)
    // 몸(불투명 bbox) 중심의 월드 x — 반전(flipX, scale.x 부호) 반영
    public float BodyX => transform.position.x + _centerOff * Mathf.Sign(transform.localScale.x) * (Body != null && Body.flipX ? -1 : 1);

    SpriteRenderer _shadow;
    SpriteRenderer[] _tinted;
    int _shadowPx;

    public void Init(SpriteRenderer body, int scale, bool monster) {
        Body = body; Scale = Mathf.Max(1, scale); IsMonster = monster;
        float sx = transform.localScale.x < 0 ? -1 : 1;
        transform.localScale = new Vector3(sx * Scale, Scale, 1);
        // 액터마다 머티리얼 1개: 공유하면 URP 2D 배칭이 스프라이트 텍스처를 섞어버림(캡처에서 전원 같은 그림) → mainTexture 직접 지정
        if (_shader == null) _shader = Resources.Load<Shader>("Shaders/ActorSprite");
        if (_shader != null) body.sharedMaterial = _mat = new Material(_shader);

        var bb = SpriteBBox.Get(body.sprite);
        HalfWidth = bb.width * Scale / 2f;
        _centerOff = bb.center.x * Scale;
        var col = GetComponent<BoxCollider2D>();
        if (col == null) col = gameObject.AddComponent<BoxCollider2D>(); // ?? 는 Unity fake-null 에 안 먹힘
        _collider = col; col.isTrigger = true; col.size = bb.size; col.offset = bb.center; // 로컬(배율은 transform 이 반영)

        _tinted = GetComponentsInChildren<SpriteRenderer>(true);
        _shadow = new GameObject("Shadow").AddComponent<SpriteRenderer>();
        _shadow.transform.SetParent(transform, false);
        _shadowPx = Mathf.Max(2, Mathf.RoundToInt(bb.width * 40 * Scale * 0.72f));
        All.Add(this);
        Refresh();
    }

    void OnDestroy() { All.Remove(this); if (_mat != null) Destroy(_mat); }
    float _flash;
    // 피격 흰 플래시(초). 머티리얼이 액터별이라 이 액터만.
    public void Flash(float sec, float amount = 1f) { _flash = sec; _mat?.SetFloat("_Flash", amount); } // amount: 흰색 섞는 비율(큰 보스는 낮게)
    void LateUpdate() {
        if (_flash > 0 && (_flash -= Time.deltaTime) <= 0) _mat?.SetFloat("_Flash", 0);
        Refresh();
    }
    public void TickFlash(float dt) { if (_flash > 0 && (_flash -= dt) <= 0) _mat?.SetFloat("_Flash", 0); } // 캡처 툴용

    // 매 프레임: 현재 프레임 bbox 하단을 발라인에 맞추고 그림자/틴트 갱신.
    public void Refresh() {
        if (Body == null || Body.sprite == null) return;
        var bb = SpriteBBox.Get(Body.sprite);
        // Combat width stays at the initial pose; the debug collider mirrors that footprint and follows the current foot.
        if (_collider != null) {
            _collider.size = new Vector2(_collider.size.x, bb.height);
            _collider.offset = new Vector2((Body.flipX ? -_centerOff : _centerOff) / Scale, bb.center.y);
        }
        var p = transform.position;
        transform.position = new Vector3(p.x, WorldConfig.GroundY + Lift - bb.yMin * Scale, p.z);

        if (_mat != null) _mat.mainTexture = Body.sprite.texture;

        int w = Mathf.RoundToInt(_shadowPx * ZoneController.ShadowWidthMul);
        _shadow.sprite = ShadowSprite(w);
        _shadow.color = new Color(0, 0, 0, (IsMonster ? 0.7f : 0.55f) * ZoneController.ShadowAlphaMul);
        _shadow.sortingOrder = ZoneController.ShadowOrder;
        float footX = p.x + (Body.flipX ? -bb.center.x : bb.center.x) * transform.localScale.x;
        float shift = float.IsNaN(ZoneController.SunWorldX) ? 0 : Mathf.Sign(footX - ZoneController.SunWorldX) * 5;
        float leftPx = Mathf.Round(footX * 40 - w / 2f + shift);
        // 4px 높이, 중심 = 발라인+3px (compose ao GROUND+3). 점프해도 그림자는 지면에.
        _shadow.transform.position = new Vector3(leftPx / 40f, WorldConfig.GroundY - 4 / 40f, 0); // 행 발라인+1 ~ +4
        var ls = transform.localScale;
        _shadow.transform.localScale = new Vector3(1 / ls.x, 1 / ls.y, 1);
        _shadow.flipX = false;
    }

    // 정수 px 블롭(스케일로 늘리지 않음). 피벗 좌하단.
    static Sprite ShadowSprite(int w) {
        w = Mathf.Max(2, w);
        if (_shadowSprites.TryGetValue(w, out var s)) return s;
        var t = new Texture2D(w, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[w * 4];
        int[] inset = { Mathf.Max(1, w / 7), 0, 0, Mathf.Max(1, w / 7) };
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = x >= inset[y] && x < w - inset[y] ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
        t.SetPixels32(px); t.Apply();
        return _shadowSprites[w] = Sprite.Create(t, new Rect(0, 0, w, 4), Vector2.zero, 40);
    }

    // 스펙 2-4: 접촉 거리 = 중심거리 - 반폭 합. 사거리 판정은 전부 이걸로.
    public static float Gap(Transform a, Transform b) {
        var va = a.GetComponent<ActorVisual>(); var vb = b.GetComponent<ActorVisual>();
        float xa = va ? va.BodyX : a.position.x, xb = vb ? vb.BodyX : b.position.x;
        return Mathf.Abs(xa - xb) - (va ? va.HalfWidth : 0) - (vb ? vb.HalfWidth : 0);
    }
}
}
