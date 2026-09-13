using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Wasm2Cs;
using static ExecutionChecks;

internal static class TypedIrChecks
{
    private static byte[] Type(byte[] parameters, byte[] results) => [0x60, ..U32(parameters.Length), ..parameters, ..U32(results.Length), ..results];
    private static byte[] TypedModule(byte[] parameters, byte[] results, byte[] instructions, byte[][]? types = null, byte[]? locals = null)
    {
        types ??= [Type(parameters, results)];
        byte[] body = [..(locals ?? [0]), ..instructions];
        return [0,97,115,109,1,0,0,0, ..Section(1, [..U32(types.Length), ..types.SelectMany(t => t)]),
            ..Section(3, [1,0]), ..Section(7, [1,1,(byte)'f',0,0]), ..Section(10, [1,..U32(body.Length),..body])];
    }
    private static void Reject(byte[] bytes, string diagnostic)
    {
        try { Transpiler.Translate(bytes, "Subject"); }
        catch (WasmException e) when (e.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected rejection: " + diagnostic);
    }
    private static object? Invoke(byte[] bytes, params object[] arguments)
    {
        var type = ExecutionChecks.Compile(bytes, portable: true).GetType("Wasm2Cs.Generated.Subject")!;
        return type.GetMethod("f")!.Invoke(Activator.CreateInstance(type), arguments);
    }
    public static async Task Verify()
    {
        // s33 indices must not be confused with single-byte value types (64, 127) or u32.
        foreach (int index in new[] { 0, 63, 64, 127, 128 })
        {
            var types = Enumerable.Range(0, index+1).Select(_ => Type([], [0x7f])).ToArray();
            var encoded = S32(index);
            byte[] module = TypedModule([], [0x7f], [0x02,..encoded,0x41,42,0x0b,0x0b], types);
            if (!Equals(Invoke(module), 42)) throw new Exception("Signed block type index failed: " + index);
        }
        // Non-minimal but valid s33 encodings are accepted.
        if (!Equals(Invoke(TypedModule([], [0x7f], [0x02,0x80,0x80,0x80,0x80,0,0x41,42,0x0b,0x0b])), 42))
            throw new Exception("Padded s33 index failed.");
        foreach (var (encoding, diagnostic) in new (byte[], string)[] {
            ([1], "block type index"), ([0x7a], "block type index"), ([0xff,0x7f], "block type index"),
            ([0xff,0xff,0xff,0xff,0x0f], "block type index"),
            ([0x80,0x80,0x80,0x80,0x10], "33 bits"), ([0x80,0x80,0x80,0x80,0x80,0], "too long") })
            Reject(TypedModule([], [0x7f], [0x02,..encoding,0x41,0,0x0b,0x0b]), diagnostic);
        if (Invoke(TypedModule([], [0x70], [0xd0,0x70,0x0b])) is not null)
            throw new Exception("ref.null did not produce null.");
        if (!Equals(Invoke(TypedModule([0x70], [0x7f], [0x20,0,0xd1,0x0b]), new object[] { null! }), 1))
            throw new Exception("ref.is_null did not recognize null.");
        if (Invoke(TypedModule([], [0x70], [0xd2,0,0x0b])) is not Delegate)
            throw new Exception("ref.func did not produce a delegate.");
        ExecutionChecks.Compile(TypedModule([], [0x7f], [0x00,0xd1,0x0b]), portable: true);
        Reject(TypedModule([], [], [0xfd,0x00,0x0b]), "opcode 0xfd/0");
        Reject(TypedModule([], [], [0x41,0,0x41,1,0xfd,0xe4,0x01,0x0b]), "expected V128");
        Reject(TypedModule([0x7b], [], [0x0b]), "does not support v128");
        Reject(TypedModule([], [], [0x1c,0,0x0b]), "exactly one");
        Reject(TypedModule([], [], [0x00,0x02,0x40,0x1a,0x0b,0x0b]), "underflow");
        Reject(TypedModule([], [], [0x02,0x40,0x02,0x7f,0x00,0x0e,1,0,1,0x0b,0x1a,0x0b,0x0b]), "arities");
        // A known type on an unreachable stack remains known after a select.
        Reject(TypedModule([0x7e], [0x7f], [0x00,0x20,0,0x41,0,0x1b,0x0b]), "type mismatch");
        // The untyped bottom of select can feed any typed consumer in dead code.
        ExecutionChecks.Compile(TypedModule([], [0x7e], [0x00,0x1b,0x0b]), portable: true);
        ExecutionChecks.Compile(TypedModule([], [], [0x00,0x1b,0x21,0,0x0b], locals: [1,1,0x7d]), portable: true);
        ExecutionChecks.Compile(TypedModule([], [], [0x00,0x02,0,0x0b,0x0b], types: [Type([],[])]), portable: true);
        foreach (byte valueType in new byte[] { 0x7e, 0x7d, 0x7c, 0x70, 0x6f })
            ExecutionChecks.Compile(TypedModule([valueType], [valueType], [0x02,valueType,0x20,0,0x0b,0x0b]), portable: true);

        // Reference types have distinct validation types and can cross existing typed operations.
        object marker = new object();
        Action function = () => { };
        foreach (var (valueType, first, second) in new (byte, object, object)[] { (0x6f, marker, new object()), (0x70, function, (Action)(() => { })) })
        {
            byte[] module = TypedModule([valueType,valueType,0x7f], [valueType], [0x20,0,0x20,1,0x20,2,0x1c,1,valueType,0x0b]);
            if (!ReferenceEquals(Invoke(module, first, second, 1), first) || !ReferenceEquals(Invoke(module, first, second, 0), second))
                throw new Exception("Typed reference select changed identity.");
            Reject(TypedModule([valueType,valueType,0x7f], [valueType], [0x20,0,0x20,1,0x20,2,0x1b,0x0b]), "numeric operands");
        }
        VerifyImport();
        VerifyValues();
        using (var unknown = JsonDocument.Parse("""{"SchemaVersion":2,"Cases":[{"Kind":"unknown","Line":1}]}"""))
        {
            try { ConformanceChecks.VerifyCases(unknown.RootElement); throw new Exception("Unknown command accepted."); }
            catch (Exception e) when (e.Message.StartsWith("Unknown conformance command:", StringComparison.Ordinal)) { }
        }
        Console.WriteLine("PASS: typed imports/references, s33 block types, polymorphic validation, unsupported opcodes, and exact primitive bit transport.");
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ConformanceTools/test.mjs"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Conformance/typed-ir.json"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Conformance/i64.json"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Conformance/i64-memory.json"));
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync();
        if (node.ExitCode != 0) throw new Exception(await error);
        Console.Write(await output);
    }
    private static void VerifyImport()
    {
        byte[] signature = Type([0x7e,0x7d,0x7c], [0x7c,0x7e,0x7d]);
        byte[] body = [0,0x20,0,0x20,1,0x20,2,0x10,0,0x0b];
        byte[] module = [0,97,115,109,1,0,0,0, ..Section(1,[1,..signature]), ..Section(2,[1,0,0,0,0]),
            ..Section(3,[1,0]), ..Section(7,[1,1,(byte)'f',0,1]), ..Section(10,[1,..U32(body.Length),..body])];
        var type = ExecutionChecks.Compile(module, portable: true).GetType("Wasm2Cs.Generated.Subject")!;
        int calls = 0;
        Func<long,float,double,(double,long,float)> host = (a,b,c) => { calls++; return (c,a,b); };
        var import = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import0")!, host.Target, host.Method);
        var value = type.GetMethod("f")!.Invoke(Activator.CreateInstance(type, [import]), [9007199254740993L, -0.0f, double.NegativeInfinity]);
        if (value is not ValueTuple<double,long,float> tuple || tuple.Item1 != double.NegativeInfinity || tuple.Item2 != 9007199254740993L ||
            BitConverter.SingleToInt32Bits(tuple.Item3) != int.MinValue || calls != 1)
            throw new Exception("Typed multi-result import changed values or invocation count.");
    }
    private static void VerifyValues()
    {
        var patterns = new Dictionary<string,string[]> {
            ["i32"] = ["00000000","ffffffff","80000000","7fffffff"],
            ["i64"] = ["0000000000000000","ffffffffffffffff","8000000000000000","7fffffffffffffff","0020000000000001"],
            ["f32"] = ["00000000","80000000","00000001","7f800000","ff800000","7fc00123","7f800001"],
            ["f64"] = ["0000000000000000","8000000000000000","0000000000000001","7ff0000000000000","fff0000000000000","7ff8000000000123","7ff0000000000001"] };
        foreach (var (type, values) in patterns)
            foreach (string bits in values)
            {
                var expected = new ConformanceValue(type,bits);
                var transported = JsonSerializer.Deserialize<ConformanceValue>(JsonSerializer.Serialize(expected))!;
                if (ConformanceValue.FromRuntime(type, transported.ToRuntime()) != expected) throw new Exception("Bit roundtrip failed: " + expected);
            }
        if (!new ConformanceValue("f32", NaN: "canonical").Matches(BitConverter.Int32BitsToSingle(unchecked((int)0xffc00000))) ||
            new ConformanceValue("f32", NaN: "canonical").Matches(BitConverter.Int32BitsToSingle(0x7fc00001)) ||
            !new ConformanceValue("f64", NaN: "arithmetic").Matches(BitConverter.Int64BitsToDouble(0x7ff8000000001234)) ||
            new ConformanceValue("f64", NaN: "arithmetic").Matches(double.PositiveInfinity) ||
            new ConformanceValue("f64", NaN: "arithmetic").Matches(BitConverter.Int64BitsToDouble(0x7ff0000000000001)))
            throw new Exception("Incorrect NaN matching rules.");
    }
}
