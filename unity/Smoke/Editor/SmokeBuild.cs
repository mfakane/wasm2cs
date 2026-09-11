using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

public static class SmokeBuild
{
    public static void Verify() { SmokeRunner.Verify(); }
    public static void Build()
    {
        SmokeRunner.Verify();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/Smoke.unity");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Smoke.unity" },
            locationPathName = "Build/Smoke.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("IL2CPP build failed.");
    }
}
