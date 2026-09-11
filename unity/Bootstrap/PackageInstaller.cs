using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public static class PackageInstaller
{
    static AddRequest request;
    static double deadline;
    public static void Install()
    {
        request = Client.Add("file:" + System.Environment.GetEnvironmentVariable("WASM2CS_UNITY_PACKAGE"));
        deadline = EditorApplication.timeSinceStartup + 600;
        EditorApplication.update += Poll;
    }
    static void Poll()
    {
        if (!request.IsCompleted && EditorApplication.timeSinceStartup < deadline) return;
        EditorApplication.update -= Poll;
        if (request.IsCompleted && request.Status == StatusCode.Success)
        { Debug.Log("WASM2CS_PACKAGE_INSTALLED"); EditorApplication.Exit(0); }
        else { Debug.LogError(request.Error == null ? "UPM timed out" : request.Error.message); EditorApplication.Exit(1); }
    }
}
