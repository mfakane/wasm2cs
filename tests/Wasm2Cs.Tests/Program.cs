extern alias Generator;
using WasmGenerator = Generator::Wasm2Cs.WasmGenerator;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Wasm2Cs;

if (args.Contains("--floating")) { ConformanceChecks.VerifyFloating(); await FloatChecks.Verify(); return; }

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p))
    .Append(MetadataReference.CreateFromFile(typeof(WasmMemory).Assembly.Location)).ToArray();
CSharpCompilation Compilation(string source) => CSharpCompilation.Create("Test_" + Guid.NewGuid().ToString("N"),
    [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9))], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow: true));
Assembly Compile(Microsoft.CodeAnalysis.Compilation compilation)
{
    using var stream = new MemoryStream();
    var result = compilation.Emit(stream);
    if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics));
    return Assembly.Load(stream.ToArray());
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

var wasmPath = Path.Combine(AppContext.BaseDirectory, "Arithmetic.wasm");
var bytes = File.ReadAllBytes(wasmPath);
var assembly = Compile(Compilation(Transpiler.Translate(bytes, "Arithmetic")));
var module = assembly.GetType("Wasm2Cs.Generated.Arithmetic")!;
var instance = Activator.CreateInstance(module)!;
var portableReferences = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ReferenceAssemblies"), "*.dll")
    .Select(p => MetadataReference.CreateFromFile(p));
Compile(Compilation(Transpiler.Translate(bytes, "Portable")).WithReferences(portableReferences));
Console.WriteLine("PASS: Generated C# 9 compiles against .NET Standard 2.0 reference assemblies.");
var calls = new List<Call>();
int[] boundaries = [0, 1, -1, int.MinValue, int.MaxValue, 65536, -65536];
foreach (var name in new[] { "add", "sub", "mul" })
    foreach (int a in boundaries)
        foreach (int b in boundaries) calls.Add(new(name, [a, b]));
var random = new Random(12345);
for (int i = 0; i < 500; i++)
{
    int a = (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1);
    int b = (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1);
    foreach (var name in new[] { "add", "sub", "mul" }) calls.Add(new(name, [a, b]));
    foreach (var name in new[] { "square", "snapshot", "tee", "early" }) calls.Add(new(name, [a]));
}
calls.Add(new("minimum", []));
calls.Add(new("negative", []));
var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "oracle.mjs"));
start.ArgumentList.Add(wasmPath);
using var node = Process.Start(start)!;
var output = node.StandardOutput.ReadToEndAsync();
var error = node.StandardError.ReadToEndAsync();
await node.StandardInput.WriteAsync(JsonSerializer.Serialize(calls));
node.StandardInput.Close();
await node.WaitForExitAsync();
Assert(node.ExitCode == 0, await error);
int[] expected = JsonSerializer.Deserialize<int[]>(await output)!;
Assert(expected.Length == calls.Count, "Oracle result count differs.");
for (int i = 0; i < calls.Count; i++)
{
    var call = calls[i];
    var actual = (int)module.GetMethod(call.Name)!.Invoke(instance, call.Args.Cast<object>().ToArray())!;
    Assert(actual == expected[i], $"{call.Name}({string.Join(',', call.Args)}): {actual} != {expected[i]}");
}
Console.WriteLine($"PASS: {calls.Count} C# results match Node.js WebAssembly (checked C# compilation).");

// Exercise invalid binary structure and invalid stack programs independently of the emitter.
byte[] Module(byte[] instructions) => [0,97,115,109,1,0,0,0, 1,5,1,0x60,0,1,0x7f,
    3,2,1,0, 7,5,1,1,(byte)'f',0,0, 10,(byte)(instructions.Length+3),1,(byte)(instructions.Length+1),0,..instructions];
var invalid = new (byte[] Bytes, string Message)[]
{
    ([], "Unexpected end"),
    ([1,97,115,109,1,0,0,0], "magic"),
    (bytes[..^1], "boundary"),
    ([..bytes, 1,0], "out-of-order"),
    ([0,97,115,109,1,0,0,0, 14,1,0], "Unsupported WASM section"),
    (Module([0x6a,0x0b]), "underflow"),
    (Module([0x20,0,0x0b]), "local index"),
    (Module([0x41,0,0x41,1,0x0b]), "stack height"),
    (Module([0x41,0]), "missing end"),
    (Module([0x41,0,0x0b,0x01]), "Trailing bytes"),
    (Module([0x41,0,0x41,1,0x92,0x0b]), "type mismatch"),
    (Module([0x41,0x80,0x80,0x80,0x80,0x08,0x0b]), "32 bits"),
    (Module([0x41,0x80,0x80,0x80,0x80,0x80,0,0x0b]), "too long"),
};
foreach (var test in invalid)
{
    try { Transpiler.Translate(test.Bytes, "Invalid"); throw new Exception("Invalid module accepted."); }
    catch (WasmException e) { Assert(e.Message.Contains(test.Message), $"Expected {test.Message}, got {e.Message}"); }
}
Console.WriteLine($"PASS: {invalid.Length} malformed/unsupported modules rejected.");

