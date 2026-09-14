using System;
using Wasm2Cs;
using UnityEngine;
using Wasm2Cs.Generated;

public static class ExceptionChecks
{
    public static void Verify()
    {
        var module = new Exceptions();
        if (module.value == null || module.f(41) != 42 || module.direct(41) != 42 || module.indirect(41) != 42 ||
            module.catch_all() != 7 || module.rethrow(41) != 42 ||
            module.different() != 2 || module.branch(41) != 41)
            throw new Exception("Unity WASM exception handling differs from WebAssembly.");
        try { module.trap(); throw new Exception("WASM trap was caught as a WASM exception."); }
        catch (Exceptions.TrapException) { }
        var failure = new InvalidOperationException("host failure");
        var host = new HostException(_ => throw failure);
        try { host.f(41); throw new Exception("Host exception was caught as a WASM exception."); }
        catch (InvalidOperationException e) when (ReferenceEquals(e, failure)) { }
        var hostTag = new HostTagException(module.value, _ => throw new WasmThrownException(module.value, new object[] { 41 }));
        if (hostTag.f(41) != 42) throw new Exception("Host-thrown WASM tag was not caught.");
        Debug.Log("WASM2CS_EXCEPTION_PASS");
    }
}
