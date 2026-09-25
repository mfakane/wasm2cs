using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

public static class SelfHostBuild
{
    public static void Build()
    {
        GuestAssemblyCheck.Verify();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/SelfHost.unity");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
        // ponytail: Low, not Disabled. link.xml keeps indirect-table methods that stripping cannot see.
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Low);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/SelfHost.unity" },
            locationPathName = "Build/SelfHost.exe",
            target = BuildTarget.StandaloneWindows64
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("IL2CPP build failed.");
    }
}