var input = new Input("Arithmetic.wasm.base64", Convert.ToBase64String(bytes));
GeneratorDriver driver = CSharpGeneratorDriver.Create([new WasmGenerator().AsSourceGenerator()], [input],
    parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9));
driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(""), out var generated, out var diagnostics);
Assert(diagnostics.Length == 0, string.Join("\n", diagnostics));
var generatedModule = Compile(generated).GetType("Wasm2Cs.Generated.Arithmetic")!;
Assert((int)generatedModule.GetMethod("add")!.Invoke(Activator.CreateInstance(generatedModule), [20,22])! == 42, "Generator result differs.");

// Same path, different content: exercise the incremental generator's content dependency.
var updated = new Input(input.Path, Convert.ToBase64String(Module([0x41,42,0x0b])));
driver = driver.ReplaceAdditionalText(input, updated);
driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(""), out generated, out diagnostics);
Assert(diagnostics.Length == 0, "Updated input failed.");
generatedModule = Compile(generated).GetType("Wasm2Cs.Generated.Arithmetic")!;
Assert((int)generatedModule.GetMethod("f")!.Invoke(Activator.CreateInstance(generatedModule), null)! == 42, "Generator retained stale binary.");
Assert(generatedModule.GetMethod("add") is null, "Old export retained.");
var broken = new Input(input.Path, Convert.ToBase64String(Module([0xff,0x0b])));
driver = driver.ReplaceAdditionalText(updated, broken).RunGenerators(Compilation(""));
var result = driver.GetRunResult();
Assert(result.Diagnostics.Any(d => d.Id == "WASM001" && d.Severity == DiagnosticSeverity.Error), "Missing WASM001.");
Assert(result.GeneratedTrees.Length == 0, "Invalid binary produced code.");
Console.WriteLine("PASS: Source Generator compilation, content updates, and WASM001 diagnostics.");

var unityInput = new Input("Arithmetic.Wasm2Cs.Generator.additionalfile", Convert.ToBase64String(bytes));
GeneratorDriver unityDriver = CSharpGeneratorDriver.Create([new WasmGenerator().AsSourceGenerator()], [unityInput],
    parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9));
unityDriver = unityDriver.RunGeneratorsAndUpdateCompilation(Compilation("").WithAssemblyName("Wasm2Cs.Modules"), out generated, out diagnostics);
Assert(diagnostics.Length == 0, "Unity input failed.");
Compile(generated);
Assert(unityDriver.GetRunResult().GeneratedTrees.Length == 1, "Unity module was not generated.");
unityDriver = unityDriver.RunGenerators(Compilation("").WithAssemblyName("Consumer"));
Assert(unityDriver.GetRunResult().GeneratedTrees.Length == 0, "Unity module leaked into a referencing assembly.");
var duplicate = new Input(unityInput.Path, "!WASM002:Duplicate module name: Arithmetic");
unityDriver = unityDriver.ReplaceAdditionalText(unityInput, duplicate)
    .RunGenerators(Compilation("").WithAssemblyName("Wasm2Cs.Modules"));
Assert(unityDriver.GetRunResult().Diagnostics.Any(d => d.Id == "WASM002"), "Duplicate Unity input not diagnosed.");
Console.WriteLine("PASS: Unity AdditionalFiles, asmdef isolation, and duplicate diagnostics.");
await ExecutionChecks.Numerics();
await ExecutionChecks.ControlFlow();
await ExecutionChecks.Calls();
await ExecutionChecks.Memory();
ExecutionChecks.SharedMemoryGlobalsAndBulkData();
ExecutionChecks.TargetProfiles();
await ExecutionChecks.CAlgorithms();
ExecutionChecks.Imports();
ConformanceChecks.Verify();
await TypedIrChecks.Verify();
await I64Checks.Verify();
await FloatChecks.Verify();
await TableChecks.Verify();
await ExceptionChecks.Verify();

record Call(string Name, int[] Args);
sealed class Input(string path, string content) : AdditionalText
{
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
}
