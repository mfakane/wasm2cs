using System;
using Unity.Mathematics;
using Wasm2Cs.Generated;

public static class VectorChecks
{
    public static void Verify()
    {
        float4 result = new Vector().f();
        if (result.x != 22f || result.y != 44f || result.z != 66f || result.w != 88f)
            throw new Exception("Unity.Mathematics vector lowering changed lane results.");
        UnityEngine.Debug.Log("WASM2CS_VECTOR_PASS");
    }
}
