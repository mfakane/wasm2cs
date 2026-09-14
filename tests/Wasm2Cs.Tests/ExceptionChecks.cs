using System.Reflection;
using Wasm2Cs;

internal static class ExceptionChecks
{
    public static async Task Verify()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Exceptions", "Exceptions.wasm"));
        await ExecutionChecks.Compare(bytes, [[0], [41], [int.MaxValue]]);
        await ExecutionChecks.Compare(bytes, [[41]], "direct");
        await ExecutionChecks.Compare(bytes, [[41]], "indirect");
        await ExecutionChecks.Compare(bytes, [[41]], "try_branch");
        await ExecutionChecks.Compare(bytes, [[]], "catch_branch");
        await ExecutionChecks.Compare(bytes, [[]], "catch_all");
        await ExecutionChecks.Compare(bytes, [[41]], "rethrow");
        await ExecutionChecks.Compare(bytes, [[]], "different");
        await ExecutionChecks.Compare(bytes, [[19]], "branch");
        await ExecutionChecks.Compare(bytes, [[]], "trap");
        var type = ExecutionChecks.Compile(bytes, "Exceptions").GetType("Wasm2Cs.Generated.Exceptions")!;
        var instance = Activator.CreateInstance(type)!;
        int Call(string name, params object[] args) => (int)type.GetMethod(name)!.Invoke(instance, args)!;
        if (Call("f", 41) != 42 || Call("direct", 41) != 42 || Call("indirect", 41) != 42 ||
            Call("try_branch", 41) != 41 || Call("catch_branch") != 41 ||
            Call("catch_all") != 7 || Call("rethrow", 41) != 42 ||
            Call("different") != 2 || Call("branch", 19) != 19)
            throw new Exception("WASM exception catch, rethrow, or branch changed the result.");
        try { Call("trap"); throw new Exception("Trap was caught as a WASM exception."); }
        catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "TrapException") { }
        var importedBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Exceptions", "Imported.wasm"));
        var importedType = ExecutionChecks.Compile(importedBytes, "ImportedException").GetType("Wasm2Cs.Generated.ImportedException")!;
        var tag = new WasmTag(WasmValueType.I32);
        var imported = Activator.CreateInstance(importedType, [tag])!;
        if ((int)importedType.GetMethod("f")!.Invoke(imported, [41])! != 42)
            throw new Exception("Imported WASM tag identity or payload changed.");
        try { Activator.CreateInstance(importedType, [new WasmTag(WasmValueType.I64)]); throw new Exception("Accepted an incompatible tag import."); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }
        var hostBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Exceptions", "HostException.wasm"));
        var hostType = ExecutionChecks.Compile(hostBytes, "HostException").GetType("Wasm2Cs.Generated.HostException")!;
        var callbackType = hostType.GetNestedType("__wasm_Import0", BindingFlags.Public)!;
        var callback = Delegate.CreateDelegate(callbackType, typeof(ExceptionChecks).GetMethod(nameof(ThrowHost), BindingFlags.Static | BindingFlags.NonPublic)!);
        var host = Activator.CreateInstance(hostType, [callback])!;
        try { hostType.GetMethod("f")!.Invoke(host, [41]); throw new Exception("Host exception was caught as a WASM exception."); }
        catch (TargetInvocationException e) when (e.InnerException?.GetType() == typeof(InvalidOperationException)) { }
        var hostTagBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Exceptions", "HostTagException.wasm"));
        var hostTagType = ExecutionChecks.Compile(hostTagBytes, "HostTagException").GetType("Wasm2Cs.Generated.HostTagException")!;
        var hostTag = new WasmTag(WasmValueType.I32);
        var hostTagCallbackType = hostTagType.GetNestedType("__wasm_Import0", BindingFlags.Public)!;
        var hostTagCallback = Delegate.CreateDelegate(hostTagCallbackType, typeof(ExceptionChecks).GetMethod(nameof(ThrowTagged), BindingFlags.Static | BindingFlags.NonPublic)!);
        activeTag = hostTag;
        var hostTagInstance = Activator.CreateInstance(hostTagType, [hostTag, hostTagCallback])!;
        if ((int)hostTagType.GetMethod("f")!.Invoke(hostTagInstance, [41])! != 42)
            throw new Exception("Host-thrown WASM tag was not caught by the importing module.");
        activeTag = null;
        Console.WriteLine("PASS: legacy WASM tags, throw/catch payloads, rethrow, branch escape, and Node.js exception reference.");
    }

    private static WasmTag? activeTag;
    private static void ThrowHost(int value) => throw new InvalidOperationException("host failure");
    private static void ThrowTagged(int value) => throw new WasmThrownException(activeTag!, [value]);
}
