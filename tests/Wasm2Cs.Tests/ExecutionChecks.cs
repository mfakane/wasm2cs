using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Wasm2Cs;

internal static class ExecutionChecks
{
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
    internal static Assembly Compile(byte[] bytes, string name = "Subject")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Execution_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Transpiler.Translate(bytes,name),new CSharpParseOptions(LanguageVersion.CSharp9))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow:true));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success) throw new Exception(string.Join("\n",emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
    internal static async Task Compare(byte[] bytes, int[][] calls)
    {
        var type = Compile(bytes).GetType("Wasm2Cs.Generated.Subject")!;
        var instance = Activator.CreateInstance(type);
        var method = type.GetMethod("f")!;
        var start = new ProcessStartInfo("node") { RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"execution-oracle.mjs"));
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        await node.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Module=Convert.ToBase64String(bytes), Calls=calls }));
        node.StandardInput.Close();
        await node.WaitForExitAsync();
        if (node.ExitCode != 0) throw new Exception(await error);
        var expected = JsonSerializer.Deserialize<Outcome[]>(await output)!;
        if (expected.Length != calls.Length) throw new Exception("Oracle result count differs.");
        for (int i=0;i<calls.Length;i++)
        {
            Outcome actual;
            try { actual = new(false, (int)(method.Invoke(instance,calls[i].Cast<object>().ToArray()) ?? 0)); }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
            { actual = new(true,0); }
            if (actual != expected[i]) throw new Exception($"WASM/C# mismatch at ({string.Join(',',calls[i])}): {actual} != {expected[i]}");
        }
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
    private sealed record Outcome(bool Trapped, int Value);
}
