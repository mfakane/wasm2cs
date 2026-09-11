using System;
using UnityEngine;
using Wasm2Cs.Generated;

public static class SmokeRunner
{
    public static void Verify()
    {
        var module = new Arithmetic();
        if (module.add(20, 22) != 42 || module.square(7) != 49 || module.add(int.MaxValue, 1) != int.MinValue)
            throw new Exception("Generated arithmetic differs from WASM.");
        Debug.Log("WASM2CS_SMOKE_PASS");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Run()
    {
        try { Verify(); if (!Application.isEditor) Application.Quit(0); }
        catch (Exception e) { Debug.LogException(e); if (!Application.isEditor) Application.Quit(1); else throw; }
    }
}
