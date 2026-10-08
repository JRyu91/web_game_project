// Stage 1 제출 스크린샷(spec 1-12)을 배치모드 에디터에서 렌더: Camera → RenderTexture(1280x720) → PNG.
// 플레이 모드 없이 ZoneController 를 직접 호출(AnimTime 고정)해서 결정적 결과를 낸다.
// 실행: -batchmode -executeMethod Game.EditorTools.Stage1Capture.Run
// 출력: ../unity_review/stage1/*.png, 프레임 시퀀스는 ../unity_review/stage1/_frames/<name>/ (GIF 는 ffmpeg 로 후처리)
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Game.Data;
using Game.Rendering;

namespace Game.EditorTools {

public static class Stage1Capture {
    const string OUT = "../unity_review/stage1";
    static Camera _cam;
    static ZoneController _zc;
    static RenderTexture _rt;
    static Texture2D _tex;
    static readonly List<GameObject> _actors = new List<GameObject>();

    static readonly Dictionary<Zone, int[]> Lineup = new Dictionary<Zone, int[]> {
        { Zone.A, new[] { 1, 2, 3, 4, 5, 6 } },       // 사용자 결정: A = t1~t6
        { Zone.B, new[] { 7, 8, 9, 10, 11, 12 } },
        { Zone.C, new[] { 13, 14, 15, 16, 17, 18 } },           // C: t19-t21 스폰 제외(추후 지시)
    };

