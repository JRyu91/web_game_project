using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.EditorTools {
public static class WebBuild {
    public static void Build() {
        try {
            PlayerSettings.productName = "시간 낭비의 숲";
            PlayerSettings.bundleVersion = "3.0.13";
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.External;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.nameFilesAsHashes = true;
            PlayerSettings.WebGL.dataCaching = false;
            PlayerSettings.WebGL.initialMemorySize = 128;
            PlayerSettings.WebGL.maximumMemorySize = 2048;
            var output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../field/web"));
            if (Directory.Exists(output + "/Build")) Directory.Delete(output + "/Build", true); //해시 파일명이라 지난 빌드가 남으면 아래 GetFiles[0] 가 옛 파일을 집는다
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/Scenes/Main.unity" }, locationPathName = output,
                target = BuildTarget.WebGL, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Web build failed");
            string FileName(string pattern) => "/web/Build/" + Path.GetFileName(Directory.GetFiles(output + "/Build", pattern)[0]);
            File.WriteAllText(output + "/build.json", JsonUtility.ToJson(new Manifest {
                loaderUrl = FileName("*.loader.js"), dataUrl = FileName("*.data.gz"),
                frameworkUrl = FileName("*.framework.js.gz"), codeUrl = FileName("*.wasm.gz"), symbolsUrl = FileName("*.symbols.json.gz")
            }));
            Debug.Log("[WebBuild] PASS: v3.0.13 " + report.summary.totalSize + " bytes");
            EditorApplication.Exit(0);
        } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
    [Serializable] class Manifest { public string loaderUrl, dataUrl, frameworkUrl, codeUrl, symbolsUrl; }
}
}
