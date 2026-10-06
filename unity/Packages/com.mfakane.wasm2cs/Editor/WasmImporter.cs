using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Wasm2Cs.Editor
{
    /// <summary>
    /// Imports <c>.wasm</c> assets and exposes an optional ClassName override for the generated C# type.
    /// When ClassName is empty, <see cref="WasmAssetBridge"/> uses the filename without extension.
    /// Unity Editor and IL2CPP execution of this override path are unverified in the Linux CI environment.
    /// </summary>
    [ScriptedImporter(1, "wasm")]
    public sealed class WasmImporter : ScriptedImporter
    {
        public string ClassName = string.Empty;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            // Metadata-only import: the bridge reads module bytes from disk via File.ReadAllBytes.
            var asset = ScriptableObject.CreateInstance<WasmModuleAsset>();
            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
        }
    }

    public sealed class WasmModuleAsset : ScriptableObject
    {
    }
}
