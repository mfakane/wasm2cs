using System;
using UnityEngine;
using Wasm2Cs.Generated;

public static class SmokeRunner
{
    public static void Verify()
    {
        HostChecks.Verify();
        FloatingChecks.Verify();
        VectorChecks.Verify();
        TableChecks.Verify();
        Debug.Log("WASM2CS_FLOATING_PASS");
        var module = new Arithmetic();
        if (module.add(20, 22) != 42 || module.square(7) != 49 || module.add(int.MaxValue, 1) != int.MinValue)
            throw new Exception("Generated arithmetic differs from WASM.");
        var algorithms = new Algorithms();
        int address = algorithms.buffer_ptr();
        algorithms.WriteMemory(unchecked((uint)address), System.Text.Encoding.ASCII.GetBytes("123456789"));
        if (unchecked((uint)algorithms.crc32(address,9)) != 0xcbf43926u || algorithms.sum_bytes(address,9) != 477)
            throw new Exception("Clang-produced CRC32/array computation differs.");
        var data = Resources.Load<TextAsset>("AlgorithmsGolden");
        if (data == null) throw new Exception("Missing WebAssembly oracle vectors.");
        var vectors = JsonUtility.FromJson<GoldenVectors>(data.text);
        if (vectors.Cases == null || vectors.Cases.Length != 35) throw new Exception("Wrong oracle vector count.");
        foreach (var vector in vectors.Cases)
            if (algorithms.f(vector.Seed,vector.Length) != vector.Expected)
                throw new Exception("IL2CPP result differs from WebAssembly oracle.");
        Debug.Log("WASM2CS_SMOKE_PASS");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Run()
    {
        try { Verify(); if (!Application.isEditor) Application.Quit(0); }
        catch (Exception e) { Debug.LogException(e); if (!Application.isEditor) Application.Quit(1); else throw; }
    }

    [Serializable] private sealed class GoldenVectors { public GoldenCase[] Cases; }
    [Serializable] private sealed class GoldenCase { public int Seed; public int Length; public int Expected; }
}
