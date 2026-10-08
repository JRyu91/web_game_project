// SceneBuilder 가 편집시점에 Resources.GetBuiltinResource 로 흰 스프라이트를 굽으려 했으나
// 이 프로젝트에서는 null을 조용히 리턴해서 Ground 가 fileID:0(빈 참조)로 저장되는 버그가 있었다.
// 대신 런타임에 1x1 텍스처를 스스로 생성해 붙인다 — 에디터/빌드 어디서든 항상 동작한다.
using UnityEngine;

namespace Game.Rendering {

[RequireComponent(typeof(SpriteRenderer))]
public class SolidColorSprite : MonoBehaviour {
    static Sprite _shared;

    void Awake() {
        if (_shared == null) {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _shared = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        }
        GetComponent<SpriteRenderer>().sprite = _shared;
    }
}
}
