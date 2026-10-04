using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using Wasm2Cs.Generated;

public static class RustStdRunner
{
    static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("Rust std differs from Node: " + message);
    }

    static void Save(Report report)
    {
        string path = Environment.GetEnvironmentVariable("WASM2CS_RUST_STD_OUTPUT");
        if (string.IsNullOrEmpty(path)) throw new Exception("WASM2CS_RUST_STD_OUTPUT is required.");
        File.WriteAllText(path, JsonUtility.ToJson(report, true));
    }

    static Initial ObserveInitial(RustStd190 module)
    {
        return new Initial {
            inputPointer = module.buffer_ptr(), outputPointer = module.output_ptr(), capacity = module.buffer_capacity(),
            dataEnd = module.__data_end, heapBase = module.__heap_base, pages = module.MemorySize / 65536,
            memorySha256 = Hash(module.ReadMemory(0, module.MemorySize))
        };
    }

    public static void Verify()
    {
        var resource = Resources.Load<TextAsset>("RustStdOracle");
        if (resource == null) throw new Exception("Missing RustStdOracle.");
        var oracle = JsonUtility.FromJson<Oracle>(resource.text);
        Expect(oracle.cases != null && oracle.cases.Length == 18, "case count");
        Expect(oracle.rows != null && oracle.rows.Length == oracle.cases.Length, "reference row count");
        var module = new RustStd190();
        var report = new Report {
            nodeVersion = oracle.nodeVersion, fixtureSha256 = oracle.fixtureSha256,
            unityVersion = Application.unityVersion,
#if ENABLE_IL2CPP
            backend = "IL2CPP",
#else
            backend = "Editor Mono",
#endif
            initial = ObserveInitial(module), rows = new Row[0]
        };
        Save(report);
        var initial = report.initial;
        var expected = oracle.initial;
        Expect(initial.inputPointer == expected.inputPointer && initial.outputPointer == expected.outputPointer
            && initial.capacity == expected.capacity && initial.dataEnd == expected.dataEnd
            && initial.heapBase == expected.heapBase && initial.pages == expected.pages
            && initial.memorySha256 == expected.memorySha256, "initial state");
        var rows = new List<Row>();
        for (int i = 0; i < oracle.cases.Length; i++)
        {
            var input = oracle.cases[i];
            var bytes = Convert.FromBase64String(input.input);
            module.WriteMemory(unchecked((uint)module.buffer_ptr()), bytes);
            int status = module.analyze(input.length);
            var row = new Row {
                name = input.name, status = status,
                sum = module.result_sum().ToString(CultureInfo.InvariantCulture), unique = module.result_unique(),
                median = module.result_median().ToString(CultureInfo.InvariantCulture),
                outputBase64 = Convert.ToBase64String(module.ReadMemory(unchecked((uint)module.output_ptr()), module.output_len())),
                inputSha256 = Hash(module.ReadMemory(unchecked((uint)module.buffer_ptr()), bytes.Length)),
                pages = module.MemorySize / 65536, memorySha256 = Hash(module.ReadMemory(0, module.MemorySize))
            };
            rows.Add(row);
            report.rows = rows.ToArray();
            Save(report);
            var wanted = oracle.rows[i];
            Expect(row.name == wanted.name, "call order at " + i);
            Expect(row.status == wanted.status, input.name + " status");
            Expect(row.sum == wanted.sum && row.unique == wanted.unique && row.median == wanted.median, input.name + " summary");
            Expect(row.outputBase64 == wanted.outputBase64, input.name + " output bytes");
            Expect(row.inputSha256 == wanted.inputSha256, input.name + " input bytes");
            Expect(row.pages == wanted.pages, input.name + " memory growth");
            Expect(row.memorySha256 == wanted.memorySha256, input.name + " full memory");
        }
        Expect(module.input_byte(0) == module.ReadMemory(unchecked((uint)module.buffer_ptr()), 1)[0], "in-bounds read");
        try { module.input_byte(module.buffer_capacity()); throw new Exception("Bounds check did not trap."); }
        catch (RustStd190.TrapException error) { report.trap = error.Kind.ToString(); }
        Expect(report.trap == oracle.trap, "bounds trap");
        var fresh = new RustStd190();
        report.freshMemorySha256 = Hash(fresh.ReadMemory(0, fresh.MemorySize));
        Expect(report.freshMemorySha256 == oracle.freshMemorySha256, "fresh instance memory");
        report.passed = true;
        Save(report);
        Debug.Log("WASM2CS_RUST_STD_PASS: 18 calls, full memory, growth, bounds trap, fresh instance; " + report.backend);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Run()
    {
        try { Verify(); if (!Application.isEditor) Application.Quit(0); }
        catch (Exception error) {
            Debug.LogException(error);
            if (!Application.isEditor) Application.Quit(1); else throw;
        }
    }

    [Serializable] public sealed class Input { public string name; public string input; public int length; }
    [Serializable] public sealed class Initial {
        public int inputPointer, outputPointer, capacity, dataEnd, heapBase, pages;
        public string memorySha256;
    }
    [Serializable] public sealed class Row {
        public string name, sum, median, outputBase64, inputSha256, memorySha256;
        public int status, unique, pages;
    }
    [Serializable] public sealed class Oracle {
        public string nodeVersion, fixtureSha256, trap, freshMemorySha256;
        public Input[] cases; public Initial initial; public Row[] rows;
    }
    [Serializable] public sealed class Report {
        public string nodeVersion, fixtureSha256, unityVersion, backend, trap, freshMemorySha256;
        public bool passed; public Initial initial; public Row[] rows;
    }
}
