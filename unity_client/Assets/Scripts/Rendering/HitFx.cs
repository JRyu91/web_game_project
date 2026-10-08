using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Rendering {
// 타격감·연출 공용: 1회 재생 스프라이트 FX(PixelLab Sprites/FX/<key>/anim1), 데미지 숫자(UI/font_dmg_*), 화면 흔들림.
// 각 연출은 자기 GameObject 가 끝나면 스스로 사라진다(풀링 없음 — 동시 수십 개 수준).
public sealed class HitFx : MonoBehaviour {
    static readonly Dictionary<string, Sprite[]> Cache = new Dictionary<string, Sprite[]>();
    // PixelLab 프레임은 Multiple 자동 분할로 한 장이 여러 조각(frame_03_0, _1 …)이라 조각을 그대로 돌리면 깨진다 → 텍스처 1장 = 프레임 1장(전체 캔버스, 중앙 피벗)
    public static Sprite[] Frames(string path) {
        if (Cache.TryGetValue(path, out var f)) return f;
        var parts = Resources.LoadAll<Sprite>(path);
        f = parts.Select(s => s.texture).Distinct().OrderBy(t => t.name, System.StringComparer.Ordinal)
            .Select(t => { var p = parts.First(s => s.texture == t); return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(.5f, .5f), p.pixelsPerUnit); }).ToArray();
        return Cache[path] = f;
    }

    Sprite[] _frames; SpriteRenderer _sr; float _t, _life, _fps; Vector3 _rise; SpriteRenderer[] _fade;
    Transform _follow; Vector3 _offset; // 캐릭터 따라다니는 연출(레벨업·회복)

    // 스프라이트 FX 1회 재생. pos = 바닥 중앙이 아니라 스프라이트 피벗 기준(대부분 Center).
    public static GameObject Play(string key, Vector3 pos, float fps = 14f, float scale = 1f, bool flipX = false, int order = 11, Transform follow = null) {
        if (!Application.isPlaying) return null; // 에디터 회귀/밸런스 시뮬은 Update 가 안 돌아 연출이 영영 안 지워짐
        var frames = Frames("Sprites/FX/" + key + "/anim1");
        if (frames.Length == 0) frames = Frames("Sprites/FX/" + key); // fx_boss_warning 처럼 평면 폴더
        if (frames.Length == 0) return null;
        var fx = new GameObject("Fx_" + key).AddComponent<HitFx>();
        fx.transform.position = pos; fx.transform.localScale = Vector3.one * scale;
        fx._sr = fx.gameObject.AddComponent<SpriteRenderer>(); fx._sr.sortingOrder = order; fx._sr.flipX = flipX; fx._sr.sprite = frames[0];
        fx._frames = frames; fx._fps = fps; fx._life = frames.Length / fps;
        if (follow != null) { fx._follow = follow; fx._offset = pos - follow.position; }
        return fx.gameObject;
    }

    // 데미지/회복 숫자: 글자 스프라이트 이어붙여 0.7초 동안 위로 뜨며 사라짐. font = normal / normal_yellow / heal / crit
    public static GameObject Number(int value, Vector3 pos, string font = "normal", float scale = 1f) {
        if (!Application.isPlaying) return null;
        var digits = Frames("Sprites/UI/font_dmg_" + font);
        if (digits.Length < 10 || value <= 0) return null;
        var root = new GameObject("DmgNumber").AddComponent<HitFx>();
        string s = value.ToString();
        float w = digits[0].bounds.size.x * 0.8f; // 글자 간격(테두리 겹침)
        var list = new List<SpriteRenderer>();
        for (int i = 0; i < s.Length; i++) {
            var d = new GameObject(s[i].ToString()).AddComponent<SpriteRenderer>();
            d.transform.SetParent(root.transform, false); d.transform.localPosition = new Vector3((i - (s.Length - 1) * 0.5f) * w, 0, 0);
            d.sprite = digits[s[i] - '0']; d.sortingOrder = 12; list.Add(d);
        }
        root.transform.position = pos + new Vector3(Random.Range(-0.15f, 0.15f), 0, 0); root.transform.localScale = Vector3.one * scale;
        root._fade = list.ToArray(); root._life = 0.7f; root._rise = new Vector3(0, 0.7f, 0);
        return root.gameObject;
    }

    void Update() => Tick(Time.deltaTime);
    public void Tick(float dt) {
        _t += dt;
        if (_follow != null) transform.position = _follow.position + _offset;
        if (_frames != null) { int f = (int)(_t * _fps); if (f < _frames.Length) _sr.sprite = _frames[f]; }
        if (_fade != null) {
            transform.position += _rise * dt / _life;
            float a = Mathf.Clamp01(2f * (1f - _t / _life)); // 후반 절반만 페이드
            foreach (var r in _fade) r.color = new Color(1, 1, 1, a);
        }
        if (_t >= _life) { if (Application.isPlaying) Destroy(gameObject); else DestroyImmediate(gameObject); }
    }

    // 화면 흔들림: 픽셀 아트라 정수 px(1/40u) 단위. 카메라 추적이 위치를 다시 잡아도 LateUpdate 마지막에 더하고 다음 Update 에 뺀다.
    public static void Shake(float px, float sec) {
        var cam = Camera.main; if (cam == null || !Application.isPlaying) return;
        var sh = cam.GetComponent<CameraShake>() ?? cam.gameObject.AddComponent<CameraShake>();
        sh.Begin(px, sec);
    }
}

[DefaultExecutionOrder(10000)]
public sealed class CameraShake : MonoBehaviour {
    float _px, _left; Vector3 _applied;
    public void Begin(float px, float sec) { _px = Mathf.Max(_px, px); _left = Mathf.Max(_left, sec); }
    void Update() { transform.position -= _applied; _applied = Vector3.zero; }
    void LateUpdate() {
        if (_left <= 0) { _px = 0; return; }
        _left -= Time.deltaTime;
        int p = Mathf.Max(1, Mathf.RoundToInt(_px));
        _applied = new Vector3(Random.Range(-p, p + 1), Random.Range(-p, p + 1), 0) / 40f;
        transform.position += _applied;
    }
}
}
