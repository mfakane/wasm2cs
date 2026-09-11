using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Wasm2Cs;

internal static class ConformanceChecks
{
    public static void Verify()
    {
        VerifyFile("i32.json", (350, 9, 54, 29));
        VerifyFile("typed-ir.json", (32, 2, 23, 0), portable: true);
    }
    private static void VerifyFile(string file, (int, int, int, int) expected, bool portable = false)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", file)));
        var counts = VerifyCases(document.RootElement, portable);
        if (counts != expected) throw new Exception($"{file}: conformance coverage unexpectedly changed: {counts}");
        Console.WriteLine($"PASS: {file}: {counts.Returned} returns, {counts.Trapped} typed traps, {counts.Rejected} invalid modules; {counts.Skipped} explicit skips.");
    }
    internal static (int Returned, int Trapped, int Rejected, int Skipped) VerifyCases(JsonElement document, bool portable = false)
    {
        if (document.GetProperty("SchemaVersion").GetInt32() != 2) throw new Exception("Unsupported conformance schema version.");
        object? instance = null;
        Type? type = null;
        int returned = 0, trapped = 0, rejected = 0, skipped = 0;
        foreach (var test in document.GetProperty("Cases").EnumerateArray())
        {
            string? kind = test.GetProperty("Kind").GetString();
            int line = test.GetProperty("Line").GetInt32();
            switch (kind)
            {
                case "module":
                    type = ExecutionChecks.Compile(Convert.FromBase64String(test.GetProperty("Binary").GetString()!), portable: portable).GetType("Wasm2Cs.Generated.Subject")!;
                    instance = Activator.CreateInstance(type);
                    break;
                case "assert_return": case "assert_trap":
                    var arguments = test.GetProperty("Args").EnumerateArray().Select(a => ConformanceValue.Read(a).ToRuntime()).ToArray();
                    try
                    {
                        string name = test.GetProperty("Export").GetString()!;
                        object? value;
                        switch (test.GetProperty("Action").GetString())
                        {
                            case "invoke": value = type!.GetMethod(name)!.Invoke(instance, arguments); break;
                            case "get":
                                if (arguments.Length != 0) throw new Exception("Get action cannot have arguments.");
                                value = type!.GetProperty(name)!.GetValue(instance); break;
                            default: throw new Exception("Unknown conformance action.");
                        }
                        if (kind == "assert_trap") throw new Exception($"Line {line}: missing trap");
                        object?[] actual = value switch
                        {
                            null => [], ITuple tuple => Enumerable.Range(0, tuple.Length).Select(i => tuple[i]).ToArray(), _ => [value]
                        };
                        var expected = test.GetProperty("Expected").EnumerateArray().Select(ConformanceValue.Read).ToArray();
                        if (actual.Length != expected.Length || actual.Where((v, i) => !expected[i].Matches(v)).Any())
                            throw new Exception($"Line {line}: wrong typed result");
                        returned++;
                    }
                    catch (TargetInvocationException e) when (kind == "assert_trap" && e.InnerException != null && e.InnerException.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
                    {
                        string? expected = test.GetProperty("Trap").GetString();
                        if (expected is not ("Unreachable" or "DivisionByZero" or "IntegerOverflow" or "MemoryOutOfBounds"))
                            throw new Exception("Unsupported conformance trap: " + expected);
                        if (e.InnerException!.GetType().GetProperty("Kind")!.GetValue(e.InnerException)!.ToString() != expected)
                            throw new Exception($"Line {line}: wrong trap kind");
                        trapped++;
                    }
                    break;
                case "assert_invalid": case "assert_malformed":
                    try { Transpiler.Translate(Convert.FromBase64String(test.GetProperty("Binary").GetString()!), "Subject"); }
                    catch (WasmException) { rejected++; break; }
                    throw new Exception($"Line {line}: accepted invalid module");
                case "skip":
                    if (string.IsNullOrEmpty(test.GetProperty("Reason").GetString())) throw new Exception("Skip requires an explicit reason.");
                    skipped++; break;
                default: throw new Exception("Unknown conformance command: " + kind);
            }
        }
        return (returned, trapped, rejected, skipped);
    }
}