    public static void RunAdaptive() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _cam = new GameObject("AdaptiveCaptureCamera").AddComponent<Camera>();
        _cam.orthographic = true; _cam.backgroundColor = Color.black;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        var pixel = _cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();
        pixel.assetsPPU = 40; pixel.refResolutionX = 1280; pixel.refResolutionY = 720;
        _zc = new GameObject("AdaptiveCaptureZone").AddComponent<ZoneController>();
        _zc.Cam = _cam; ZoneController.AnimTime = 0;
        foreach (var size in new[] { new Vector2Int(1280, 1240), new Vector2Int(852, 329) }) {
            _rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _tex = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            _cam.targetTexture = _rt; _cam.aspect = (float)size.x / size.y;
            foreach (var zone in new[] { Zone.A, Zone.B, Zone.C }) {
                foreach (var tod in new[] { Tod.Day, Tod.Night }) {
                    ClearActors(); _zc.SetZone(zone, tod); Cam(36); LineupActors(zone, "idle", 0);
                    foreach (var actor in ActorVisual.All.ToArray()) if (actor != null) actor.Refresh();
                    _cam.Render(); _cam.Render();
                    RenderTexture.active = _rt;
                    _tex.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); _tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes($"Logs/adaptive-{zone}-{tod}-{size.x}x{size.y}.png", _tex.EncodeToPNG());
                }
            }
            _cam.targetTexture = null; _rt.Release(); Object.DestroyImmediate(_rt); Object.DestroyImmediate(_tex);
        }
        ClearActors(); ZoneController.AnimTime = -1;
        Debug.Log("[Stage1Capture] adaptive screenshots complete: A/B/C, day/night, tall/landscape; visual review required");
    }

    public static void Run() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Directory.CreateDirectory(OUT);
        _cam = new GameObject("Cam").AddComponent<Camera>();
        _cam.orthographic = true; _cam.orthographicSize = 9f;
        _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = Color.black;
        _rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        _cam.targetTexture = _rt;
        _tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
        _zc = new GameObject("Zone").AddComponent<ZoneController>();
        _zc.Cam = _cam;
        ZoneController.AnimTime = 0;
        _zc.SetZone(Zone.A, Tod.Day); Cam(36); _cam.Render(); _cam.Render(); // 워밍업: 첫 렌더는 텍스처 업로드 전이라 하늘만 나옴

        var shots = new (Zone z, Tod t, string n)[] {
            (Zone.A, Tod.Day, "A_day"), (Zone.A, Tod.Dawn, "A_dawn"), (Zone.A, Tod.Night, "A_night"),
            (Zone.B, Tod.Day, "B_day"), (Zone.B, Tod.Dawn, "B_dawn"), (Zone.B, Tod.Night, "B_night"),
            (Zone.C, Tod.Day, "C_cave"),
        };
        foreach (var (z, t, n) in shots) {
            _zc.SetZone(z, t);
            Cam(36); Shot($"{n}_x800");
            SceneActors(z); Shot($"{n}_x800_actors"); ClearActors();
        }

        foreach (var z in new[] { Zone.A, Zone.B, Zone.C }) {
            _zc.SetZone(z, Tod.Day);
            Cam(80); Shot($"{z}_wrap");                                  // 카메라 좌측 = 2560px
            Cam(WrapCam(0.15f)); Shot($"{z}_wrap_slow");                 // far(p .15) 이음새가 화면 중앙
            Cam(WrapCam(0.05f)); Shot($"{z}_wrap_slow_clouds");          // clouds/back_wall(p .05)

            // 라인업: 플레이어 + 존 몬스터 7종, 발라인 일렬 (Stage 2 확인)
            Cam(36); LineupActors(z, "idle", 0); Shot($"monsters_lineup_{z}");
            // 발 정렬 측정용 마스크(배경 숨김, 마젠타 바탕, 외곽선 끔) — walk/attack/hurt 전 프레임
            _zc.gameObject.SetActive(false); _cam.backgroundColor = Color.magenta;
            Shader.SetGlobalColor("_ActorOutline", Color.clear);
            int f = 0;
            foreach (var clip in new[] { "walk", "attack", "hurt" })
                for (int i = 0; i < 8; i++) { LineupActors(z, clip, i); Shot($"_frames/feet_{z}/{f++:D3}"); }
            _zc.gameObject.SetActive(true); _cam.backgroundColor = Color.black;
            ClearActors();
        }

        // 스크롤 GIF 프레임: 맵 0 → 3200+640px, 8초, 12fps
        foreach (var (z, t, n) in new[] { (Zone.A, Tod.Day, "A_day_scroll"), (Zone.B, Tod.Night, "B_night_scroll"), (Zone.C, Tod.Day, "C_cave_scroll") }) {
            _zc.SetZone(z, t);
            for (int i = 0; i < 96; i++) {
                ZoneController.AnimTime = i / 12f;
                Cam((i * 3840f / 95f + 640f) / 40f); Shot($"_frames/{n}/{i:D3}");
            }
        }
        // 용암폭포 16프레임(10fps 2루프)
        _zc.SetZone(Zone.C, Tod.Day);
        for (int i = 0; i < 16; i++) { ZoneController.AnimTime = i / 10f; Cam(36); Shot($"_frames/C_lavafall/{i:D3}"); }
        ZoneController.AnimTime = -1;
        MeasureParallax();
        Debug.Log("[Stage1Capture] done");
    }

    // 레이어별 화면 이동 px / 카메라 이동 px (카메라 400px 이동, 실제 배치된 오브젝트 좌표로 측정)
    static void MeasureParallax() {
        var sb = new System.Text.StringBuilder("zone\tlayer\tcam_px\tscreen_px\tratio\n");
        foreach (var z in new[] { Zone.A, Zone.B, Zone.C }) {
            _zc.SetZone(z, z == Zone.C ? Tod.Day : Tod.Night); // 밤: 별/달 포함
            var before = new Dictionary<string, float>();
            Cam(30); foreach (Transform t in _zc.transform) before[t.name] = t.position.x - _zc.CamX;
            Cam(40);
            foreach (Transform t in _zc.transform) {
                if (!before.TryGetValue(t.name, out var b0) || t.name.EndsWith("_1")) continue;
                float screenPx = -((t.position.x - _zc.CamX) - b0) * 40f;
                sb.Append($"{z}\t{t.name.Replace("_0", "")}\t400\t{screenPx:0}\t{screenPx / 400f:0.00}\n");
            }
        }
        File.WriteAllText($"{OUT}/parallax.tsv", sb.ToString());
    }

    // p 레이어의 랩 경계(레이어 맵 x=0)가 화면 중앙에 오는 카메라 x: camX = off + 80, off = (camX-36)(1-p)
    static float WrapCam(float p) => (WorldConfig.MapWidth - 36f * (1 - p)) / p;

    static void Cam(float x) { _zc.CameraXOverride = x; _zc.Layout(null); }

    static void Shot(string name) {
        ActorVisual.All.RemoveAll(a => a == null);
        foreach (var a in ActorVisual.All) a.Refresh();
        _cam.Render();
        RenderTexture.active = _rt;
        _tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); _tex.Apply();
        RenderTexture.active = null;
        string path = $"{OUT}/{name}.png";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, _tex.EncodeToPNG());
    }

    static GameObject Actor(string root, string clip, int frame, float mapPx, int scale, bool monster, bool weapon = false) {
        var frames = Resources.LoadAll<Sprite>($"{root}/{clip}").OrderBy(s => s.name, System.StringComparer.Ordinal).ToArray();
        if (frames.Length == 0) frames = Resources.LoadAll<Sprite>($"{root}/walk").OrderBy(s => s.name, System.StringComparer.Ordinal).ToArray();
        var go = new GameObject(root);
        go.transform.position = new Vector3(mapPx / 40f, 0, 0);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = frames[frame % frames.Length];
        sr.sortingOrder = monster ? 5 : 6;
        if (weapon) { // 게임과 같은 경로: GearAttachment 손 앵커 + 같은 배율로 구운 무기
            var g = go.AddComponent<GearAttachment>(); g.Init(sr);
            g.SetWeapon("L000_낡은단검");
        }
        go.AddComponent<ActorVisual>().Init(sr, scale, monster);
        _actors.Add(go);
        return go;
    }

    // 미리보기와 비슷한 배치: 플레이어 화면 x≈300, 몬스터 몇 마리 오른쪽(왼쪽을 보게)
    static void SceneActors(Zone z) {
        Actor($"{ActorScale.PlayerRoot}/main_m", "idle", 0, 980, ActorScale.Player, false); // 미리보기처럼 빈 공간(화면 x≈180), 무기 숨김
        var tiers = Lineup[z];
        float[] xs = { 1480, 1700, 1930 };
        for (int i = 0; i < xs.Length; i++) {
            int tier = tiers[(i * 3) % tiers.Length];
            var go = Actor(ActorScale.MonsterRoot(tier), "walk", 0, xs[i], ActorScale.Tier(tier), true);
            go.GetComponent<SpriteRenderer>().flipX = true;
        }
    }

    static void LineupActors(Zone z, string clip, int frame) {
        ClearActors();
        float x = 800 + 100;
        var p = Actor($"{ActorScale.PlayerRoot}/main_m", clip == "idle" ? "idle" : clip, frame, x, ActorScale.Player, false);
        x += 140;
        foreach (int tier in Lineup[z]) {
            var probe = Actor(ActorScale.MonsterRoot(tier), clip == "idle" ? "walk" : clip, frame, 0, ActorScale.Tier(tier), true);
            float hw = probe.GetComponent<ActorVisual>().HalfWidth * 40;
            x += hw + 12;
            probe.transform.position = new Vector3(Mathf.Round(x) / 40f, 0, 0);
            x += hw + 12;
        }
    }

    static void ClearActors() {
        foreach (var a in _actors) Object.DestroyImmediate(a);
        _actors.Clear();
    }
}
}
