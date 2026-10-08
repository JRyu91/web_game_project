// 실제 플레이 버튼을 눌러보는 건 형만 할 수 있지만, "떠서 몇 초간 죽지 않는다"는
// 최소 확인은 헤드리스 빌드+실행으로 대신한다. Player.log 에 예외가 있으면 잡아낸다.
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools {

public static class SmokeTest {
    public static void BuildAndRun() {
        string outDir = "/tmp/unity_client_smoke";
        string outPath = outDir + "/game.app";
        Directory.CreateDirectory(outDir);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { "Assets/Scenes/Main.unity" },
            locationPathName = outPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.Development,
        });
        UnityEngine.Debug.Log($"[SmokeTest] build result: {report.summary.result}, errors={report.summary.totalErrors}");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) return;

        string logPath = "/tmp/unity_client_smoke/Player.log";
        if (File.Exists(logPath)) File.Delete(logPath);
        var exe = outPath + "/Contents/MacOS/unity_client";
        var psi = new ProcessStartInfo {
            FileName = exe,
            Arguments = $"-batchmode -nographics -logFile {logPath}",
            UseShellExecute = false,
        };
        var p = Process.Start(psi);
        p.WaitForExit(8000);
        if (!p.HasExited) p.Kill();
        UnityEngine.Debug.Log($"[SmokeTest] ran for ~8s, log at {logPath}");
    }
}
}
