using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Wasm2Cs;
using static ExecutionChecks;

internal static class I64Checks
{
    private sealed record Function(string Name, byte[] Parameters, byte[] Results, byte[] Code);
    private sealed record Call(string Export, ConformanceValue[] Args, string[] Results);
    private static ConformanceValue I64(long n) => ConformanceValue.FromRuntime("i64", n);
    private static ConformanceValue I32(int n) => ConformanceValue.FromRuntime("i32", n);
    private static byte[] Signature(byte[] parameters, byte[] results) => [0x60, ..U32(parameters.Length), ..parameters, ..U32(results.Length), ..results];
    private static byte[] Name(string name) { byte[] bytes = Encoding.UTF8.GetBytes(name); return [..U32(bytes.Length), ..bytes]; }
    private static byte[] Module(Function[] functions, bool import = false)
    {
        int offset = import ? 1 : 0;
        var types = functions.SelectMany(f => Signature(f.Parameters, f.Results));
        var exports = functions.SelectMany((f, i) => (byte[])[..Name(f.Name), 0, ..U32(i + offset)]);
        var bodies = functions.SelectMany(f => (byte[])[..U32(f.Code.Length + 1), 0, ..f.Code]);
        return [0,97,115,109,1,0,0,0,
            ..Section(1, [..U32(functions.Length + offset), ..(import ? Signature([0x7e],[0x7e]) : []), ..types]),
            ..(import ? Section(2, [1,..Name("env"),..Name("exchange"),0,0]) : []),
            ..Section(3, [..U32(functions.Length), ..Enumerable.Range(offset, functions.Length).SelectMany(U32)]),
            ..Section(7, [..U32(functions.Length), ..exports]), ..Section(10, [..U32(functions.Length), ..bodies])];
    }
    private static byte[] S64(long value)
    {
        var bytes = new List<byte>();
        bool more;
        do
        {
            byte next = (byte)(value & 127);
            value >>= 7;
            more = !((value == 0 && (next & 64) == 0) || (value == -1 && (next & 64) != 0));
            bytes.Add((byte)(next | (more ? 128 : 0)));
        } while (more);
        return bytes.ToArray();
    }
    private static readonly long[] Boundaries = [0,1,-1,long.MinValue,long.MaxValue,
        int.MinValue,int.MaxValue,0xffffffffL,0x100000000L,9007199254740993L,
        0x0123456789abcdefL,unchecked((long)0xfedcba9876543210UL),
        63,64,65,-63,-64,-65,127,128,255,256,32767,32768,65535,65536];
    private static ulong randomState = 0x6a09e667f3bcc909UL;
    private static long NextValue()
    {
        randomState ^= randomState << 13;
        randomState ^= randomState >> 7;
        randomState ^= randomState << 17;
        return unchecked((long)randomState);
    }
    public static async Task Verify()
    {
        await Numerics();
        await Leb();
        await MemoryAndGlobals();
        await Imports();
        Validation();
    }
    private static async Task Compare(byte[] bytes, Call[] calls, byte[]? memory = null, long? importMask = null)
    {
        var type = ExecutionChecks.Compile(bytes, portable: true).GetType("Wasm2Cs.Generated.Subject")!;
        var hostCalls = new List<ConformanceValue>();
        object? instance;
        if (importMask.HasValue)
        {
            Func<long,long> exchange = value => { hostCalls.Add(I64(value)); return value ^ importMask.Value; };
            var callback = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import0")!, exchange.Target, exchange.Method);
            instance = Activator.CreateInstance(type, [callback]);
        }
        else instance = Activator.CreateInstance(type);
        if (memory != null) type.GetMethod("WriteMemory")!.Invoke(instance, [0u, memory]);
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "i64-oracle.mjs"));
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Module = Convert.ToBase64String(bytes), Calls = calls,
            Memory = memory == null ? null : Convert.ToBase64String(memory), ImportMask = importMask.HasValue ? I64(importMask.Value) : null }));
        node.StandardInput.Close();
        await node.WaitForExitAsync();
        if (node.ExitCode != 0) throw new Exception(await error);
        using var response = JsonDocument.Parse(await output);
        var outcomes = response.RootElement.GetProperty("Outcomes");
        if (outcomes.GetArrayLength() != calls.Length) throw new Exception("i64 oracle result count differs.");
        for (int i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            var expected = outcomes[i];
            bool trapped = false;
            object? value = null;
            try { value = type.GetMethod(call.Export)!.Invoke(instance, call.Args.Select(a => a.ToRuntime()).ToArray()); }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
            {
                trapped = true;
                string actual = e.InnerException!.GetType().GetProperty("Kind")!.GetValue(e.InnerException)!.ToString()!;
                if (!expected.TryGetProperty("Trap", out var trap) || trap.GetString() != actual)
                    throw new Exception($"{call.Export} call {i}: unexpected {actual}");
            }
            if (trapped) continue;
            if (expected.TryGetProperty("Trap", out _)) throw new Exception($"{call.Export} call {i}: missing trap");
            object?[] actualValues = value switch { null => [], ITuple tuple => Enumerable.Range(0, tuple.Length).Select(n => tuple[n]).ToArray(), _ => [value] };
            var expectedValues = expected.GetProperty("Values").EnumerateArray().Select(ConformanceValue.Read).ToArray();
            if (actualValues.Length != expectedValues.Length || actualValues.Where((v, n) => !expectedValues[n].Matches(v)).Any())
                throw new Exception($"{call.Export} call {i}: i64 result differs for {string.Join(", ", call.Args.Select(a => a.Bits))}");
        }
        var expectedMemory = response.RootElement.GetProperty("Memory");
        if (expectedMemory.ValueKind != JsonValueKind.Null)
        {
            var actual = (byte[])type.GetMethod("ReadMemory")!.Invoke(instance, [0u, (int)type.GetProperty("MemorySize")!.GetValue(instance)!])!;
            if (Convert.ToBase64String(actual) != expectedMemory.GetString()) throw new Exception("i64 final memory differs (including bytes after trapping stores).");
        }
        foreach (var global in response.RootElement.GetProperty("Globals").EnumerateObject())
            if (!ConformanceValue.Read(global.Value).Matches(type.GetProperty(global.Name)!.GetValue(instance))) throw new Exception("i64 global differs: " + global.Name);
        var expectedCalls = response.RootElement.GetProperty("HostCalls").EnumerateArray().Select(ConformanceValue.Read);
        if (!hostCalls.SequenceEqual(expectedCalls)) throw new Exception("i64 host argument bits or invocation order differs.");
    }
    private static async Task Numerics()
    {
        // Signatures and opcodes from the integer instruction set; the oracle executes the WASM.
        var operations = new List<(byte Opcode, int Arity, byte Input, byte Result)>();
        foreach (byte opcode in new byte[] { 0x79,0x7a,0x7b,0xc2,0xc3,0xc4 }) operations.Add((opcode,1,0x7e,0x7e));
        foreach (byte opcode in new byte[] { 0x50,0xa7 }) operations.Add((opcode,1,0x7e,0x7f));
        foreach (byte opcode in new byte[] { 0xac,0xad }) operations.Add((opcode,1,0x7f,0x7e));
        foreach (byte opcode in new byte[] { 0xc0,0xc1 }) operations.Add((opcode,1,0x7f,0x7f));
        foreach (byte opcode in Enumerable.Range(0x51,10)) operations.Add((opcode,2,0x7e,0x7f));
        foreach (byte opcode in Enumerable.Range(0x7c,15)) operations.Add((opcode,2,0x7e,0x7e));
        var functions = new List<Function>();
        var calls = new List<Call>();
        foreach (var op in operations)
        {
            string name = $"op_{op.Opcode:x2}";
            functions.Add(new(name, Enumerable.Repeat(op.Input, op.Arity).ToArray(), [op.Result],
                [0x20,0,..(op.Arity == 2 ? new byte[] {0x20,1} : []),op.Opcode,0x0b]));
            ConformanceValue Argument(long n) => op.Input == 0x7e ? I64(n) : I32(unchecked((int)n));
            string[] results = [op.Result == 0x7e ? "i64" : "i32"];
            foreach (long a in Boundaries)
            {
                if (op.Arity == 1) calls.Add(new(name, [Argument(a)], results));
                else foreach (long b in Boundaries) calls.Add(new(name, [Argument(a),Argument(b)], results));
            }
            for (int i = 0; i < 256; i++) calls.Add(new(name,
                op.Arity == 1 ? [Argument(NextValue())] : [Argument(NextValue()),Argument(NextValue())], results));
        }
        await Compare(Module(functions.ToArray()), calls.ToArray());
        Console.WriteLine($"PASS: {calls.Count} fixed-seed integer outcomes across {operations.Count} i64/conversion/sign-extension instructions match Node (C# 9, checked, .NET Standard 2.0).");
    }
    private static async Task Leb()
    {
        var cases = Boundaries.Select(value => (Bytes: S64(value), Value: value)).ToList();
        cases.Add(([..Enumerable.Repeat((byte)0x80,9),0],0));
        cases.Add(([..Enumerable.Repeat((byte)0xff,9),0x7f],-1));
        cases.Add(([0x81,..Enumerable.Repeat((byte)0x80,8),0],1));
        cases.Add(([..Enumerable.Repeat((byte)0x80,9),0x7f],long.MinValue));
        var functions = cases.Select((test, i) => new Function($"c{i}", [], [0x7e], [0x42,..test.Bytes,0x0b])).ToArray();
        byte[] bytes = Module(functions);
        await Compare(bytes, functions.Select(f => new Call(f.Name, [], ["i64"])).ToArray());
        var type = ExecutionChecks.Compile(bytes).GetType("Wasm2Cs.Generated.Subject")!;
        var instance = Activator.CreateInstance(type);
        for (int i = 0; i < cases.Count; i++)
            if (!Equals(type.GetMethod($"c{i}")!.Invoke(instance,null), cases[i].Value)) throw new Exception("i64 constant bits changed.");
        int rejected = 0;
        void Invalid(byte[] payload, string message)
        {
            Reject(Module([new Function("f", [], [0x7e], [0x42,..payload])]), message);
            rejected++;
        }
        for (int last = 1; last < 127; last++) Invalid([..Enumerable.Repeat((byte)0x80,9),(byte)last,0x0b], "64 bits");
        Invalid([..Enumerable.Repeat((byte)0x80,10),0,0x0b], "too long");
        Invalid([..Enumerable.Repeat((byte)0xff,10),0x7f,0x0b], "too long");
        for (int length = 0; length < 10; length++) Invalid(Enumerable.Repeat((byte)0x80,length).ToArray(), "Unexpected end");
        Console.WriteLine($"PASS: {cases.Count} exact i64 constants/padded LEB encodings and {rejected} oversized, overlong, or truncated LEB encodings.");
    }
    private static async Task MemoryAndGlobals()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Conformance/i64-memory.json")));
        byte[] bytes = Convert.FromBase64String(document.RootElement.GetProperty("Cases")[0].GetProperty("Binary").GetString()!);
        byte[] memory = new byte[65536];
        for (int i = 0; i < memory.Length; i++) memory[i] = unchecked((byte)NextValue());
        var calls = new List<Call>();
        string[] loads = ["load","load8_s","load8_u","load16_s","load16_u","load32_s","load32_u","load_offset","load_plus3"];
        int[] addresses = [0,1,2,3,65528,65529,65532,65533,65534,65535,65536,-1,int.MinValue,int.MaxValue];
        foreach (string name in loads) foreach (int address in addresses) calls.Add(new(name, [I32(address)], ["i64"]));
        foreach (string name in new[] { "store","store8","store16","store32","store_offset" })
            foreach (int address in addresses) foreach (long value in Boundaries) calls.Add(new(name, [I32(address),I64(value)], []));
        foreach (long value in Boundaries) calls.Add(new("roundtrip", [I64(value)], ["i64"]));
        calls.Add(new("set_global", [I64(long.MinValue+1)], []));
        // End with a trapping write crossing the last byte, so any partial store remains visible.
        calls.Add(new("store", [I32(65535),I64(-1)], []));
        await Compare(bytes, calls.ToArray(), memory);
        var type = ExecutionChecks.Compile(bytes).GetType("Wasm2Cs.Generated.Subject")!;
        var first = Activator.CreateInstance(type); var second = Activator.CreateInstance(type);
        type.GetProperty("g")!.SetValue(first, 9007199254740993L);
        if (!Equals(type.GetProperty("g")!.GetValue(first),9007199254740993L) || !Equals(type.GetProperty("g")!.GetValue(second),long.MinValue+1))
            throw new Exception("Exported i64 globals lost bits or instance isolation.");
        Console.WriteLine($"PASS: {calls.Count} i64 memory/global outcomes, signed narrow loads, unaligned access, unsigned addresses/offsets, truncated stores, bounds traps, full memory, and global isolation.");
    }
    private static async Task Imports()
    {
        byte[] bytes = Module([new Function("f", [0x7e], [0x7e], [0x20,0,0x10,0,0x0b])], import: true);
        var values = Boundaries.Concat(Enumerable.Range(0,256).Select(_ => NextValue())).ToArray();
        await Compare(bytes, values.Select(value => new Call("f", [I64(value)], ["i64"])).ToArray(), importMask: unchecked((long)0x9e3779b97f4a7c15UL));
        Console.WriteLine($"PASS: {values.Length} i64 host import/export roundtrips preserve bits and callback order.");
    }
    private static void Reject(byte[] bytes, string diagnostic)
    {
        try { Transpiler.Translate(bytes, "Subject"); }
        catch (WasmException e) when (e.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected i64 rejection: " + diagnostic);
    }
    private static void Validation()
    {
        foreach (byte opcode in new byte[] { 0x79,0x7a,0x7b,0x50,0xa7,0xc2,0xc3,0xc4 })
            Reject(Module([new Function("f", [0x7f], [], [0x20,0,opcode,0x1a,0x0b])]), "type mismatch");
        foreach (byte opcode in new byte[] { 0xac,0xad })
            Reject(Module([new Function("f", [0x7e], [], [0x20,0,opcode,0x1a,0x0b])]), "type mismatch");
        Reject(Module([new Function("f", [0x7e,0x7f], [0x7e], [0x20,0,0x20,1,0x86,0x0b])]), "type mismatch");
        Reject(Module([new Function("f", [0x7e], [0x7e], [0x20,0,0x50,0x0b])]), "type mismatch");
        Reject(Module([new Function("f", [0x7e], [0x7e], [0x20,0,0xa7,0x0b])]), "type mismatch");
        foreach (byte opcode in new byte[] { 0x29,0x30,0x31,0x32,0x33,0x34,0x35,0x37,0x3c,0x3d,0x3e })
            Reject(Module([new Function("f", [], [], [0x00,opcode,0,0,0x0b])]), "requires memory");
        Console.WriteLine("PASS: i64 operation signatures and memory presence validation.");
    }
}
