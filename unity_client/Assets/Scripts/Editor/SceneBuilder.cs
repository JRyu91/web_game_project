// GUI 클릭 없이 -batchmode -executeMethod 로 메인 씬을 통째로 코드로 구성한다.
// (이 세션엔 화면이 없어서 에디터 GUI 조작이 불가능 — 씬 구성도 전부 코드로 완결되게 하려는 목적.)
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Gameplay;

namespace Game.EditorTools {

public static class SceneBuilder {
    [MenuItem("Tools/Build Main Scene")]
    public static void Build() {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 9f; // 720 / 2 / PPU 40 → 1280x720 에서 1:1 픽셀 (spec §0)
        cam.backgroundColor = Color.black;
        cam.clearFlags = CameraClearFlags.SolidColor;
        camGo.tag = "MainCamera";
        camGo.transform.position = new Vector3(36, 0, -10);
        camGo.AddComponent<AudioListener>();
        var ppc = camGo.AddComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();
        ppc.assetsPPU = 40; ppc.refResolutionX = 1280; ppc.refResolutionY = 720;
        ppc.gridSnapping = UnityEngine.Rendering.Universal.PixelPerfectCamera.GridSnapping.PixelSnapping;

        // 존 배경/지면/발라인 = ZoneController (manifest 레이어, 패럴랙스, 시간대). 기본 A/낮.
        var zc = new GameObject("Zone").AddComponent<Game.Rendering.ZoneController>();
        zc.Cam = cam;

        var spawnerGo = new GameObject("MonsterSpawner");
        var spawner = spawnerGo.AddComponent<MonsterSpawner>();
        spawner.MaxAlive = 14; spawner.SpawnInterval = 1.2f;

        var gmGo = new GameObject("GameManager");
        var gm = gmGo.AddComponent<GameManager>();
        gm.Spawner = spawner;
        gm.ServerUrl = "ws://localhost:8080";
        gm.AccountId = "local_dev";
        gm.PlayerName = "모험가";
        gm.Gender = "male";

        System.IO.Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Main.unity");

        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene> {
            new EditorBuildSettingsScene("Assets/Scenes/Main.unity", true)
        };
        EditorBuildSettings.scenes = scenes.ToArray();

        Debug.Log("[SceneBuilder] Main.unity 생성 완료");
    }
}
}
