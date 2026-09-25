using System;
using System.Text;
using UnityEngine;
using Wasm2Cs.Generated;

public static class GeneratedModuleRunner
{
    public static void Verify()
    {
        var arithmetic = new Arithmetic();
        if (arithmetic.add(20, 22) != 42 || arithmetic.square(7) != 49)
            throw new Exception("Obtained arithmetic source differs.");
        var changed = new ArithmeticChangedBytes();
        if (changed.minimum() != 43) throw new Exception("Obtained changed arithmetic source differs: " + changed.minimum());
        var algorithms = new Algorithms();
        int address = algorithms.buffer_ptr();
        algorithms.WriteMemory(unchecked((uint)address), Encoding.ASCII.GetBytes("123456789"));
        if (unchecked((uint)algorithms.crc32(address, 9)) != 0xcbf43926u)
            throw new Exception("Obtained clang source differs.");
        Debug.Log("WASM2CS_GENERATED_PASS");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Run()
    {
        try
        {
            Verify();
            if (!Application.isEditor) Application.Quit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (!Application.isEditor) Application.Quit(1);
            else throw;
        }
    }
}
