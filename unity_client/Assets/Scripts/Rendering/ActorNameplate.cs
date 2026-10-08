using UnityEngine;

namespace Game.Rendering {
public class ActorNameplate : MonoBehaviour {
    TextMesh _label;
    SpriteRenderer _body;

    public void Init(string text, Color color) {
        _body = GetComponent<SpriteRenderer>();
        _label = new GameObject("Nameplate").AddComponent<TextMesh>();
        _label.transform.SetParent(transform, false);
        _label.richText = false;
        _label.anchor = TextAnchor.LowerCenter; _label.fontSize = 12; _label.characterSize = 0.3f; _label.color = color;
        var font = Resources.Load<Font>("Fonts/Galmuri11");
        if (font != null) { _label.font = font; _label.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
        _label.GetComponent<MeshRenderer>().sortingOrder = 10;
        SetText(text); LateUpdate();
    }

    public void SetText(string text) { if (_label != null) _label.text = text; }
    void LateUpdate() {
        if (_label == null || _body == null || _body.sprite == null) return;
        var bounds = SpriteBBox.Get(_body.sprite);
        _label.transform.localPosition = new Vector3(_body.flipX ? -bounds.center.x : bounds.center.x, bounds.yMax + 0.2f, 0);
    }
}
}
