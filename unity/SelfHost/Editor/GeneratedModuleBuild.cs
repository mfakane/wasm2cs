using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

public static class GeneratedModuleBuild
{
    public static void Verify() { GeneratedModuleRunner.Verify(); }

    public static void Build()
    {
        GeneratedModuleRunner.Verify();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/Generated.unity");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Low);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Generated.unity" },
            locationPathName = "Build/Generated.exe",
            target = BuildTarget.StandaloneWindows64
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Obtained-source IL2CPP build failed.");
    }
}
