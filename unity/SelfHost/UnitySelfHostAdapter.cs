using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// File, log, and exit differences only. Mono startup stays in the generated runner.
public static class UnitySelfHostAdapter
{
    static Dictionary<string, string> hashes;

    public static string Scenario()
    {
        var value = Argument("-selfHostScenario") ?? Environment.GetEnvironmentVariable("SH10_SCENARIO");
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("SH10_SCENARIO is not set.");
        return value;
    }

    public static byte[] LoadAssembly(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            throw new ArgumentException("Assembly name is not a data-asset key.", nameof(name));
        var asset = Resources.Load<TextAsset>("Guest/" + name);
        if (asset == null) throw new InvalidOperationException("Guest data asset is missing: " + name);
        var bytes = asset.bytes;
        if (bytes == null || bytes.Length == 0) throw new InvalidOperationException("Guest data asset is empty: " + name);
        var actual = Sha256(bytes);
        if (!string.Equals(actual, ExpectedHash(name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Guest data asset hash differs for " + name + ".");
        return (byte[])bytes.Clone();
    }

    public static void WriteTranslation(string id, string output)
    {
        if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Translation id is not a file name.", nameof(id));
        var directory = Path.Combine(OutputDirectory(), "translations");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".txt"), output ?? "", new UTF8Encoding(false));
        Log("WASM2CS_TRANSLATION " + id + " bytes=" + (output == null ? 0 : Encoding.UTF8.GetByteCount(output)));
    }

    public static void WriteOutcome(string scenario, int? managedReturn, int? exitCode, string state,
        string failureType, string failureMessage, double startupDurationMs, double executionDurationMs,
        int linearPages, long outerGcHeap)
    {
        var lines = new[]
        {
            "scenario=" + (scenario ?? ""),
            "state=" + (state ?? ""),
            "managedReturn=" + (managedReturn.HasValue ? managedReturn.Value.ToString() : ""),
            "exitCode=" + (exitCode.HasValue ? exitCode.Value.ToString() : ""),
            "failureType=" + (failureType ?? ""),
            "failureMessage=" + (failureMessage ?? "").Replace("\r", " ").Replace("\n", " "),
            "startupDurationMs=" + startupDurationMs.ToString("R"),
            "executionDurationMs=" + executionDurationMs.ToString("R"),
            "linearPages=" + linearPages,
            "outerGcHeap=" + outerGcHeap,
            "unityVersion=" + Application.unityVersion,
            "platform=" + Application.platform,
            "isEditor=" + Application.isEditor
        };
        File.WriteAllText(Path.Combine(OutputDirectory(), "summary.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        Log("WASM2CS_SELFHOST_RESULT " + scenario + " " + (state ?? ""));
    }

    public static void Exit(int code)
    {
        Log("WASM2CS_SELFHOST_EXIT " + code);
        if (Application.isEditor)
        {
            if (code != 0) throw new InvalidOperationException("Self-host exit " + code + ".");
            return;
        }
        Application.Quit(code);
    }

    public static void Log(string message)
    {
        Debug.Log(message);
        try
        {
            File.AppendAllText(Path.Combine(OutputDirectory(), "adapter.log"), message + "\n", new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
    }

    static string OutputDirectory()
    {
        var value = Argument("-selfHostOut") ?? Environment.GetEnvironmentVariable("WASM2CS_SELFHOST_OUT");
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("WASM2CS_SELFHOST_OUT is not set.");
        Directory.CreateDirectory(value);
        return value;
    }

    static string ExpectedHash(string name)
    {
        if (hashes == null)
        {
            var manifest = Resources.Load<TextAsset>("Guest/manifest");
            if (manifest == null) throw new InvalidOperationException("Guest hash manifest is missing.");
            hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in manifest.text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var split = line.IndexOf(' ');
                if (split <= 0) throw new InvalidOperationException("Guest hash manifest is invalid.");
                hashes[line.Substring(0, split)] = line.Substring(split + 1).Trim();
            }
        }
        string hash;
        if (!hashes.TryGetValue(name, out hash))
            throw new InvalidOperationException("Guest hash manifest has no entry for " + name + ".");
        return hash;
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

    static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
