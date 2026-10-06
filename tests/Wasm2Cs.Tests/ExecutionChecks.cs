using System.Diagnostics;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Wasm2Cs;
using Wasm2Cs.DotnetHost;

internal static class ExecutionChecks
{
    public static void Imports()
    {
        // No defined functions: both start and an export refer to the imported function.
        byte[] bytes = [0,97,115,109,1,0,0,0,
            ..Section(1,[1,0x60,0,0]),
            ..Section(2,[1,3, (byte)'e',10,(byte)'v',1,(byte)'x',0,0]),
            ..Section(7,[1,1,(byte)'f',0,0]), ..Section(8,[0])];
        var type = Compile(bytes).GetType("Wasm2Cs.Generated.Subject")!;
        int calls = 0;
        Action callback = () => calls++;
        var import = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import0")!,callback.Target,callback.Method);
        var instance = Activator.CreateInstance(type,[import]);
        if (calls != 1) throw new Exception("Imported start function did not run.");
        type.GetMethod("f")!.Invoke(instance,null);
        if (calls != 2) throw new Exception("Imported function export did not run.");
        byte[] unsupported = [0,97,115,109,1,0,0,0,
            ..Section(1,[2,0x60,2,0x7f,0x7f,1,0x7f,0x60,0,1,0x7f]),
            ..Section(2,[1,3,(byte)'e',(byte)'n',(byte)'v',7,(byte)'m',(byte)'i',(byte)'s',(byte)'s',(byte)'i',(byte)'n',(byte)'g',0,0]),
            ..Section(3,[1,1]), ..Section(7,[1,1,(byte)'f',0,1]),
            ..Section(10,[1,8,0,0x41,11,0x41,22,0x10,0,0x0b])];
        var unsupportedType = Compile(unsupported).GetType("Wasm2Cs.Generated.Subject")!;
        var bindingsType = unsupportedType.GetNestedType("Bindings")!;
        var unsupportedInstance = unsupportedType.GetConstructor([bindingsType])!.Invoke([Activator.CreateInstance(bindingsType)]);
        try
        {
            unsupportedType.GetMethod("f")!.Invoke(unsupportedInstance, null);
            throw new Exception("Unsupported generated import did not fail.");
        }
        catch (TargetInvocationException error) when (error.InnerException is WasmImportException unsupportedImport)
        {
            if (unsupportedImport.ModuleName != "env" || unsupportedImport.ImportName != "missing" ||
                !unsupportedImport.Arguments.SequenceEqual(new object[] { 11, 22 }) ||
                !unsupportedImport.Message.Contains("env.missing", StringComparison.Ordinal) ||
                !unsupportedImport.Message.Contains("11", StringComparison.Ordinal) ||
                !unsupportedImport.Message.Contains("22", StringComparison.Ordinal))
                throw new Exception("Unsupported generated import diagnostic differs.");
        }
        foreach (byte[] invalid in new byte[][] {
            [0,97,115,109,1,0,0,0,..Section(2,[1,0,0,0,0])],
            [0,97,115,109,1,0,0,0,..Section(2,[1,0,0,2,2,0,0])],
            [0,97,115,109,1,0,0,0,..Section(2,[1,0,0,3,0x7f,2])]
        })
        {
            try { Transpiler.Translate(invalid,"Subject"); throw new Exception("Accepted invalid/unsupported import."); }
            catch (WasmException) { }
        }
        Console.WriteLine("PASS: imported start/export, arbitrary import names, and invalid/unsupported imports.");
    }
    internal static byte[] U32(int value)
    {
        var bytes = new List<byte>();
        uint remaining = (uint)value;
        do { byte next = (byte)(remaining & 127); remaining >>= 7; bytes.Add((byte)(next | (remaining == 0 ? 0 : 128))); } while (remaining != 0);
        return bytes.ToArray();
    }
    internal static byte[] Section(byte id, params byte[] bytes) => [id, ..U32(bytes.Length), ..bytes];
    internal static byte[] Module(int parameters, byte[] instructions, int locals = 0, int results = 1)
    {
        byte[] declarations = locals == 0 ? [0] : [1,..U32(locals),0x7f];
        byte[] body = [..declarations, ..instructions];
        return [0,97,115,109,1,0,0,0,
            ..Section(1, [1,0x60,..U32(parameters),..Enumerable.Repeat((byte)0x7f,parameters), (byte)results,..(results == 1 ? new byte[] { 0x7f } : Array.Empty<byte>())]),
            ..Section(3, [1,0]), ..Section(7, [1,1,(byte)'f',0,0]), ..Section(10, [1,..U32(body.Length),..body])];
    }
    internal static byte[] I64Unary(byte opcode)
    {
        byte[] body = [0,0x20,0,opcode,0x0b];
        return [0,97,115,109,1,0,0,0,
            ..Section(1,[1,0x60,1,0x7e,1,0x7e]), ..Section(3,[1,0]),
            ..Section(7,[1,1,(byte)'f',0,0]), ..Section(10,[1,..U32(body.Length),..body])];
    }
    internal static byte[] VectorModule()
    {
        byte[] body = [0,
            0xfd,0x0c,..VectorBytes(0x3f800000,0x40000000,0x40400000,0x40800000),
            0xfd,0x0c,..VectorBytes(0x41200000,0x41a00000,0x41f00000,0x42200000),
            0xfd,0xe4,0x01,
            0xfd,0x0c,..VectorBytes(0x40000000,0x40000000,0x40000000,0x40000000),
            0xfd,0xe6,0x01,0x0b];
        return [0,97,115,109,1,0,0,0,
            ..Section(1,[1,0x60,0,1,0x7b]), ..Section(3,[1,0]),
            ..Section(7,[1,1,(byte)'f',0,0]), ..Section(10,[1,..U32(body.Length),..body])];
    }
    private static byte[] VectorBytes(params uint[] bits) => bits.SelectMany(value => new[]
    {
        unchecked((byte)value), unchecked((byte)(value >> 8)), unchecked((byte)(value >> 16)), unchecked((byte)(value >> 24))
    }).ToArray();
    internal static Assembly Compile(byte[] bytes, string name = "Subject", bool portable = false,
        WasmTargetProfile profile = WasmTargetProfile.PortableNetStandard20)
    {
        var paths = portable ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ReferenceAssemblies"), "*.dll") :
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = paths.Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(RuntimeAssembly(portable)));
        var compilation = CSharpCompilation.Create("Execution_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Transpiler.Translate(bytes,name,profile),new CSharpParseOptions(LanguageVersion.CSharp9))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow:true));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success) throw new Exception(string.Join("\n",emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
    private static string RuntimeAssembly(bool portable)
    {
        string location = typeof(WasmMemory).Assembly.Location;
        if (!portable) return location;
        return Path.Combine(AppContext.BaseDirectory, "Runtime20", "Wasm2Cs.Runtime.dll");
    }
    internal static async Task Compare(byte[] bytes, int[][] calls, string exportName = "f")
    {
        var type = Compile(bytes).GetType("Wasm2Cs.Generated.Subject")!;
        object? instance = null;
        bool initializationTrapped = false;
        try { instance = Activator.CreateInstance(type); }
        catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
        { initializationTrapped = true; }
        var method = type.GetMethod(exportName)!;
        var start = new ProcessStartInfo("node") { RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"execution-oracle.mjs"));
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Module=Convert.ToBase64String(bytes), Calls=calls, Export=exportName }));
        node.StandardInput.Close();
        await node.WaitForExitAsync();
        if (node.ExitCode != 0) throw new Exception(await error);
        using var response = JsonDocument.Parse(await output);
        bool expectedInitializationTrap = response.RootElement.TryGetProperty("InstantiationTrap",out var initialization) && initialization.GetBoolean();
        if (expectedInitializationTrap != initializationTrapped) throw new Exception("Instantiation trap differs.");
        if (initializationTrapped) return;
        var expected = response.RootElement.GetProperty("Outcomes").Deserialize<Outcome[]>()!;
        if (expected.Length != calls.Length) throw new Exception("Oracle result count differs.");
        for (int i=0;i<calls.Length;i++)
        {
            Outcome actual;
            try { actual = new(false, (int)(method.Invoke(instance,calls[i].Cast<object>().ToArray()) ?? 0)); }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
            { actual = new(true,0); }
            if (actual != expected[i]) throw new Exception($"WASM/C# mismatch at ({string.Join(',',calls[i])}): {actual} != {expected[i]}");
        }
        var memory = response.RootElement.GetProperty("Memory");
        if (memory.ValueKind != JsonValueKind.Null)
        {
            var bytesRead = (byte[])type.GetMethod("ReadMemory")!.Invoke(instance,[0u,(int)type.GetProperty("MemorySize")!.GetValue(instance)!])!;
            if (Convert.ToBase64String(bytesRead) != memory.GetString()) throw new Exception("Final memory contents differ.");
        }
        foreach(var global in response.RootElement.GetProperty("Globals").EnumerateObject())
            if ((int)type.GetProperty(global.Name)!.GetValue(instance)! != global.Value.GetInt32()) throw new Exception("Global value differs: "+global.Name);
    }
    public static async Task Numerics()
    {
        int[] values = [0,1,-1,int.MinValue,int.MaxValue,31,32,33,-32,65536,-65536];
        int count=0;
        foreach(byte op in Enumerable.Range(0x45,11).Concat(Enumerable.Range(0x67,18)).Select(i=>(byte)i))
        {
            bool unary = op == 0x45 || op is >= 0x67 and <= 0x69;
            var calls = unary ? values.Select(a=>new[]{a}).ToArray() : values.SelectMany(a=>values.Select(b=>new[]{a,b})).ToArray();
            byte[] instructions = unary ? [0x20,0,op,0x0b] : [0x20,0,0x20,1,op,0x0b];
            await Compare(Module(unary?1:2,instructions),calls);
            count += calls.Length;
        }
        await Compare(Module(3,[0x20,0,0x20,1,0x20,2,0x1b,0x0b]), [[10,20,0],[10,20,1],[10,20,-1]]);
        await Compare(Module(0,[0x00,0x0b]),[[]]);
        var type = Compile(Module(2,[0x20,0,0x20,1,0x6d,0x0b])).GetType("Wasm2Cs.Generated.Subject")!;
        foreach(var (arguments,kind) in new[] { (new[]{1,0},"DivisionByZero"),(new[]{int.MinValue,-1},"IntegerOverflow") })
        {
            try { type.GetMethod("f")!.Invoke(Activator.CreateInstance(type),arguments.Cast<object>().ToArray()); throw new Exception("Missing trap."); }
            catch(TargetInvocationException e)
            {
                if (e.InnerException!.GetType().GetProperty("Kind")?.GetValue(e.InnerException)?.ToString() != kind)
                    throw new Exception("Incorrect trap kind.");
            }
        }
        Console.WriteLine($"PASS: {count+4} i32/selection/unreachable outcomes match WebAssembly, including traps.");
    }
    public static async Task ControlFlow()
    {
        await Compare(Module(2,[0x20,0,0x04,0x7f,0x20,1,0x05,0x41,7,0x0b,0x0b]),[[0,99],[1,99],[-1,42]]);
        await Compare(Module(1,[0x02,0x7f,0x20,0,0x0c,0,0x6a,0x0b,0x0b]),[[0],[1],[-1],[int.MinValue]]);
        await Compare(Module(1,[0x02,0x7f,0x41,42,0x20,0,0x0d,0,0x1a,0x41,9,0x0b,0x0b]),[[0],[1],[-1]]);
        await Compare(Module(1,[0x02,0x7f,0x02,0x7f,0x41,10,0x20,0,0x0e,1,0,1,0x0b,0x41,1,0x6a,0x0b,0x0b]),[[0],[1],[-1],[int.MaxValue]]);
        await Compare(Module(1,[0x20,0,0x0f,0x6a,0x0b]),[[12],[-7]]);
        byte[] gcd = [0x02,0x40,0x03,0x40,0x20,1,0x45,0x0d,1,
            0x20,0,0x20,1,0x70,0x21,2,0x20,1,0x21,0,0x20,2,0x21,1,0x0c,0,0x0b,0x0b,0x20,0,0x0b];
        var random = new Random(728);
        await Compare(Module(2,gcd,1), Enumerable.Range(0,200).Select(_=>new[]{random.Next(10000),random.Next(10000)}).ToArray());
        foreach(var body in new byte[][] { [0x0c,1,0x0b], [0x05,0x0b], [0x41,1,0x04,0x7f,0x41,2,0x0b,0x0b], [0x02,0x40,0x41,1,0x0b,0x41,0,0x0b] })
        {
            try { Transpiler.Translate(Module(0,body),"Invalid"); throw new Exception("Invalid control flow accepted."); }
            catch(WasmException) { }
        }
        Console.WriteLine("PASS: structured branches, branch values, branch tables, unreachable validation, and 200 GCD loops.");
    }
    internal static byte[] Functions(int[] parameters, byte[][] instructions, int[]? results = null)
    {
        results ??= Enumerable.Repeat(1,parameters.Length).ToArray();
        var types = new List<byte>(U32(parameters.Length));
        var code = new List<byte>(U32(parameters.Length));
        for(int i=0;i<parameters.Length;i++)
        {
            types.AddRange([0x60,..U32(parameters[i]),..Enumerable.Repeat((byte)0x7f,parameters[i]),(byte)results[i],..(results[i] == 1 ? new byte[]{0x7f} : Array.Empty<byte>())]);
            byte[] body = [0,..instructions[i]];
            code.AddRange([..U32(body.Length),..body]);
        }
        return [0,97,115,109,1,0,0,0,..Section(1,types.ToArray()),
            ..Section(3,[..U32(parameters.Length),..Enumerable.Range(0,parameters.Length).SelectMany(U32)]),
            ..Section(7,[1,1,(byte)'f',0,0]),..Section(10,code.ToArray())];
    }
    public static async Task Calls()
    {
        await Compare(Functions([2,2],[[0x20,0,0x20,1,0x10,1,0x0b],[0x20,0,0x20,1,0x6b,0x0b]]),[[1,2],[7,3],[int.MinValue,1]]);
        await Compare(Functions([0,1],[[0x41,7,0x10,1,0x41,42,0x0b],[0x20,0,0x1a,0x0b]],[1,0]),[[]]);
        await Compare(Functions([1],[[0x20,0,0x45,0x04,0x7f,0x41,1,0x05,0x20,0,0x20,0,0x41,1,0x6b,0x10,0,0x6c,0x0b,0x0b]]),
            Enumerable.Range(0,14).Select(n=>new[]{n}).ToArray());
        byte[] even = [0x20,0,0x45,0x04,0x7f,0x41,1,0x05,0x20,0,0x41,1,0x6b,0x10,1,0x0b,0x0b];
        byte[] odd = [0x20,0,0x45,0x04,0x7f,0x41,0,0x05,0x20,0,0x41,1,0x6b,0x10,0,0x0b,0x0b];
        await Compare(Functions([1,1],[even,odd]),Enumerable.Range(0,40).Select(n=>new[]{n}).ToArray());
        foreach(var bytes in new[] { Module(0,[0x10,1,0x0b]),Functions([0,1],[[0x10,1,0x0b],[0x20,0,0x0b]]) })
        {
            try { Transpiler.Translate(bytes,"Invalid"); throw new Exception("Invalid call accepted."); }
            catch(WasmException) { }
        }
        Console.WriteLine("PASS: private calls, argument order, void calls, factorial, mutual recursion, and invalid calls.");
    }
    internal static byte[] S32(int value)
    {
        var bytes = new List<byte>();
        while(true)
        {
            byte next = (byte)(value & 127); value >>= 7;
            bool last = (value == 0 && (next & 64) == 0) || (value == -1 && (next & 64) != 0);
            bytes.Add((byte)(next | (last ? 0 : 128)));
            if (last) return bytes.ToArray();
        }
    }
    internal static byte[] WithMemory(int parameters, byte[] instructions, byte[]? start = null, int dataOffset = 0)
    {
        byte[] signature = [0x60,..U32(parameters),..Enumerable.Repeat((byte)0x7f,parameters),1,0x7f];
        byte[] body = [0,..instructions];
        var code = new List<byte>([start == null ? (byte)1 : (byte)2,..U32(body.Length),..body]);
        if (start != null) code.AddRange([..U32(start.Length+1),0,..start]);
        return [0,97,115,109,1,0,0,0,
            ..Section(1,start == null ? [1,..signature] : [2,..signature,0x60,0,0]),
            ..Section(3,start == null ? [1,0] : [2,0,1]),
            ..Section(5,[1,1,1,2]), ..Section(6,[1,0x7f,1,0x41,5,0x0b]),
            ..Section(7,[3,1,(byte)'f',0,0,6,.."memory"u8.ToArray(),2,0,1,(byte)'g',3,0]),
            ..(start == null ? Array.Empty<byte>() : Section(8,[1])), ..Section(10,code.ToArray()),
            ..Section(11,[1,0,0x41,..S32(dataOffset),0x0b,4,7,8,9,10])];
    }
    public static async Task Memory()
    {
        foreach(var (load,store,alignment) in new (byte,byte,byte)[] { (0x28,0x36,2),(0x2c,0x3a,0),(0x2d,0x3a,0),(0x2e,0x3b,1),(0x2f,0x3b,1) })
        {
            var bytes = WithMemory(2,[0x20,0,0x20,1,store,alignment,0,0x20,0,load,alignment,0,0x0b]);
            await Compare(bytes,new[]{0,1,65532,65535,-1}.SelectMany(a=>new[]{-1,int.MinValue,int.MaxValue}.Select(v=>new[]{a,v})).ToArray());
        }
        await Compare(WithMemory(1,[0x20,0,0x28,2,0xff,0xff,0xff,0xff,0x0f,0x0b]),[[0],[-1]]);
        await Compare(WithMemory(1,[0x20,0,0x40,0,0x0b]),[[0],[1],[1],[-1],[0]]);
        await Compare(WithMemory(0,[0x3f,0,0x0b]),[[]]);
        byte[] initialized = WithMemory(0,[0x23,0,0x0b],[0x41,0,0x41,9,0x3a,0,0,0x23,0,0x41,1,0x6a,0x24,0,0x0b]);
        await Compare(initialized,[[],[]]);
        await Compare(WithMemory(0,[0x41,0,0x0b],dataOffset:65534),[[]]);
        var type = Compile(initialized).GetType("Wasm2Cs.Generated.Subject")!;
        var first = Activator.CreateInstance(type); var second = Activator.CreateInstance(type);
        type.GetMethod("WriteMemory")!.Invoke(first,[0u,new byte[]{55}]);
        type.GetProperty("g")!.SetValue(first,100);
        var unchanged = (byte[])type.GetMethod("ReadMemory")!.Invoke(second,[0u,1])!;
        if (unchanged[0] != 9 || (int)type.GetProperty("g")!.GetValue(second)! != 6) throw new Exception("Instances share state.");
        unchanged[0] = 99;
        if (((byte[])type.GetMethod("ReadMemory")!.Invoke(second,[0u,1])!)[0] != 9) throw new Exception("ReadMemory leaked backing storage.");
        foreach(var invalid in new[] { Module(0,[0x3f,0,0x0b]),WithMemory(0,[0x41,0,0x28,3,0,0x0b]),WithMemory(0,[0x23,1,0x0b]) })
        {
            try { Transpiler.Translate(invalid,"Invalid"); throw new Exception("Invalid memory/global instruction accepted."); }
            catch(WasmException) { }
        }
        Console.WriteLine("PASS: memory loads/stores, unsigned bounds, grow, data/start initialization, globals, and isolated instances.");
    }
    private static byte[] Name(string value) => [..U32(System.Text.Encoding.UTF8.GetByteCount(value)), ..System.Text.Encoding.UTF8.GetBytes(value)];
    private static byte[] SharedMemoryModule()
    {
        byte[] types = [1,0x60,0,1,0x7f];
        byte[] imports = [2,..Name("env"),..Name("memory"),2,1,1,2,..Name("env"),..Name("counter"),3,0x7f,1];
        byte[] body = [0,0x23,0,0x41,1,0x6a,0x24,0,0x41,0,0x2d,0,0,0x0b];
        return [0,97,115,109,1,0,0,0,..Section(1,types),..Section(2,imports),..Section(3,[1,0]),
            ..Section(7,[3,1,(byte)'f',0,0,6,.."memory"u8.ToArray(),2,0,7,.."counter"u8.ToArray(),3,0]),
            ..Section(10,[1,..U32(body.Length),..body])];
    }
    private static byte[] ImportedGlobalInitializer()
    {
        byte[] imports = [1,..Name("env"),..Name("offset"),3,0x7f,0];
        byte[] globals = [1,0x7f,0,0x23,0,0x0b];
        byte[] exports = [2,1,(byte)'g',3,1,6,.."memory"u8.ToArray(),2,0];
        byte[] data = [1,2,0,0x23,0,0x0b,1,99];
        return [0,97,115,109,1,0,0,0,..Section(2,imports),..Section(5,[1,1,1,1]),..Section(6,globals),
            ..Section(7,exports),..Section(11,data)];
    }
    private static byte[] PassiveBulkModule()
    {
        byte[] types = [1,0x60,0,1,0x7f];
        byte[] init = [0,0x41,0,0x41,0,0x41,3,0xfc,8,0,0,0x41,0,0x2d,0,0,0x0b];
        byte[] drop = [0,0x41,0,0x41,0,0x41,1,0xfc,8,0,0,0xfc,9,0,0x41,0,0x41,0,0x41,1,0xfc,8,0,0,0x41,0,0x2d,0,0,0x0b];
        byte[] code = [2,..U32(init.Length),..init,..U32(drop.Length),..drop];
        byte[] exports = [2,1,(byte)'f',0,0,4,(byte)'d',(byte)'r',(byte)'o',(byte)'p',0,1];
        byte[] data = [1,1,3,65,66,67];
        return [0,97,115,109,1,0,0,0,..Section(1,types),..Section(3,[2,0,0]),..Section(5,[1,0,1]),
            ..Section(7,exports),..Section(12,[1]),..Section(10,code),..Section(11,data)];
    }
    private static byte[] CopyFillModule()
    {
        byte[] types = [1,0x60,0,1,0x7f];
        byte[] copy = [0,0x41,1,0x41,0,0x41,3,0xfc,10,0,0,0x41,1,0x2d,0,0,0x0b];
        byte[] fill = [0,0x41,0,0x41,0x7f,0x41,2,0xfc,11,0,0x41,1,0x2d,0,0,0x0b];
        byte[] code = [2,..U32(copy.Length),..copy,..U32(fill.Length),..fill];
        byte[] exports = [2,1,(byte)'c',0,0,1,(byte)'f',0,1];
        byte[] data = [1,0,0x41,0,0x0b,4,1,2,3,4];
        return [0,97,115,109,1,0,0,0,..Section(1,types),..Section(3,[2,0,0]),..Section(5,[1,0,1]),
            ..Section(7,exports),..Section(10,code),..Section(11,data)];
    }
    public static void SharedMemoryGlobalsAndBulkData()
    {
        var bytes = SharedMemoryModule();
        File.WriteAllText("/tmp/shared.g.cs", Transpiler.Translate(bytes,"SharedA"));
        var firstType = Compile(bytes,"SharedA").GetType("Wasm2Cs.Generated.SharedA")!;
        var secondType = Compile(bytes,"SharedB").GetType("Wasm2Cs.Generated.SharedB")!;
        var memory = new WasmMemory(1,2);
        memory.WriteMemory(0,new byte[] { 7 });
        var global = new WasmGlobal(WasmValueType.I32,10,true);
        var first = Activator.CreateInstance(firstType,[memory,global])!;
        var second = Activator.CreateInstance(secondType,[memory,global])!;
        if ((int)firstType.GetMethod("f")!.Invoke(first,null)! != 7 || (int)global.Value! != 11)
            throw new Exception("Imported memory/global state was not shared.");
        if ((int)secondType.GetMethod("f")!.Invoke(second,null)! != 7 || (int)global.Value! != 12)
            throw new Exception("Imported memory/global state was not visible to the second module.");
        if (!ReferenceEquals(firstType.GetProperty("memory")!.GetValue(first), memory) || memory.Grow(1) != 1 ||
            (int)firstType.GetProperty("MemorySize")!.GetValue(first)! != 131072 ||
            (int)secondType.GetProperty("MemorySize")!.GetValue(second)! != 131072)
            throw new Exception("Shared memory object or post-grow visibility differs.");
        var buffer = new byte[3];
        firstType.GetMethod("ReadMemoryInto")!.Invoke(first,[0u,buffer,1,1]);
        if (buffer[1] != 7) throw new Exception("Range-based memory read failed.");
        firstType.GetMethod("WriteMemoryFrom")!.Invoke(first,[1u,new byte[] { 8, 9 },1,1]);
        if (memory.ReadMemory(1,1)[0] != 9) throw new Exception("Range-based memory write failed.");
        try { firstType.GetMethod("ReadMemoryInto")!.Invoke(first,[0u,buffer,3,1]); throw new Exception("Accepted an invalid host buffer range."); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { }
        if (memory.Grow(1) != -1 || memory.CurrentPages != 2) throw new Exception("Failed grow changed memory state.");
        try { Activator.CreateInstance(firstType,[new WasmMemory(1,3),global]); throw new Exception("Accepted an incompatible imported memory maximum."); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }

        var initializerType = Compile(ImportedGlobalInitializer(),"GlobalInit").GetType("Wasm2Cs.Generated.GlobalInit")!;
        var initializer = Activator.CreateInstance(initializerType,[new WasmGlobal(WasmValueType.I32,4)])!;
        if ((int)initializerType.GetProperty("g")!.GetValue(initializer)! != 4 ||
            ((byte[])initializerType.GetMethod("ReadMemory")!.Invoke(initializer,[4u,1])!)[0] != 99)
            throw new Exception("Imported immutable global was not visible to initialization.");

        var bulkType = Compile(PassiveBulkModule(),"PassiveBulk").GetType("Wasm2Cs.Generated.PassiveBulk")!;
        var bulk = Activator.CreateInstance(bulkType)!;
        if ((int)bulkType.GetMethod("f")!.Invoke(bulk,null)! != 65) throw new Exception("memory.init failed.");
        try { bulkType.GetMethod("drop")!.Invoke(bulk,null); throw new Exception("memory.init after data.drop did not trap."); }
        catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "TrapException") { }

        File.WriteAllText("/tmp/copyfill.g.cs", Transpiler.Translate(CopyFillModule(),"CopyFill"));
        var copyFillType = Compile(CopyFillModule(),"CopyFill").GetType("Wasm2Cs.Generated.CopyFill")!;
        var copyFill = Activator.CreateInstance(copyFillType)!;
        if ((int)copyFillType.GetMethod("c")!.Invoke(copyFill,null)! != 1 ||
            (int)copyFillType.GetMethod("f")!.Invoke(copyFill,null)! != 255)
            throw new Exception("memory.copy or memory.fill failed.");
        Console.WriteLine("PASS: imported/shared memory and globals, dynamic initialization, bulk data, overlap copy, fill, and data.drop.");
    }
    public static void TargetProfiles()
    {
        byte[] bytes = WithMemory(2,[0x20,0,0x20,1,0x36,2,0,0x20,0,0x28,2,0,0x0b]);
        string portable = Transpiler.Translate(bytes,"Portable",WasmTargetProfile.PortableNetStandard20);
        string net21 = Transpiler.Translate(bytes,"Net21",WasmTargetProfile.DotNetNetStandard21);
        string vector = Transpiler.Translate(bytes,"Vector",WasmTargetProfile.DotNetVector);
        string unity = Transpiler.Translate(bytes,"Unity",WasmTargetProfile.UnityMathematics);
        if (!portable.Contains("portable-netstandard2.0") || portable.Contains("ReadUInt32"))
            throw new Exception("Portable profile emitted target-specific memory code.");
        if (!net21.Contains("dotnet-netstandard2.1") || !net21.Contains("ReadUInt32") ||
            !vector.Contains("dotnet-vector") || !vector.Contains("ReadUInt32") ||
            !unity.Contains("unity-mathematics") || unity.Contains("ReadUInt32"))
            throw new Exception("Target profile did not select its memory backend.");
        if (portable.Contains("System.Runtime.Intrinsics") || portable.Contains("Unity.Mathematics") ||
            net21.Contains("System.Runtime.Intrinsics") || net21.Contains("Unity.Mathematics"))
            throw new Exception("A non-vector profile leaked a target-specific vector assembly.");
        string floatNet21 = Transpiler.Translate(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Floating", "Floating.wasm")),
            "FloatNet21", WasmTargetProfile.DotNetNetStandard21);
        string floatPortable = Transpiler.Translate(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Floating", "Floating.wasm")),
            "FloatPortable", WasmTargetProfile.PortableNetStandard20);
        string floatUnity = Transpiler.Translate(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Floating", "Floating.wasm")),
            "FloatUnity", WasmTargetProfile.UnityMathematics);
        if (!floatNet21.Contains("MathF.Sqrt") || !floatNet21.Contains("MathF.Round") ||
            !floatNet21.Contains("__wasm_Sqrt32") || !floatNet21.Contains("__wasm_Round32"))
            throw new Exception(".NET Standard 2.1 backend did not select the guarded MathF f32 backend.");
        if (floatPortable.Contains("MathF") || floatUnity.Contains("MathF"))
            throw new Exception("Target-specific MathF helpers leaked into a portable backend.");

        var vectorPlan = Lowering.Create(Decoder.Decode(bytes), WasmTargetProfile.DotNetVector);
        if (!vectorPlan.TryLowerVector(CanonicalVectorOperation.AddF32x4, "left", "right", out var dotnetVector) ||
            dotnetVector is null || !dotnetVector.TypeName.Contains("Vector128<float>") ||
            !dotnetVector.Expression.Contains("Vector128.Create") || !dotnetVector.Expression.Contains("GetElement(left, 3)"))
            throw new Exception(".NET vector backend did not lower the canonical f32x4 add operation.");
        CompileVectorExpression(dotnetVector);
        var unityPlan = Lowering.Create(Decoder.Decode(bytes), WasmTargetProfile.UnityMathematics);
        if (!unityPlan.TryLowerVector(CanonicalVectorOperation.MultiplyF32x4, "left", "right", out var unityVector) ||
            unityVector is null || unityVector.TypeName != "global::Unity.Mathematics.float4" ||
            unityVector.Expression != "left * right")
            throw new Exception("Unity backend did not lower the canonical f32x4 multiply operation.");
        if (!vectorPlan.TryLowerVector(CanonicalVectorOperation.AddI32x4, "left", "right", out var addI32) || addI32 is null ||
            !addI32.Expression.Contains("AsInt32") ||
            !vectorPlan.TryLowerVector(CanonicalVectorOperation.AndV128, "left", "right", out var andV) || andV is null ||
            !vectorPlan.TryLowerVectorUnary(CanonicalVectorOperation.ExtendLowI8x16U, "value", out var extend) || extend is null ||
            !vectorPlan.TryLowerVectorSplat(CanonicalVectorOperation.SplatI32x4, "scalar", out var splat) || splat is null ||
            !vectorPlan.TryLowerVectorExtract(CanonicalVectorOperation.ExtractLaneI32x4, "vector", 2, out var extract) || extract is null ||
            !vectorPlan.TryLowerVectorShuffle("a", "b", new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, out var shuffle) || shuffle is null ||
            !vectorPlan.TryLowerVectorLoad(false, "addr", 0, out var load) || load is null ||
            !vectorPlan.TryLowerVectorStore("addr", "value", 0, out var store) || store is null)
            throw new Exception(".NET vector backend did not lower the T05 SIMD group.");
        if (!unityPlan.TryLowerVector(CanonicalVectorOperation.AddI32x4, "left", "right", out var unityAddI32) || unityAddI32 is null ||
            !unityPlan.TryLowerVectorTernary(CanonicalVectorOperation.BitselectV128, "a", "b", "c", out var unityBit) || unityBit is null)
            throw new Exception("Unity backend did not lower the T05 SIMD group.");
        var scalarPlan = Lowering.Create(Decoder.Decode(bytes), WasmTargetProfile.PortableNetStandard20);
        if (scalarPlan.TryLowerVector(CanonicalVectorOperation.AddF32x4, "left", "right", out _) ||
            scalarPlan.TryLowerVector(CanonicalVectorOperation.AddI32x4, "left", "right", out _) ||
            scalarPlan.TryLowerVectorLoad(false, "addr", 0, out _))
            throw new Exception("Portable backend unexpectedly accepted a vector lowering.");
        if (!CanonicalOperations.TryCreate(0x6c, 0, out var canonicalMultiply) || canonicalMultiply is null ||
            canonicalMultiply.Kind != CanonicalOperationKind.I32 || canonicalMultiply.Inputs.Length != 2 ||
            canonicalMultiply.Result != Wasm2Cs.ValueType.I32)
            throw new Exception("Scalar multiplication did not enter canonical lowering.");
        if (!CanonicalOperations.TryCreate(0xfc, 4, out var canonicalTruncation) || canonicalTruncation is null ||
            canonicalTruncation.Kind != CanonicalOperationKind.Conversion || canonicalTruncation.Inputs.Single() != Wasm2Cs.ValueType.F32 ||
            canonicalTruncation.Result != Wasm2Cs.ValueType.I64)
            throw new Exception("Conversion did not enter canonical lowering.");

        foreach (var (opcode, api) in new (byte,string)[] {
            (0x67,"LeadingZeroCount"), (0x68,"TrailingZeroCount"), (0x69,"PopCount") })
        {
            byte[] i32BitModule = Module(1,[0x20,0,opcode,0x0b]);
            string vectorBitSource = Transpiler.Translate(i32BitModule,"VectorBits",WasmTargetProfile.DotNetVector);
            if (!vectorBitSource.Contains($"System.Numerics.BitOperations.{api}"))
                throw new Exception(".NET vector profile did not select the i32 BCL bit operation: " + api);
            var vectorBitType = Compile(i32BitModule,"VectorBits",false,WasmTargetProfile.DotNetVector)
                .GetType("Wasm2Cs.Generated.VectorBits")!;
            int expected = opcode == 0x69 ? 0 : 32;
            if ((int)vectorBitType.GetMethod("f")!.Invoke(Activator.CreateInstance(vectorBitType),[0])! != expected)
                throw new Exception(".NET BCL bit operation changed the i32 result: " + api);
        }
        byte[] i64BitModule = I64Unary(0x7b);
        string vectorBit64Source = Transpiler.Translate(i64BitModule,"VectorBits64",WasmTargetProfile.DotNetVector);
        if (!vectorBit64Source.Contains("System.Numerics.BitOperations.PopCount"))
            throw new Exception(".NET vector profile did not select the i64 BCL bit operation.");
        var vectorBit64Type = Compile(i64BitModule,"VectorBits64",false,WasmTargetProfile.DotNetVector)
            .GetType("Wasm2Cs.Generated.VectorBits64")!;
        if ((long)vectorBit64Type.GetMethod("f")!.Invoke(Activator.CreateInstance(vectorBit64Type),[-1L])! != 64)
            throw new Exception(".NET BCL population count changed the i64 result.");

        byte[] vectorBytes = VectorModule();
        var decodedVector = Decoder.Decode(vectorBytes);
        Validator.Validate(decodedVector, "CanonicalVector");
        var canonicalVector = CanonicalLowering.Lower(decodedVector);
        if (canonicalVector.Source != decodedVector || canonicalVector.Bodies.Count != 1 ||
            canonicalVector.Bodies[0].Instructions.Count != decodedVector.Bodies[0].Instructions.Count)
            throw new Exception("Canonical module did not preserve the validated function graph.");
        if (!canonicalVector.Bodies[0].Instructions.Any(i => i.VectorConstant?.Bytes.Length == 16) ||
            !canonicalVector.Bodies[0].Instructions.Any(i => i.VectorOperation == CanonicalVectorOperation.AddF32x4) ||
            !canonicalVector.Bodies[0].Instructions.Any(i => i.VectorOperation == CanonicalVectorOperation.MultiplyF32x4))
            throw new Exception("Canonical vector lowering lost a typed vector instruction.");
        var decodedScalar = Decoder.Decode(Module(2,[0x20,0,0x20,1,0x6a,0x0b]));
        Validator.Validate(decodedScalar, "CanonicalScalar");
        var canonicalScalar = CanonicalLowering.Lower(decodedScalar);
        var scalarAdd = canonicalScalar.Bodies[0].Instructions.Single(i => i.Source.Opcode == 0x6a).Operation;
        if (scalarAdd is null || scalarAdd.Kind != CanonicalOperationKind.I32 || scalarAdd.Inputs.Length != 2 ||
            scalarAdd.Result != Wasm2Cs.ValueType.I32)
            throw new Exception("Canonical scalar lowering lost operation type metadata.");
        var decodedMemory = Decoder.Decode(bytes);
        Validator.Validate(decodedMemory, "CanonicalMemory");
        var canonicalMemory = CanonicalLowering.Lower(decodedMemory);
        if (!canonicalMemory.Bodies[0].Instructions.Any(i => i.Kind == CanonicalInstructionKind.Memory))
            throw new Exception("Canonical lowering did not classify memory instructions.");
        string vectorSource = Transpiler.Translate(vectorBytes,"Vector",WasmTargetProfile.DotNetVector);
        if (!vectorSource.Contains("Vector128<float>") || !vectorSource.Contains("BitConverter.Int32BitsToSingle"))
            throw new Exception(".NET vector backend did not emit a v128 function.");
        var vectorType = Compile(vectorBytes,"Vector",false,WasmTargetProfile.DotNetVector)
            .GetType("Wasm2Cs.Generated.Vector")!;
        var vectorResult = (Vector128<float>)vectorType.GetMethod("f")!.Invoke(Activator.CreateInstance(vectorType),null)!;
        if (Vector128.GetElement(vectorResult,0) != 22f || Vector128.GetElement(vectorResult,1) != 44f ||
            Vector128.GetElement(vectorResult,2) != 66f || Vector128.GetElement(vectorResult,3) != 88f)
            throw new Exception(".NET vector backend changed f32x4 results.");
        string unityVectorSource = Transpiler.Translate(vectorBytes,"UnityVector",WasmTargetProfile.UnityMathematics);
        if (!unityVectorSource.Contains("Unity.Mathematics.float4") || !unityVectorSource.Contains("math.asfloat"))
            throw new Exception("Unity backend did not emit the canonical v128 fixture.");
        if (vectorSource.Contains("Unity.Mathematics") || vectorSource.Contains("BitOperations") ||
            unityVectorSource.Contains("System.Runtime.Intrinsics") || unityVectorSource.Contains("Vector128"))
            throw new Exception("A selected vector backend leaked another target's type or assembly.");
        try { Transpiler.Translate(vectorBytes,"PortableVector",WasmTargetProfile.PortableNetStandard20); throw new Exception("Portable profile accepted v128."); }
        catch (WasmException) { }

        var type = Compile(bytes,"Net21",false,WasmTargetProfile.DotNetNetStandard21)
            .GetType("Wasm2Cs.Generated.Net21")!;
        var instance = Activator.CreateInstance(type)!;
        if ((int)type.GetMethod("f")!.Invoke(instance,[0,0x11223344])! != 0x11223344)
            throw new Exception("netstandard2.1 memory lowering changed the result.");

        var memory = new WasmMemory(1);
        memory.WriteUInt16(0,0x1234);
        memory.WriteUInt32(2,0x89abcdef);
        memory.WriteUInt64(6,0x0123456789abcdefUL);
        if (memory.ReadUInt16(0) != 0x1234 || memory.ReadUInt32(2) != 0x89abcdef ||
            memory.ReadUInt64(6) != 0x0123456789abcdefUL)
            throw new Exception("Span/MemoryMarshal memory access did not preserve little-endian bits.");
        Console.WriteLine("PASS: explicit target profiles, netstandard2.1 Span/MemoryMarshal memory lowering, and bit-preserving typed access.");
    }
    private static void CompileVectorExpression(VectorLowering lowering)
    {
        string source = $"public static class VectorFixture {{ public static {lowering.TypeName} Add({lowering.TypeName} left, {lowering.TypeName} right) => {lowering.Expression}; }}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("VectorFixture_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception("Vector128 lowering did not compile: " + string.Join("\n", errors));
    }
    public static async Task CAlgorithms()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Algorithms.wasm"));
        var calls = new[] {0,1,-1,int.MinValue,int.MaxValue}.SelectMany(seed=>new[]{0,1,8,64,255,256,300}.Select(length=>new[]{seed,length})).ToArray();
        await Compare(bytes,calls);
        var type = Compile(bytes).GetType("Wasm2Cs.Generated.Subject")!;
        var instance = Activator.CreateInstance(type);
        int address = (int)type.GetMethod("buffer_ptr")!.Invoke(instance,null)!;
        type.GetMethod("WriteMemory")!.Invoke(instance,[unchecked((uint)address),System.Text.Encoding.ASCII.GetBytes("123456789")]);
        int crc = (int)type.GetMethod("crc32")!.Invoke(instance,[address,9])!;
        int sum = (int)type.GetMethod("sum_bytes")!.Invoke(instance,[address,9])!;
        if (unchecked((uint)crc) != 0xcbf43926u || sum != 477) throw new Exception("C algorithm known-answer test failed.");
        Console.WriteLine("PASS: Clang-produced CRC32/array algorithms, known answers, and full-memory differential checks.");
    }
    public static async Task CWorkload()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "CWorkload");
        string callsPath = Path.Combine(directory, "calls.json");
        var calls = JsonSerializer.Deserialize<JsonElement[][]>(File.ReadAllText(callsPath))!;
        foreach (string name in new[] { "Workload", "WorkloadFeatures" })
        {
            string wasmPath = Path.Combine(directory, name + ".wasm");
            var type = Compile(File.ReadAllBytes(wasmPath), name).GetType("Wasm2Cs.Generated." + name)!;
            var instance = Activator.CreateInstance(type)!;
            var actual = new List<string>();
            foreach (var call in calls)
            {
                string export = call[0].GetString()!;
                object[] arguments = call[1].EnumerateArray().Select(value => (object)value.GetInt32()).ToArray();
                string head = $"{export}({string.Join(",", arguments)})";
                try { actual.Add($"{head} = {type.GetMethod(export)!.Invoke(instance, arguments)}"); }
                catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
                { actual.Add($"{head} trap"); }
            }
            var memory = (WasmMemory)type.GetProperty("memory")!.GetValue(instance)!;
            actual.Add("memory " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ReadMemory(0, (int)memory.Size))).ToLowerInvariant());

            var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sequence-oracle.mjs"));
            start.ArgumentList.Add(wasmPath);
            start.ArgumentList.Add(callsPath);
            using var node = Process.Start(start)!;
            var output = node.StandardOutput.ReadToEndAsync();
            var error = node.StandardError.ReadToEndAsync();
            await node.WaitForExitAsync();
            if (node.ExitCode != 0) throw new Exception(await error);
            string[] expected = (await output).TrimEnd('\n').Split('\n');
            if (expected.Length != actual.Count) throw new Exception(name + ": oracle line count differs.");
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) throw new Exception($"{name}: WASM/C# mismatch: {actual[i]} != {expected[i]}");
        }
        Console.WriteLine($"PASS: Clang -O2 workload (default and bulk-memory/sign-ext/nontrapping-fptoint builds) matched Node over {calls.Length} calls and final memory.");
    }
    public static async Task CppWorkload()
    {
        // T02 C++ input: wasi-sdk-24.0 clang++ -O2 reactor library (libc++ std::sort, virtual dispatch, new/delete, static ctor).
        string directory = Path.Combine(AppContext.BaseDirectory, "CppWorkload");
        string callsPath = Path.Combine(directory, "calls.json");
        string wasmPath = Path.Combine(directory, "CppWorkload.wasm");
        var calls = JsonSerializer.Deserialize<JsonElement[][]>(File.ReadAllText(callsPath))!;
        var type = Compile(File.ReadAllBytes(wasmPath), "CppWorkload").GetType("Wasm2Cs.Generated.CppWorkload")!;
        var instance = Activator.CreateInstance(type)!;
        var actual = new List<string>();
        var trapKinds = new List<string>();
        foreach (var call in calls)
        {
            string export = call[0].GetString()!;
            object[] arguments = call[1].EnumerateArray().Select(value => (object)value.GetInt32()).ToArray();
            string head = $"{export}({string.Join(",", arguments)})";
            try { actual.Add($"{head} = {type.GetMethod(export)!.Invoke(instance, arguments) ?? "undefined"}"); }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
            {
                actual.Add($"{head} trap");
                trapKinds.Add($"{head} {e.InnerException.GetType().GetProperty("Kind")!.GetValue(e.InnerException)}");
            }
        }
        // Node reports only RuntimeError; these kinds match wasmtime 28.0.1's trap messages for the same calls.
        string[] expectedKinds = ["checked_at(4096) Unreachable", "checked_at(-1) Unreachable", "divide(-2147483648,-1) IntegerOverflow", "divide(1,0) DivisionByZero"];
        if (!trapKinds.SequenceEqual(expectedKinds)) throw new Exception("CppWorkload: trap kinds differ: " + string.Join("; ", trapKinds));
        var memory = (WasmMemory)type.GetProperty("memory")!.GetValue(instance)!;
        actual.Add("memory " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ReadMemory(0, (int)memory.Size))).ToLowerInvariant());

        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sequence-oracle.mjs"));
        start.ArgumentList.Add(wasmPath);
        start.ArgumentList.Add(callsPath);
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync();
        if (node.ExitCode != 0) throw new Exception(await error);
        string[] expected = (await output).TrimEnd('\n').Split('\n');
        if (expected.Length != actual.Count) throw new Exception("CppWorkload: oracle line count differs.");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) throw new Exception($"CppWorkload: WASM/C# mismatch: {actual[i]} != {expected[i]}");
        Console.WriteLine($"PASS: C++ -O2 workload (libc++ sort, virtual calls, new/delete, static ctor) matched Node over {calls.Length} calls and final memory.");
    }
    public static async Task CSimd()
    {
        // T05: Clang -O3 -msimd128 autovectorized fixture (docs/t04-simd-selection.md).
        string directory = Path.Combine(AppContext.BaseDirectory, "CSimd");
        string callsPath = Path.Combine(directory, "calls.json");
        string wasmPath = Path.Combine(directory, "Simd.wasm");
        byte[] wasm = File.ReadAllBytes(wasmPath);
        var calls = JsonSerializer.Deserialize<JsonElement[][]>(File.ReadAllText(callsPath))!;

        foreach (var profile in new[] { WasmTargetProfile.PortableNetStandard20, WasmTargetProfile.DotNetNetStandard21 })
        {
            try { Transpiler.Translate(wasm, "SimdRejected", profile); throw new Exception(profile + " accepted Simd.wasm."); }
            catch (WasmException) { }
        }

        string unitySource = Transpiler.Translate(wasm, "SimdUnity", WasmTargetProfile.UnityMathematics);
        if (!unitySource.Contains("Unity.Mathematics.float4") || unitySource.Contains("Vector128"))
            throw new Exception("Unity SIMD lowering leaked Vector128 or lost float4.");

        var type = Compile(wasm, "Simd", false, WasmTargetProfile.DotNetVector).GetType("Wasm2Cs.Generated.Simd")!;
        var instance = Activator.CreateInstance(type)!;
        var actual = new List<string>();
        foreach (var call in calls)
        {
            string export = call[0].GetString()!;
            object[] arguments = call[1].EnumerateArray().Select(value => (object)value.GetInt32()).ToArray();
            string head = $"{export}({string.Join(",", arguments)})";
            try { actual.Add($"{head} = {type.GetMethod(export)!.Invoke(instance, arguments)}"); }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
            { actual.Add($"{head} trap"); }
        }
        var memory = (WasmMemory)type.GetProperty("memory")!.GetValue(instance)!;
        actual.Add("memory " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ReadMemory(0, (int)memory.Size))).ToLowerInvariant());

        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sequence-oracle.mjs"));
        start.ArgumentList.Add(wasmPath);
        start.ArgumentList.Add(callsPath);
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync();
        if (node.ExitCode != 0) throw new Exception(await error);
        string[] expected = (await output).TrimEnd('\n').Split('\n');
        if (expected.Length != actual.Count) throw new Exception("CSimd: oracle line count differs.");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) throw new Exception($"CSimd: WASM/C# mismatch: {actual[i]} != {expected[i]}");

        // Memory bounds: v128.load / v128.store need 16 bytes; load32_zero needs 4.
        byte[] loadStore = V128MemoryModule();
        var memType = Compile(loadStore, "SimdMem", false, WasmTargetProfile.DotNetVector).GetType("Wasm2Cs.Generated.SimdMem")!;
        var memInstance = Activator.CreateInstance(memType)!;
        if ((int)memType.GetMethod("roundtrip")!.Invoke(memInstance, [0])! != unchecked((int)0x04030201))
            throw new Exception("v128 load/store round-trip failed.");
        if ((int)memType.GetMethod("load32")!.Invoke(memInstance, [0])! != unchecked((int)0x04030201))
            throw new Exception("v128.load32_zero failed.");
        foreach (var (export, arg) in new[] { ("roundtrip", 65536 - 15), ("load32", 65536 - 3) })
        {
            try
            {
                memType.GetMethod(export)!.Invoke(memInstance, [arg]);
                throw new Exception(export + " missing OOB trap.");
            }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == memType &&
                e.InnerException.GetType().Name == "TrapException" &&
                e.InnerException.GetType().GetProperty("Kind")!.GetValue(e.InnerException)!.ToString() == "MemoryOutOfBounds")
            { }
        }
        foreach (var profile in new[] { WasmTargetProfile.PortableNetStandard20, WasmTargetProfile.DotNetNetStandard21 })
        {
            try { Transpiler.Translate(loadStore, "SimdMemRejected", profile); throw new Exception(profile + " accepted SIMD memory module."); }
            catch (WasmException) { }
        }

        // Reject invalid immediates.
        foreach (byte[] invalid in new[] { InvalidShuffleModule(), InvalidExtractModule(22, 16), InvalidExtractModule(27, 4) })
        {
            try { Transpiler.Translate(invalid, "SimdInvalid", WasmTargetProfile.DotNetVector); throw new Exception("Accepted invalid SIMD immediate."); }
            catch (WasmException) { }
        }

        Console.WriteLine($"PASS: Clang -O3 -msimd128 Simd.wasm matched Node over {calls.Length} calls and final memory on dotnet-vector; portable/netstandard2.1 reject; Unity translates (Editor/IL2CPP unverified).");
    }
    private static byte[] V128MemoryModule()
    {
        // memory 1 page; roundtrip(addr): store const bytes then load and extract lane 0 as i32
        // load32(addr): load32_zero and extract lane 0
        byte[] roundtripBody = [0, // locals
            0x20, 0, // local.get 0
            0xfd, 0x0c, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, // v128.const
            0xfd, 0x0b, 4, 0, // v128.store align=4 offset=0
            0x20, 0,
            0xfd, 0x00, 4, 0, // v128.load
            0xfd, 0x1b, 0, // i32x4.extract_lane 0
            0x0b];
        byte[] load32Body = [0,
            0x20, 0,
            0xfd, 0x0c, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            0xfd, 0x0b, 4, 0,
            0x20, 0,
            0xfd, 0x5c, 2, 0, // v128.load32_zero
            0xfd, 0x1b, 0,
            0x0b];
        byte[] roundtrip = [..U32(roundtripBody.Length), ..roundtripBody];
        byte[] load32 = [..U32(load32Body.Length), ..load32Body];
        return [0, 97, 115, 109, 1, 0, 0, 0,
            ..Section(1, [1, 0x60, 1, 0x7f, 1, 0x7f]),
            ..Section(3, [2, 0, 0]),
            ..Section(5, [1, 0, 1]),
            ..Section(7, [2, 9, (byte)'r',(byte)'o',(byte)'u',(byte)'n',(byte)'d',(byte)'t',(byte)'r',(byte)'i',(byte)'p', 0, 0,
                6, (byte)'l',(byte)'o',(byte)'a',(byte)'d',(byte)'3',(byte)'2', 0, 1]),
            ..Section(10, [2, ..roundtrip, ..load32])];
    }
    private static byte[] InvalidShuffleModule()
    {
        byte[] lanes = Enumerable.Repeat((byte)32, 16).ToArray();
        byte[] body = [0, 0xfd, 0x0c, ..Enumerable.Repeat((byte)0, 16), 0xfd, 0x0c, ..Enumerable.Repeat((byte)0, 16), 0xfd, 0x0d, ..lanes, 0x1a, 0x0b];
        return [0, 97, 115, 109, 1, 0, 0, 0,
            ..Section(1, [1, 0x60, 0, 0]), ..Section(3, [1, 0]),
            ..Section(7, [1, 1, (byte)'f', 0, 0]), ..Section(10, [1, ..U32(body.Length), ..body])];
    }
    private static byte[] InvalidExtractModule(int opcode, int lane)
    {
        byte[] body = [0, 0xfd, 0x0c, ..Enumerable.Repeat((byte)0, 16), 0xfd, ..U32(opcode), (byte)lane, 0x1a, 0x0b];
        return [0, 97, 115, 109, 1, 0, 0, 0,
            ..Section(1, [1, 0x60, 0, 0]), ..Section(3, [1, 0]),
            ..Section(7, [1, 1, (byte)'f', 0, 0]), ..Section(10, [1, ..U32(body.Length), ..body])];
    }
    public static void MemoryDuringStart()
    {
        byte[] peekType = [0x60, 2, 0x7f, 0x7f, 0];
        byte[] emptyType = [0x60, 0, 0];
        byte[] types = [2, ..peekType, ..emptyType];
        byte[] env = [3, (byte)'e', (byte)'n', (byte)'v'];
        byte[] memoryName = [6, .."memory"u8.ToArray()];
        byte[] peekName = [4, .."peek"u8.ToArray()];
        byte[] memoryImport = [..env, ..memoryName, 2, 0, 1];
        byte[] peekImport = [..env, ..peekName, 0, 0];
        // start loads the byte at address 8 and passes (loaded, 4) to env.peek.
        byte[] body = [0, 0x41, 8, 0x2d, 0, 0, 0x41, 4, 0x10, 0, 0x0b];
        byte[] data = [1, 0, 0x41, 8, 0x0b, 4, 0x11, 0x22, 0x33, 0x44];
        byte[] code = [1, ..U32(body.Length), ..body];
        byte[] imported = [0, 97, 115, 109, 1, 0, 0, 0,
            ..Section(1, types),
            ..Section(2, [2, ..memoryImport, ..peekImport]),
            ..Section(3, [1, 1]),
            ..Section(7, [1, ..memoryName, 2, 0]),
            ..Section(8, [1]),
            ..Section(10, code),
            ..Section(11, data)];
        var importedType = Compile(imported, "ImportedStart").GetType("Wasm2Cs.Generated.ImportedStart")!;
        var memory = new WasmMemory(1);
        int importedValue = -1, importedLength = -1;
        bool importedDuringConstruction = false;
        Action<int, int> importedPeek = (value, length) =>
        {
            importedDuringConstruction = true;
            importedValue = value;
            importedLength = length;
            var bytes = memory.ReadMemory(8, 4);
            if (bytes[0] != 0x11 || bytes[1] != 0x22 || bytes[2] != 0x33 || bytes[3] != 0x44)
                throw new Exception("Imported memory did not contain the active data segment during start.");
        };
        var importedDelegate = Delegate.CreateDelegate(importedType.GetNestedType("__wasm_Import0")!, importedPeek.Target, importedPeek.Method);
        var importedCtor = importedType.GetConstructors().Single(ctor => ctor.GetParameters().Length == 2);
        var importedArgs = importedCtor.GetParameters().Select(parameter =>
            typeof(WasmMemory).IsAssignableFrom(parameter.ParameterType) ? (object)memory : importedDelegate).ToArray();
        var importedInstance = importedCtor.Invoke(importedArgs);
        if (!importedDuringConstruction || importedValue != 0x11 || importedLength != 4)
            throw new Exception("Start did not pass the imported-memory byte to the host.");
        if (!ReferenceEquals(importedType.GetProperty("memory")!.GetValue(importedInstance), memory))
            throw new Exception("Imported memory export is not the host memory.");

        byte[] owned = [0, 97, 115, 109, 1, 0, 0, 0,
            ..Section(1, types),
            ..Section(2, [1, ..peekImport]),
            ..Section(3, [1, 1]),
            ..Section(5, [1, 0, 1]),
            ..Section(7, [1, ..memoryName, 2, 0]),
            ..Section(8, [1]),
            ..Section(10, code),
            ..Section(11, data)];
        var ownedType = Compile(owned, "OwnedStart").GetType("Wasm2Cs.Generated.OwnedStart")!;
        object? ownedInstance = null;
        bool ownedPeekSawUnpublishedInstance = false;
        int ownedValue = -1;
        Action<int, int> ownedPeek = (value, length) =>
        {
            ownedPeekSawUnpublishedInstance = ownedInstance == null;
            ownedValue = value;
            if (length != 4) throw new Exception("Owned start passed the wrong length.");
        };
        var ownedDelegate = Delegate.CreateDelegate(ownedType.GetNestedType("__wasm_Import0")!, ownedPeek.Target, ownedPeek.Method);
        ownedInstance = Activator.CreateInstance(ownedType, ownedDelegate)!;
        var after = (byte[])ownedType.GetMethod("ReadMemory")!.Invoke(ownedInstance!, [8u, 4])!;
        if (ownedValue != 0x11 || !after.SequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0x44 }))
            throw new Exception("Owned memory was not initialized with the active data segment before start.");
        if (!ownedPeekSawUnpublishedInstance)
            throw new Exception("Owned-memory instance was visible to the host during start.");
        Console.WriteLine("PASS: host reads imported memory during start; owned memory is initialized but unpublished until the constructor returns.");
    }
    public static void WasiFdWrite()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WasiPreview1", "wasi_hello.wasm"));
        var type = Compile(bytes, "WasiHello").GetType("Wasm2Cs.Generated.WasiHello")!;
        var stdout = new List<byte>();
        var stderr = new List<byte>();
        var host = new HostEnvironment(stdout: stdout.AddRange, stderr: stderr.AddRange);
        object? guest = null;
        bool procExitCalled = false;
        Func<int, int, int, int, int> fdWrite = (fd, iovs, iovsLength, nwritten) =>
        {
            if (guest == null)
                throw new InvalidOperationException("Owned memory is not published until the constructor returns.");
            var memory = (WasmMemory)type.GetProperty("memory")!.GetValue(guest)!;
            return host.FdWrite(memory, fd, iovs, iovsLength, nwritten);
        };
        Action<int> procExit = code =>
        {
            procExitCalled = true;
            throw new InvalidOperationException("proc_exit is not connected: " + code);
        };
        var fdWriteDelegate = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import0")!, fdWrite.Target, fdWrite.Method);
        var procExitDelegate = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import1")!, procExit.Target, procExit.Method);
        var constructor = type.GetConstructors().Single(ctor => ctor.GetParameters().Length == 2);
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.Name == "import0" ? fdWriteDelegate : procExitDelegate).ToArray();
        guest = constructor.Invoke(arguments);
        // _start is void. A normal return does not call proc_exit; wasmtime reports that as exit 0.
        type.GetMethod("_start")!.Invoke(guest, null);
        if (procExitCalled) throw new Exception("proc_exit was called.");
        string text = System.Text.Encoding.ASCII.GetString(stdout.ToArray());
        if (text != "hello wasi\n") throw new Exception("stdout was " + text);
        if (stderr.Count != 0) throw new Exception("stderr was not empty.");
        Console.WriteLine("PASS: wasi_hello.wasm fd_write wrote hello wasi and _start returned normally. proc_exit was not called. Owned memory during start is still unpublished.");
    }
    public static void WasiProcExit()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WasiPreview1", "exit_code.wasm"));
        var type = Compile(bytes, "WasiExitCode").GetType("Wasm2Cs.Generated.WasiExitCode")!;
        var stdout = new List<byte>();
        var stderr = new List<byte>();
        var host = new HostEnvironment(stdout: stdout.AddRange, stderr: stderr.AddRange);
        object? guest = null;
        Func<int, int, int, int, int> fdWrite = (fd, iovs, iovsLength, nwritten) =>
        {
            if (guest == null)
                throw new InvalidOperationException("Owned memory is not published until the constructor returns.");
            var memory = (WasmMemory)type.GetProperty("memory")!.GetValue(guest)!;
            return host.FdWrite(memory, fd, iovs, iovsLength, nwritten);
        };
        Action<int> procExit = host.ProcExit;
        var fdWriteDelegate = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import0")!, fdWrite.Target, fdWrite.Method);
        var procExitDelegate = Delegate.CreateDelegate(type.GetNestedType("__wasm_Import1")!, procExit.Target, procExit.Method);
        var constructor = type.GetConstructors().Single(ctor => ctor.GetParameters().Length == 2);
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.Name == "import0" ? fdWriteDelegate : procExitDelegate).ToArray();
        guest = constructor.Invoke(arguments);
        // main returns 3, so wasi-libc's _start calls proc_exit(3). wasmtime and node:wasi report exit 3.
        int? exitCode = null;
        try { type.GetMethod("_start")!.Invoke(guest, null); }
        catch (TargetInvocationException exception) when (exception.InnerException is WasiProcExitException exit) { exitCode = exit.ExitCode; }
        if (exitCode != 3) throw new Exception("proc_exit code was " + (exitCode?.ToString() ?? "not called"));
        if (!host.HasExited || host.ExitCode != 3) throw new Exception("host exit state differs.");
        string text = System.Text.Encoding.ASCII.GetString(stderr.ToArray());
        if (text != "exit 3\n") throw new Exception("stderr was " + text);
        if (stdout.Count != 0) throw new Exception("stdout was not empty.");
        Console.WriteLine("PASS: exit_code.wasm wrote exit 3 to stderr and proc_exit(3) unwound _start through WasiProcExitException.");
    }
    public static void WasiPrintf()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WasiPreview1", "wasi_printf.wasm"));
        var type = Compile(bytes, "WasiPrintf").GetType("Wasm2Cs.Generated.WasiPrintf")!;
        var stdout = new List<byte>();
        var stderr = new List<byte>();
        var host = new HostEnvironment(stdout: stdout.AddRange, stderr: stderr.AddRange);
        object? guest = null;
        var calls = new List<string>();
        WasmMemory Memory() => guest == null
            ? throw new InvalidOperationException("Owned memory is not published until the constructor returns.")
            : (WasmMemory)type.GetProperty("memory")!.GetValue(guest)!;
        // Import order: fd_close, fd_fdstat_get, fd_seek, fd_write, proc_exit.
        Func<int, int> fdClose = fd => throw new UnsupportedImportException("wasi_snapshot_preview1", "fd_close", fd);
        Func<int, int, int> fdstatGet = (fd, buf) => { calls.Add($"fd_fdstat_get({fd})"); return host.FdFdstatGet(Memory(), fd, buf); };
        Func<int, long, int, int, int> fdSeek = (fd, offset, whence, result) =>
            throw new UnsupportedImportException("wasi_snapshot_preview1", "fd_seek", fd, offset, whence, result);
        Func<int, int, int, int, int> fdWrite = (fd, iovs, iovsLength, nwritten) => { calls.Add($"fd_write({fd})"); return host.FdWrite(Memory(), fd, iovs, iovsLength, nwritten); };
        Action<int> procExit = host.ProcExit;
        Delegate[] functions = [fdClose, fdstatGet, fdSeek, fdWrite, procExit];
        var constructor = type.GetConstructors().Single(ctor => ctor.GetParameters().Length == 5);
        var arguments = constructor.GetParameters().Select((parameter, index) =>
            (object)Delegate.CreateDelegate(type.GetNestedType("__wasm_Import" + index)!, functions[index].Target, functions[index].Method)).ToArray();
        guest = constructor.Invoke(arguments);
        // main returns 0: _start returns normally, as in wasmtime 28.0.1 and node:wasi.
        type.GetMethod("_start")!.Invoke(guest, null);
        if (host.HasExited) throw new Exception("proc_exit was called.");
        // node:wasi made the same three calls; fd_seek and fd_close are imported but not called.
        if (string.Join(" ", calls) != "fd_fdstat_get(1) fd_write(1) fd_write(2)") throw new Exception("WASI calls were " + string.Join(" ", calls));
        string text = System.Text.Encoding.ASCII.GetString(stdout.ToArray());
        if (text != "count=4 total=1028 mean=257.000 hex=0x404 name=wasi\n") throw new Exception("stdout was " + text);
        string error = System.Text.Encoding.ASCII.GetString(stderr.ToArray());
        if (error != "warn:  12.3%\n") throw new Exception("stderr was " + error);
        Console.WriteLine("PASS: wasi_printf.wasm printf/fprintf matched wasmtime and node:wasi through fd_fdstat_get and fd_write.");
    }
    private sealed record Outcome(bool Trapped, int Value);
}
