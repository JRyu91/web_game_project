// Resources/Sprites/<key>/<clip>/frame_NN.png 로 슬라이스된 프레임들을 재생하는 최소 상태머신.
// field/animfsm.js 의 유니티 버전 — 같은 clip 이름(idle/walk/attack/attack1/attack2/hurt/dead)을 그대로 쓴다.
// Stage 3: 클립 정의 {frames(원본 인덱스), ms(프레임별), impact(원본 인덱스), loop} — 파일은 두고 인덱스만 고른다.
// 정의는 Resources/Config/clip_table.json (key = "main_m/attack"). 정의 없는 클립은 전 프레임 균등(Fps).
using System.Collections.Generic;
using UnityEngine;

namespace Game.Rendering {

[System.Serializable]
public class ClipDef {
    public string key;
    public int[] frames;
    public int[] ms;
    public int impact = -1;   // 원본 인덱스, -1 = 없음
    public bool loop;
}

public static class ClipTable {
    [System.Serializable] class File { public ClipDef[] clips; }
    static Dictionary<string, ClipDef> _map;
    public static void Reload() => _map = null;
    public static ClipDef Get(string key) {
        if (_map == null) {
            _map = new Dictionary<string, ClipDef>();
            var t = Resources.Load<TextAsset>("Config/clip_table");
            if (t != null) foreach (var c in JsonUtility.FromJson<File>(t.text).clips) _map[c.key] = c;
        }
        return _map.TryGetValue(key, out var d) ? d : null;
    }
}

public class FrameAnimator : MonoBehaviour {
    public SpriteRenderer Renderer;
    public float Fps = 10f;

    string _resourceRoot;                 // 예: "Sprites/Monsters/t1"
    readonly Dictionary<string, Sprite[]> _cache = new Dictionary<string, Sprite[]>();
    string _clip;
    Sprite[] _src;
    int[] _idx;                           // 재생할 원본 인덱스
    float[] _dur;                         // 초
    int _impactPos = -1;                  // _idx 내 위치
    int _frame;
    float _timer;
    bool _loop = true;
    bool _done;
    float _freeze;                        // 히트스톱 남은 시간(이 애니만 정지, timeScale 무관)
    System.Action _onDone;

    public bool Done => _done;
    public bool Frozen => _freeze > 0;
    public string Clip => _clip;
    public int SourceIndex => _idx != null && _idx.Length > 0 ? _idx[_frame] : -1;
    public event System.Action OnImpact;  // impact 프레임 진입 시 1회

    void Awake() {
        if (Renderer == null) Renderer = GetComponent<SpriteRenderer>();
    }

    public void SetSource(string resourceRoot) {
        _resourceRoot = resourceRoot;
        _cache.Clear();
        _clip = null;
    }

    public string SourceKey => _resourceRoot == null ? "" : _resourceRoot.Substring(_resourceRoot.LastIndexOf('/') + 1);

    Sprite[] LoadClip(string clip) {
        if (_cache.TryGetValue(clip, out var s)) return s;
        var loaded = Resources.LoadAll<Sprite>($"{_resourceRoot}/{clip}");
        // frame_00, frame_01 ... 이름순 정렬 보정 (LoadAll 순서가 항상 보장되진 않음)
        System.Array.Sort(loaded, (a, b) => string.CompareOrdinal(a.name, b.name));
        _cache[clip] = loaded;
        return loaded;
    }

    public int FrameCount(string clip) => LoadClip(clip).Length;
    public void Freeze(float sec) => _freeze = Mathf.Max(_freeze, sec); // 누적 안 함

    // 정의 테이블(SourceKey/clip)이 있으면 그걸, 없으면 균등 fps. fps>0 이면 균등 재생 속도 덮어씀.
    // impactFrac>=0 이면 (정의 없는 클립) 그 비율 지점 프레임을 impact 로.
    public void Play(string clip, bool loop = true, System.Action onDone = null, float fps = 0, float impactFrac = -1) {
        if (_clip == clip && !_done && _still < 0) return;
        var frames = LoadClip(clip);
        if (frames.Length == 0) {
            Debug.LogWarning($"[anim] no frames for {_resourceRoot}/{clip}");
            return;
        }
        var def = ClipTable.Get($"{SourceKey}/{clip}");
        if (def != null) {
            _idx = def.frames; _dur = new float[def.ms.Length];
            for (int i = 0; i < _dur.Length; i++) _dur[i] = def.ms[i] / 1000f;
            _impactPos = System.Array.IndexOf(_idx, def.impact);
        } else {
            _idx = new int[frames.Length]; _dur = new float[frames.Length];
            float d = 1f / (fps > 0 ? fps : Fps);
            for (int i = 0; i < _idx.Length; i++) { _idx[i] = i; _dur[i] = d; }
            _impactPos = impactFrac >= 0 ? Mathf.Min(frames.Length - 1, Mathf.FloorToInt(frames.Length * impactFrac)) : -1;
        }
        _src = frames; _clip = clip; _loop = loop; _frame = 0; _timer = 0; _done = false; _onDone = onDone; _still = -1;
        Show();
    }

    int _still = -1;
    // 한 프레임 고정(공격 사이 idle 0 유지 등). 루프 재생 안 함.
    public void Still(string clip, int srcIndex) {
        var frames = LoadClip(clip);
        if (frames.Length == 0) return;
        _src = frames; _clip = clip; _idx = new[] { Mathf.Min(srcIndex, frames.Length - 1) }; _dur = new[] { 1f };
        _impactPos = -1; _frame = 0; _done = true; _onDone = null; _still = srcIndex;
        Show();
    }
    public bool IsStill => _still >= 0;

    // 공격 간격이 클립보다 짧으면 impact 이후(회수) 프레임만 뒤에서부터 잘라낸다.
    public void TrimTo(float maxSec) {
        if (_idx == null) return;
        float total = 0; foreach (var d in _dur) total += d;
        int n = _idx.Length;
        while (total > maxSec && n - 1 > _impactPos && n > 1) { n--; total -= _dur[n]; }
        if (n < _idx.Length) { System.Array.Resize(ref _idx, n); System.Array.Resize(ref _dur, n); }
    }

    public float Length { get { float t = 0; if (_dur != null) foreach (var d in _dur) t += d; return t; } }

    void Show() {
        Renderer.sprite = _src[Mathf.Min(_idx[_frame], _src.Length - 1)];
        if (_frame == _impactPos) OnImpact?.Invoke();
    }

    void Update() => Tick(Time.deltaTime);

    // 누적 시간 방식: 한 번에 여러 프레임을 건너뛸 수 있다. 캡처 툴이 직접 호출하기도 함.
    public void Tick(float dt) {
        if (_clip == null || _done) return;
        if (_freeze > 0) {
            float used = Mathf.Min(_freeze, dt);
            _freeze -= used; dt -= used;
            if (dt <= 0) return;
        }
        _timer += dt;
        while (!_done && _timer >= _dur[_frame]) {
            _timer -= _dur[_frame];
            if (_frame + 1 >= _idx.Length) {
                if (_loop) { _frame = 0; Show(); }
                else { _done = true; _onDone?.Invoke(); }
            } else { _frame++; Show(); }
            if (_freeze > 0) { _timer = 0; break; } // impact 에서 히트스톱이 걸리면 남은 시간 버림
        }
    }

    public void FlipX(bool flip) => Renderer.flipX = flip;

    // 크기 정규화/발 정렬은 ActorVisual 담당(정수 배율 + 불투명 bbox 하단 기준, 프레임마다).
}
}
