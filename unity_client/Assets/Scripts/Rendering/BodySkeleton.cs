// 파츠별로 따로 뽑은 스프라이트(머리/몸통/상완/전완/허벅지/종아리+발, Assets/Resources/Sprites/CharacterParts/<main_m|main_f>/)를
// 관절 Transform 계층으로 조립한다. GearAttachment(무기/투구/갑옷 앵커)와 달리 이건 몸 자체가 관절마다 실제로 접힌다.
// 다만 걷기/공격 사이클은 손으로 그린 애니메이션이 아니라 사인파로 흔드는 1차 프로시저럴 구현.
// main_m/main_f 외 캐릭터·몬스터용 파츠 세트는 없어서 지금은 플레이어 전용.
using UnityEngine;

namespace Game.Rendering {

public class BodySkeleton : MonoBehaviour {
    public Transform Neck, ShoulderL, ShoulderR, HipL, HipR;
    Transform _elbowL, _elbowR, _kneeL, _kneeR;
    SpriteRenderer _head, _torso, _upperL, _upperR, _lowerL, _lowerR, _thighL, _thighR, _shinL, _shinR;
    float _t;

    public bool IsReady { get; private set; }

    public void Init(string key) {
        Sprite Load(string p) => Resources.Load<Sprite>($"Sprites/CharacterParts/{key}/{p}");
        var head = Load("head"); var torso = Load("torso");
        var upper = Load("upperarm"); var lower = Load("lowerarm");
        var thigh = Load("thigh"); var shin = Load("shinfoot");
        if (head == null || torso == null || upper == null || lower == null || thigh == null || shin == null) {
            Debug.LogWarning($"[BodySkeleton] {key} 파츠 스프라이트 누락 — 기존 프레임시트 렌더러를 그대로 쓴다");
            return;
        }

        _torso = MakePart(transform, torso, 5, Vector2.zero);

        Neck = MakeJoint(transform, "Neck", new Vector2(0, HalfH(torso)));
        _head = MakePart(Neck, head, 6, new Vector2(0, HalfH(head)));

        ShoulderL = MakeJoint(transform, "ShoulderL", new Vector2(-HalfW(torso) * 0.7f, HalfH(torso) * 0.7f));
        ShoulderR = MakeJoint(transform, "ShoulderR", new Vector2(HalfW(torso) * 0.7f, HalfH(torso) * 0.7f));
        _upperL = MakePart(ShoulderL, upper, 4, new Vector2(0, -HalfH(upper)));
        _upperR = MakePart(ShoulderR, upper, 4, new Vector2(0, -HalfH(upper)), mirror: true);
        _elbowL = MakeJoint(_upperL.transform, "ElbowL", new Vector2(0, -HalfH(upper)));
        _elbowR = MakeJoint(_upperR.transform, "ElbowR", new Vector2(0, -HalfH(upper)));
        _lowerL = MakePart(_elbowL, lower, 4, new Vector2(0, -HalfH(lower)));
        _lowerR = MakePart(_elbowR, lower, 4, new Vector2(0, -HalfH(lower)), mirror: true);

        HipL = MakeJoint(transform, "HipL", new Vector2(-HalfW(torso) * 0.4f, -HalfH(torso)));
        HipR = MakeJoint(transform, "HipR", new Vector2(HalfW(torso) * 0.4f, -HalfH(torso)));
        _thighL = MakePart(HipL, thigh, 3, new Vector2(0, -HalfH(thigh)));
        _thighR = MakePart(HipR, thigh, 3, new Vector2(0, -HalfH(thigh)), mirror: true);
        _kneeL = MakeJoint(_thighL.transform, "KneeL", new Vector2(0, -HalfH(thigh)));
        _kneeR = MakeJoint(_thighR.transform, "KneeR", new Vector2(0, -HalfH(thigh)));
        _shinL = MakePart(_kneeL, shin, 3, new Vector2(0, -HalfH(shin)));
        _shinR = MakePart(_kneeR, shin, 3, new Vector2(0, -HalfH(shin)), mirror: true);

        IsReady = true;
    }

    float HalfH(Sprite s) => s.rect.height / s.pixelsPerUnit / 2f;
    float HalfW(Sprite s) => s.rect.width / s.pixelsPerUnit / 2f;

    Transform MakeJoint(Transform parent, string name, Vector2 localPos) {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        return go.transform;
    }

    SpriteRenderer MakePart(Transform parent, Sprite s, int order, Vector2 localPos, bool mirror = false) {
        var go = new GameObject(s.name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        if (mirror) go.transform.localScale = new Vector3(-1, 1, 1);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s; sr.sortingOrder = order;
        return sr;
    }

    // 진짜 IK 없이 사인파로 팔다리를 흔드는 1차 구현. 손으로 그린 워크사이클로 바꾸고 싶으면
    // 이 함수만 교체하면 된다(관절 트랜스폼 구조는 그대로 재사용 가능).
    public void Animate(bool walking, float speed01) {
        if (!IsReady) return;
        _t += Time.deltaTime * (walking ? 6f * Mathf.Max(0.3f, speed01) : 1.5f);
        float swing = walking ? Mathf.Sin(_t) * 28f : Mathf.Sin(_t) * 3f;
        ShoulderL.localRotation = Quaternion.Euler(0, 0, swing);
        ShoulderR.localRotation = Quaternion.Euler(0, 0, -swing);
        HipL.localRotation = Quaternion.Euler(0, 0, -swing);
        HipR.localRotation = Quaternion.Euler(0, 0, swing);
        float kneeBend = walking ? Mathf.Max(0, Mathf.Sin(_t + Mathf.PI * 0.5f)) * 20f : 0f;
        _kneeL.localRotation = Quaternion.Euler(0, 0, -kneeBend);
        _kneeR.localRotation = Quaternion.Euler(0, 0, -kneeBend);
    }
}
}
