using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Wasm2Cs;
using static ExecutionChecks;

internal static class FloatChecks
{
    private sealed record Operation(string Name, string Instruction, byte Opcode, string[] Parameters, string Result, bool Arithmetic, int Subopcode = 0);
    private sealed record Call(string Export, ConformanceValue[] Args, string[] Results, bool Arithmetic = false);
    private static ulong state = 0xbb67ae8584caa73bUL;
    private static ulong Next() { state ^= state << 13; state ^= state >> 7; state ^= state << 17; return state; }
    private static ConformanceValue Bits(string type, ulong bits) => new(type, (type.EndsWith("32", StringComparison.Ordinal) ? bits & uint.MaxValue : bits).ToString(type.EndsWith("32", StringComparison.Ordinal) ? "x8" : "x16", CultureInfo.InvariantCulture));
    private static ConformanceValue I32(int value) => Bits("i32", unchecked((uint)value));
    private static byte TypeCode(string type) => type switch { "i32" => 0x7f, "i64" => 0x7e, "f32" => 0x7d, "f64" => 0x7c, _ => throw new Exception(type) };
    private static ConformanceValue[] Boundaries(string type)
    {
        var bits = new HashSet<ulong> { 0, 1, 0x7fffffff, 0x80000000, 0xffffffff, 0x7fffffffffffffff, 0x8000000000000000, ulong.MaxValue };
        if (type.StartsWith("f", StringComparison.Ordinal))
        {
            ulong sign = type == "f32" ? 0x80000000 : 0x8000000000000000;
            ulong infinity = type == "f32" ? 0x7f800000 : 0x7ff0000000000000UL;
            ulong quiet = type == "f32" ? 0x400000 : 0x8000000000000UL;
            foreach (ulong magnitude in new ulong[] { 0, 1, infinity, infinity-1, infinity+1, infinity+quiet, infinity+quiet+0x12345 })
            { bits.Add(magnitude); bits.Add(magnitude | sign); }
            foreach (double value in new double[] { 0.5, 1, 1.5, 2.5, 3.5, Math.Pow(2,-126), Math.Pow(2,-1022), Math.Pow(2,24), Math.Pow(2,31), Math.Pow(2,32), Math.Pow(2,53), Math.Pow(2,63), Math.Pow(2,64) })
            {
                ulong center = type == "f32" ? unchecked((uint)BitConverter.SingleToInt32Bits((float)value)) : unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
                foreach (ulong adjacent in new[] { center == 0 ? 0 : center-1, center, center+1 })
                { bits.Add(adjacent); bits.Add(adjacent | sign); }
            }
        }
        else
        {
            // Integers immediately around f32/f64 rounding midpoints detect double rounding.
            for (int exponent = 24; exponent < 64; exponent++)
                foreach (int precision in new[] { 24, 53 })
                    if (exponent >= precision)
                        foreach (ulong midpoint in new[] { (1UL << exponent) + (1UL << (exponent-precision)), (1UL << exponent) + 3*(1UL << (exponent-precision)) })
                            foreach (ulong adjacent in new[] { midpoint-1, midpoint, midpoint+1 })
                            { bits.Add(adjacent); bits.Add(unchecked(0UL-adjacent)); }
        }
        return bits.Select(b => Bits(type,b)).Distinct().ToArray();
    }
    public static async Task Verify()
    {
        var operations = JsonSerializer.Deserialize<Operation[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Floating/operations.json")))!;
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Floating/Floating.wasm"));
        var calls = new List<Call>();
        foreach (var op in operations)
        {
            var boundaries = Boundaries(op.Parameters[0]);
            foreach (var a in boundaries)
                if (op.Parameters.Length == 1) calls.Add(new(op.Name,[a],[op.Result],op.Arithmetic));
                else foreach (var b in boundaries) calls.Add(new(op.Name,[a,b],[op.Result],op.Arithmetic));
            for (int i = 0; i < 512; i++) calls.Add(new(op.Name,op.Parameters.Select(t => Bits(t,Next())).ToArray(),[op.Result],op.Arithmetic));
        }
        int numerics = calls.Count;
        byte[] memory = new byte[65536];
        for (int i = 0; i < memory.Length; i++) memory[i] = unchecked((byte)Next());
        foreach (string width in new[] { "32", "64" })
        {
            foreach (int address in new[] { 0,1,2,65521,65522,65525,65526,65532,65535,65536,-1,int.MinValue,int.MaxValue })
            {
                calls.Add(new("load"+width,[I32(address)],["f"+width]));
                foreach (var value in Boundaries("f"+width)) calls.Add(new("store"+width,[I32(address),value],[]));
                calls.Add(new("offset"+width,[I32(address)],["f"+width]));
            }
            foreach (var value in Boundaries("f"+width)) calls.Add(new("state"+width,[value],["f"+width]));
            calls.Add(new("constant"+width,[],["f"+width]));
            calls.Add(new("zero"+width,[],["f"+width]));
        }
        calls.Add(new("precision32",[],["f32"]));
        calls.Add(new("mixed",[Bits("f32",0xff800001),Bits("f64",0xfff0000000000001)],["f32","f64"]));
        calls.Add(new("store64",[I32(65530),Bits("f64",0xffffffffffffffff)],[]));
        await Compare(bytes,calls.ToArray(),memory);
        Validation(operations);
        Console.WriteLine($"PASS: {numerics} fixed-seed outcomes across {operations.Length} float/conversion instructions; {calls.Count-numerics} memory/global/local/multi-result cases, full memory and typed traps (C# 9, checked, .NET Standard 2.0).");
        var reference = new ProcessStartInfo("node") { RedirectStandardOutput=true, RedirectStandardError=true };
        reference.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"ConformanceTools/test.mjs"));
        foreach (string name in new[] { "f32","f64","f32_cmp","f64_cmp","f32_bitwise","f64_bitwise","conversions","float_literals" })
            reference.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"Conformance",name+".json"));
        using var process = Process.Start(reference)!;
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        Console.Write(await output);
    }
    private static bool IsNaN(ConformanceValue value) => value.Type switch
    {
        "f32" => (Convert.ToUInt64(value.Bits,16) & 0x7fffffff) > 0x7f800000,
        "f64" => (Convert.ToUInt64(value.Bits,16) & 0x7fffffffffffffff) > 0x7ff0000000000000,
        _ => false
    };
    private static bool Canonical(ConformanceValue value) => !IsNaN(value) ||
        (Convert.ToUInt64(value.Bits,16) & (value.Type == "f32" ? 0x7fffffffUL : 0x7fffffffffffffffUL)) == (value.Type == "f32" ? 0x7fc00000UL : 0x7ff8000000000000UL);
    private static async Task Compare(byte[] bytes, Call[] calls, byte[] memory)
    {
        string source;
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); source = Transpiler.Translate(bytes,"Subject"); }
        finally { CultureInfo.CurrentCulture = culture; }
        if (source != Transpiler.Translate(bytes,"Subject")) throw new Exception("Floating-point generation depends on culture.");
        var type = ExecutionChecks.Compile(bytes,portable:true).GetType("Wasm2Cs.Generated.Subject")!;
        var seen32 = new List<int>(); var seen64 = new List<long>();
        Func<float,float> host32 = value => { seen32.Add(BitConverter.SingleToInt32Bits(value)); return value; };
        Func<double,double> host64 = value => { seen64.Add(BitConverter.DoubleToInt64Bits(value)); return value; };
        object?[] imports = [Delegate.CreateDelegate(type.GetNestedType("__wasm_Import0")!,host32.Target,host32.Method),
            Delegate.CreateDelegate(type.GetNestedType("__wasm_Import1")!,host64.Target,host64.Method)];
        var instance = Activator.CreateInstance(type,imports);
        type.GetMethod("WriteMemory")!.Invoke(instance,[0u,memory]);
        var start = new ProcessStartInfo("node") { RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"float-oracle.mjs"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Module=Convert.ToBase64String(bytes), Calls=calls, Memory=Convert.ToBase64String(memory) }));
        process.StandardInput.Close(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        using var document = JsonDocument.Parse(await output);
        var outcomes = document.RootElement.GetProperty("Outcomes").EnumerateArray().ToArray();
        if (outcomes.Length != calls.Length) throw new Exception("Float oracle count differs.");
        for (int i=0;i<calls.Length;i++)
        {
            var call = calls[i]; var expected = outcomes[i];
            var native = type.GetMethod(call.Export)!;
            var raw = type.GetMethod("__wasm_bits_"+call.Export);
            foreach (var method in raw == null ? new[] { native } : new[] { native, raw })
            {
                bool useBits = method == raw;
                object? value;
                string context = $"{call.Export}({string.Join(",",call.Args.Select(a=>a.Bits))})";
                try { value = method.Invoke(instance,call.Args.Select(a=> (useBits && a.Type.StartsWith("f",StringComparison.Ordinal) ? a with { Type="i"+a.Type.Substring(1) } : a).ToRuntime()).ToArray()); }
                catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
                {
                    string trap = e.InnerException.GetType().GetProperty("Kind")!.GetValue(e.InnerException)!.ToString()!;
                    if (!expected.TryGetProperty("Trap",out var t) || t.GetString() != trap) throw new Exception(context+": unexpected "+trap);
                    continue;
                }
                if (expected.TryGetProperty("Trap",out _)) throw new Exception(context+": missing trap");
                object?[] actual = value switch { null=>[], ITuple tuple=>Enumerable.Range(0,tuple.Length).Select(n=>tuple[n]).ToArray(), _=>[value] };
                var values = expected.GetProperty("Values").EnumerateArray().Select(ConformanceValue.Read).ToArray();
                if (actual.Length != values.Length) throw new Exception(context+": result arity");
                for (int n=0;n<values.Length;n++)
                {
                    var oracle = values[n];
                    if (call.Arithmetic && IsNaN(oracle)) oracle = new(oracle.Type,NaN:call.Args.All(Canonical) ? "canonical" : "arithmetic");
                    if (useBits && oracle.Type.StartsWith("f",StringComparison.Ordinal))
                        actual[n] = (ConformanceValue.FromRuntime("i"+oracle.Type.Substring(1),actual[n]) with { Type=oracle.Type }).ToRuntime();
                    if (!oracle.Matches(actual[n])) throw new Exception($"{context}: expected {oracle}, actual {ConformanceValue.FromRuntime(oracle.Type,actual[n])}");
                }
            }
        }
        var actualMemory = (byte[])type.GetMethod("ReadMemory")!.Invoke(instance,[0u,memory.Length])!;
        if (Convert.ToBase64String(actualMemory) != document.RootElement.GetProperty("Memory").GetString()) throw new Exception("Float memory differs, including trapping writes.");
        foreach (var global in document.RootElement.GetProperty("Globals").EnumerateObject())
            if (!ConformanceValue.Read(global.Value).Matches(type.GetProperty(global.Name)!.GetValue(instance))) throw new Exception("Float global differs.");
        foreach (string width in new[] { "32","64" })
        {
            var values = Boundaries("f"+width).Concat(Enumerable.Range(0,512).Select(_=>Bits("f"+width,Next()))).Select(v=>v with { Type="i"+width }).ToArray();
            foreach (var v in values) if (!v.Matches(type.GetMethod("host"+width)!.Invoke(instance,[v.ToRuntime()]))) throw new Exception("Float import roundtrip changed bits.");
            var seen = width == "32" ? seen32.Select(v=>Bits("i32",unchecked((uint)v))) : seen64.Select(v=>Bits("i64",unchecked((ulong)v)));
            if (!seen.SequenceEqual(values)) throw new Exception("Float import argument bits/order differ.");
        }
        var second = Activator.CreateInstance(type,imports);
        type.GetProperty("g32")!.SetValue(instance,BitConverter.Int32BitsToSingle(1));
        if (!Bits("f32",1).Matches(type.GetProperty("g32")!.GetValue(instance)) || !Bits("f32",0xff812345).Matches(type.GetProperty("g32")!.GetValue(second))) throw new Exception("Exported float global setter/isolation failed.");
        type.GetProperty("g64")!.SetValue(instance,BitConverter.Int64BitsToDouble(1));
        if (!Bits("f64",1).Matches(type.GetProperty("g64")!.GetValue(instance)) || !Bits("f64",0xfff123456789abcd).Matches(type.GetProperty("g64")!.GetValue(second))) throw new Exception("Exported f64 global setter/isolation failed.");
    }
    private static byte[] Function(byte[] parameters, byte[] results, byte[] code, bool memory = false) => [0,97,115,109,1,0,0,0,
        ..Section(1,[1,0x60,..U32(parameters.Length),..parameters,..U32(results.Length),..results]),
        ..Section(3,[1,0]),..(memory ? Section(5,[1,0,1]) : []),..Section(10,[1,..U32(code.Length+1),0,..code])];
    private static void Reject(byte[] bytes,string diagnostic)
    {
        try { Transpiler.Translate(bytes,"Subject"); }
        catch (WasmException e) when (e.Message.Contains(diagnostic,StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected floating-point validation failure: "+diagnostic);
    }
    private static void Validation(Operation[] operations)
    {
        foreach (var op in operations)
        {
            byte[] parameters=op.Parameters.Select(TypeCode).ToArray();
            byte[] code=[..Enumerable.Range(0,parameters.Length).SelectMany(i=>new byte[]{0x20,(byte)i}),op.Opcode,..(op.Opcode==0xfc ? U32(op.Subopcode) : []),0x0b];
            byte wrong = parameters[0] == 0x7f ? (byte)0x7e : (byte)0x7f;
            Reject(Function([wrong,..parameters.Skip(1)],[TypeCode(op.Result)],code),"type mismatch");
            Reject(Function(parameters,[op.Result=="i32" ? (byte)0x7e : (byte)0x7f],code),"type mismatch");
        }
        foreach (var (opcode,width) in new (byte,int)[]{(0x43,4),(0x44,8)})
            for (int count=0;count<width;count++) Reject(Function([],[],[opcode,..new byte[count]]),"Unexpected end");
        foreach (byte op in new byte[]{0x2a,0x2b,0x38,0x39}) Reject(Function([],[],[0x00,op,0,0,0x0b]),"requires memory");
        foreach (byte op in new byte[]{0x2a,0x2b,0x38,0x39})
            Reject(Function([],[],[0x00,op,(byte)(op%2==0 ? 3 : 4),0,0x0b],memory:true),"natural alignment");
        Reject(Function([],[],[0x00,0xfc,8,0x0b]),"opcode 0xfc/8");
        Reject(Function([],[],[0xfc,0x80]),"Unexpected end");
        Reject(Function([],[],[0xfc,0x80,0x80,0x80,0x80,0x80,0]),"too long");
    }
}
