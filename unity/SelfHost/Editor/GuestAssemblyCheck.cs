using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class GuestAssemblyCheck
{
    public static void Verify()
    {
        var guest = "Assets/Resources/Guest";
        if (!Directory.Exists(guest)) throw new Exception("Guest data directory is missing.");
        var manifestPath = guest + "/manifest.txt";
        if (!File.Exists(manifestPath)) throw new Exception("Guest hash manifest is missing.");
        var expected = File.ReadAllLines(manifestPath)
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                var split = line.IndexOf(' ');
                if (split <= 0) throw new Exception("Guest hash manifest is invalid.");
                return new { Name = line.Substring(0, split), Hash = line.Substring(split + 1).Trim() };
            })
            .ToArray();
        if (expected.Length == 0) throw new Exception("Guest hash manifest is empty.");
        foreach (var entry in expected)
        {
            var path = guest + "/" + entry.Name + ".bytes";
            if (!File.Exists(path)) throw new Exception("Guest data asset is missing: " + entry.Name);
            if (File.Exists(guest + "/" + entry.Name)) throw new Exception("Guest DLL was also copied as a plugin name: " + entry.Name);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new Exception(path + " has no importer.");
            if (importer is PluginImporter) throw new Exception(path + " was imported as a plugin DLL.");
            if (entry == expected[0]) Debug.Log("WASM2CS_GUEST_IMPORTER " + importer.GetType().FullName);
            var bytes = File.ReadAllBytes(path);
            if (!string.Equals(Sha256(bytes), entry.Hash, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Guest data asset hash differs for " + entry.Name);
        }
        foreach (var dll in Directory.GetFiles(guest, "*.dll", SearchOption.AllDirectories))
            throw new Exception("Guest directory contains a plugin DLL: " + dll);

        var host = AssetImporter.GetAtPath("Assets/Plugins/Wasm2Cs.DotnetHost.dll");
        if (!(host is PluginImporter)) throw new Exception("Host library was not imported as a managed plugin.");
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        if (Directory.GetFiles(projectRoot, "Wasm2Cs.Generator.dll", SearchOption.AllDirectories).Length == 0)
            throw new Exception("Generator DLL was not introduced.");

        var owners = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetType("Wasm2Cs.Generated.DotnetRuntime") != null)
            .Select(assembly => assembly.GetName().Name)
            .ToArray();
        if (owners.Length != 1 || owners[0] != "Wasm2Cs.SelfHost.Runtime")
            throw new Exception("DotnetRuntime leaked or is missing: " + string.Join(", ", owners));
        var bindings = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => assembly.GetName().Name == "Wasm2Cs.SelfHost.Runtime")
            .GetType("Wasm2Cs.Generated.DotnetRuntime+Bindings");
        if (bindings == null) throw new Exception("Generated bindings type is missing.");
        var fields = bindings.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length == 0) throw new Exception("Generated bindings have no fields.");
        foreach (var field in fields)
        {
            if (field.FieldType == typeof(Delegate) || !typeof(Delegate).IsAssignableFrom(field.FieldType))
                throw new Exception("Binding " + field.Name + " is not a typed delegate.");
        }
        var runner = File.ReadAllText("Assets/SelfHost/UnitySelfHostRunner.cs");
        foreach (var forbidden in new[] { "Transpiler", "DynamicInvoke", "WebAssembly", "JsonSerializer", "File.ReadAllBytes" })
            if (runner.Contains(forbidden)) throw new Exception("Unity runner references " + forbidden + ".");
        if (!File.Exists("Assets/link.xml") || !File.ReadAllText("Assets/link.xml").Contains("Wasm2Cs.SelfHost.Runtime"))
            throw new Exception("Generated runtime preserve rule is missing.");
        Debug.Log("WASM2CS_GUEST_DATA_PASS");
        Debug.Log("WASM2CS_ISOLATION_PASS");
    }

    static string Sha256(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(bytes);
            var text = new StringBuilder(hash.Length * 2);
            foreach (var value in hash) text.Append(value.ToString("x2"));
            return text.ToString();
        }
    }
}
