using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Game.Data;
using Game.Network;

namespace Game.Gameplay {
// Presentation only: inventory/currency changes remain exclusively in server ACKs.
public sealed class WorldDropFeedback : MonoBehaviour {
    sealed class Drop { public SpriteRenderer image; public Sprite[] frames; public Vector3 origin; public Transform target; public float age; public bool held; public int uid; }
    readonly List<Drop> _drops = new List<Drop>();
    readonly Dictionary<string, Sprite[]> _frames = new Dictionary<string, Sprite[]>();
    public int VisibleCount => _drops.Count;
    public int PendingUid => _drops.FirstOrDefault(d => d.held)?.uid ?? 0;

    public void Show(InvDrop reward, InvState state, Vector3 position, Transform target) {
        if (reward == null) return;
        if (reward.gold > 0 || reward.sold > 0) Add("gold", position + Vector3.left * .4f, target);
        if (reward.stones > 0 || reward.disassembled > 0) Add(reward.tier >= 13 ? "stone_unique" : reward.tier >= 7 ? "stone_rare" : "stone_basic", position + Vector3.right * .4f, target);
        if (reward.item != null && reward.item.uid > 0 && reward.sold == 0 && reward.disassembled == 0) {
            bool pending = state != null && state.HasPendingDrop && state.pendingDrop.uid == reward.item.uid;
            bool collected = state?.inv?.Any(i => i.uid == reward.item.uid) == true;
            if (pending || collected) Add(reward.item.slot, position, target, pending, reward.item.uid);
        }
    }

    public void SyncPending(InvState state, Vector3 position, Transform target) {
        if (state == null) return;
        foreach (var drop in _drops.Where(d => d.held).ToArray()) {
            if (state.HasPendingDrop && state.pendingDrop.uid == drop.uid) continue;
            if (state.inv?.Any(i => i.uid == drop.uid) == true) {
                drop.held = false; drop.age = 0; drop.origin = drop.image.transform.position;
            } else { Remove(drop); }
        }
        if (state.HasPendingDrop && PendingUid != state.pendingDrop.uid)
            Add(state.pendingDrop.slot, position, target, true, state.pendingDrop.uid);
    }

    void Add(string kind, Vector3 position, Transform target, bool held = false, int uid = 0) {
        if (held && _drops.Any(d => d.held && d.uid == uid)) return;
        if (!_frames.TryGetValue(kind, out var frames)) {
            frames = Resources.LoadAll<Texture2D>("Sprites/UI/drop_" + kind + "_4f").OrderBy(t => t.name)
                .Select(t => Sprite.Create(t, new Rect(0,0,t.width,t.height), new Vector2(.5f,0),40)).ToArray();
            _frames[kind] = frames;
        }
        if (frames.Length == 0) return;
        var image = new GameObject("WorldDrop_" + kind).AddComponent<SpriteRenderer>();
        image.transform.SetParent(transform,false); position.y = WorldConfig.GroundY + .05f;
        image.transform.position = position; image.sortingOrder = 12; image.sprite = frames[0];
        _drops.Add(new Drop { image=image,frames=frames,origin=position,target=target,held=held,uid=uid });
    }

    void Update() => Tick(Time.deltaTime);
    public void Tick(float dt) {
        foreach (var drop in _drops.ToArray()) {
            drop.age += dt;
            drop.image.sprite = drop.frames[(int)(drop.age*10)%drop.frames.Length];
            if (drop.held) continue;
            if (drop.age < .45f) {
                drop.image.transform.position = drop.origin + Vector3.up * Mathf.Sin(drop.age/.45f*Mathf.PI)*.45f;
            } else {
                float progress = Mathf.Clamp01((drop.age-.45f)/.4f);
                var destination = drop.target != null ? drop.target.position + Vector3.up : drop.origin;
                drop.image.transform.position = Vector3.Lerp(drop.origin,destination,progress);
                drop.image.color = new Color(1,1,1,1-progress);
                if (progress >= 1) Remove(drop);
            }
        }
    }
    void Remove(Drop drop) { _drops.Remove(drop); if (Application.isPlaying) Destroy(drop.image.gameObject); else DestroyImmediate(drop.image.gameObject); }
    public void Clear() { foreach (var drop in _drops.ToArray()) Remove(drop); }
    void OnDestroy() { foreach (var frames in _frames.Values) foreach (var sprite in frames) { if (Application.isPlaying) Destroy(sprite); else DestroyImmediate(sprite); } }
}
}
