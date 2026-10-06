using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Wasm2Cs.Editor
{
    [InitializeOnLoad]
    public static class WasmAssetBridge
    {
        public const string OutputDirectory = "Assets/Wasm2CsGeneratedInputs";
        const string Suffix = ".Wasm2Cs.Generator.additionalfile";
        static bool scheduled;
        static WasmAssetBridge() { Schedule(); }

        public static void Schedule()
        {
            if (scheduled) return;
            scheduled = true;
            EditorApplication.delayCall += () => { scheduled = false; Synchronize(); };
        }

        [MenuItem("Tools/Wasm2Cs/Regenerate Inputs")]
        public static void Synchronize()
        {
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var paths = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)
                && p.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var importer = AssetImporter.GetAtPath(path) as WasmImporter;
                var name = importer != null && !string.IsNullOrWhiteSpace(importer.ClassName)
                    ? importer.ClassName.Trim()
                    : Path.GetFileNameWithoutExtension(path);
                string content;
                if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    name = "Invalid_" + AssetDatabase.AssetPathToGUID(path);
                    content = "!WASM001:The generated class name must be an ASCII C# identifier: " + path;
                }
                else
                {
                    try { content = Convert.ToBase64String(File.ReadAllBytes(path)); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    { content = "!WASM001:" + path + ": " + e.Message; }
                }
                string output = OutputDirectory + "/" + name + Suffix;
                if (expected.ContainsKey(output)) content = "!WASM002:Duplicate module name: " + name;
                expected[output] = content;
            }
            if (expected.Count > 0) Directory.CreateDirectory(OutputDirectory);
            foreach (var input in expected)
            {
                if (File.Exists(input.Key) && File.ReadAllText(input.Key) == input.Value) continue;
                File.WriteAllText(input.Key, input.Value);
                AssetDatabase.ImportAsset(input.Key);
            }
            if (!Directory.Exists(OutputDirectory)) return;
            foreach (var stale in Directory.GetFiles(OutputDirectory, "*" + Suffix))
                if (!expected.ContainsKey(stale.Replace('\\', '/'))) AssetDatabase.DeleteAsset(stale);
        }
    }

    public sealed class WasmAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom)
                .Any(p => p.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))) WasmAssetBridge.Schedule();
        }
    }

    public sealed class WasmBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder { get { return -1000; } }
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!Directory.Exists(WasmAssetBridge.OutputDirectory)) return;
            foreach (var path in Directory.GetFiles(WasmAssetBridge.OutputDirectory, "*.additionalfile"))
                if (File.ReadAllText(path).StartsWith("!WASM", StringComparison.Ordinal))
                    throw new BuildFailedException(File.ReadAllText(path));
        }
    }
}
