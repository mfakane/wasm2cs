using System;
using UnityEngine;
using Wasm2Cs.Generated;

public static class TableChecks
{
    public static void Verify()
    {
        var module = new FunctionPointers();
        if (module.call(41, 0) != 42)
            throw new Exception("Unity table indirect call differs from WebAssembly.");
        if (module.table == null || module.table.CurrentSize != 1)
            throw new Exception("Unity table export is missing or has the wrong size.");
        Debug.Log("WASM2CS_TABLE_PASS");
    }
}
