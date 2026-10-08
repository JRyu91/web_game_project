using UnityEngine;

namespace Game.Rendering {
public class ActorNameplate : MonoBehaviour {
    TextMesh _label;
    TextMesh[] _outline;
    SpriteRenderer _body;
    MeshRenderer _mesh;
    static readonly System.Collections.Generic.List<ActorNameplate> All = new System.Collections.Generic.List<ActorNameplate>();

    public void Init(string text, Color color) {
        _body = GetComponent<SpriteRenderer>();
        _label = new GameObject("Nameplate").AddComponent<TextMesh>();
        _label.transform.SetParent(transform, false);
        _label.richText = false;
        _label.anchor = TextAnchor.LowerCenter; _label.fontSize = 12; _label.characterSize = 0.3f; _label.color = color;
        var font = Resources.Load<Font>("Fonts/Galmuri11");
        if (font != null) { _label.font = font; _label.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
        _label.GetComponent<MeshRenderer>().sortingOrder = 10;
        // 밝은 하늘 배경 대비: 1px(1/40u) 검은 외곽선 = 같은 글자 4장을 뒤에 깐다
        _outline = new TextMesh[4];
        for (int i = 0; i < 4; i++) {
            var o = Instantiate(_label); o.transform.SetParent(_label.transform, false); o.name = "Outline"; // 부모 지정 복제는 앞 외곽선까지 같이 복제됨
            o.color = Color.black; o.transform.localPosition = new Vector3(i < 2 ? (i == 0 ? -1 : 1) : 0, i < 2 ? 0 : (i == 2 ? -1 : 1), 0) * (1f / 40);
            o.GetComponent<MeshRenderer>().sortingOrder = 9; _outline[i] = o;
        }
        _mesh = _label.GetComponent<MeshRenderer>(); All.Add(this);
        SetText(text); LateUpdate();
    }

    public void SetText(string text) { if (_label == null) return; _label.text = text; foreach (var o in _outline) o.text = text; }
    void LateUpdate() {
        if (_label == null || _body == null || _body.sprite == null) return;
        var bounds = SpriteBBox.Get(_body.sprite);
        _label.transform.localPosition = new Vector3(_body.flipX ? -bounds.center.x : bounds.center.x, bounds.yMax + 0.2f, 0);
        // 같은 자리에 선 캐릭터끼리 이름표가 겹치면 먼저 생긴 이름표 위로 한 줄씩 쌓는다(목록 순서 = 생성 순서)
        for (int i = 0, guard = 0, idx = All.IndexOf(this); i < idx && guard < 8; i++) {
            var other = All[i];
            if (other == null || other._mesh == null || !other._mesh.enabled || !other.gameObject.activeInHierarchy) continue;
            var a = _mesh.bounds; var b = other._mesh.bounds;
            if (a.min.x < b.max.x && b.min.x < a.max.x && a.min.y < b.max.y && b.min.y < a.max.y) {
                _label.transform.position += Vector3.up * (b.max.y - a.min.y + 1f / 40); i = -1; guard++;
            }
        }
    }
    void OnDestroy() => All.Remove(this);
}
}
