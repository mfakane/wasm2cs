using System;
using System.IO;
using System.Linq;
using UnityEngine;

// Reflection is confined to this Editor test: it lets deletion tests compile
// while the generated module is intentionally absent.
public static class BridgeVerify
{
    static Type Module => AppDomain.CurrentDomain.GetAssemblies()
        .Single(a => a.GetName().Name == "Wasm2Cs.Modules").GetType("Wasm2Cs.Generated.Arithmetic");
    public static void Present()
    {
        var type = Module;
        if (type == null || (int)type.GetMethod("add").Invoke(Activator.CreateInstance(type), new object[] { 20, 22 }) != 42)
            throw new Exception("Unity failed to generate Arithmetic.");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name != "Wasm2Cs.Modules" && assembly.GetType("Wasm2Cs.Generated.Arithmetic") != null)
                throw new Exception("Generated module leaked into " + assembly.FullName);
        var path = "Assets/Wasm2CsGeneratedInputs/Arithmetic.Wasm2Cs.Generator.additionalfile";
        var timestamp = File.GetLastWriteTimeUtc(path);
        Wasm2Cs.Editor.WasmAssetBridge.Synchronize();
        if (File.GetLastWriteTimeUtc(path) != timestamp) throw new Exception("Unchanged input was rewritten.");
        Debug.Log("WASM2CS_BRIDGE_PRESENT_PASS");
    }
    public static void Changed()
    {
        var type = Module;
        if (type == null || (int)type.GetMethod("negative").Invoke(Activator.CreateInstance(type), null) != -2)
            throw new Exception("Unity retained stale WASM contents.");
        Debug.Log("WASM2CS_BRIDGE_CHANGED_PASS");
    }
    public static void Missing()
    {
        if (Module != null) throw new Exception("Deleted module is still generated.");
        Debug.Log("WASM2CS_BRIDGE_MISSING_PASS");
    }
}
