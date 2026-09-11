using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Wasm2Cs;

internal static class ConformanceChecks
{
    public static void Verify()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Conformance/i32.json")));
        object? instance = null;
        Type? type = null;
        int returned=0, trapped=0, rejected=0, skipped=0;
        foreach (var test in document.RootElement.GetProperty("Cases").EnumerateArray())
        {
            string? kind=test.GetProperty("Kind").GetString();
            int line=test.GetProperty("Line").GetInt32();
            switch(kind)
            {
                case "module":
                    type=ExecutionChecks.Compile(Convert.FromBase64String(test.GetProperty("Binary").GetString()!)).GetType("Wasm2Cs.Generated.Subject")!;
                    instance=Activator.CreateInstance(type);
                    break;
                case "assert_return": case "assert_trap":
                    var method=type!.GetMethod(test.GetProperty("Export").GetString()!)!;
                    var arguments=test.GetProperty("Args").EnumerateArray().Select(a=>(object)a.GetInt32()).ToArray();
                    try
                    {
                        int value=(int)method.Invoke(instance,arguments)!;
                        if (kind == "assert_trap") throw new Exception($"Official i32.wast:{line}: missing trap");
                        if (value != test.GetProperty("Expected").GetInt32()) throw new Exception($"Official i32.wast:{line}: wrong result {value.ToString(CultureInfo.InvariantCulture)}");
                        returned++;
                    }
                    catch (TargetInvocationException e) when (kind == "assert_trap" && e.InnerException?.GetType().DeclaringType == type && e.InnerException.GetType().Name == "TrapException")
                    {
                        string expected=test.GetProperty("Message").GetString() == "integer divide by zero" ? "DivisionByZero" : "IntegerOverflow";
                        if (e.InnerException!.GetType().GetProperty("Kind")!.GetValue(e.InnerException)!.ToString() != expected)
                            throw new Exception($"Official i32.wast:{line}: wrong trap kind");
                        trapped++;
                    }
                    break;
                case "assert_invalid":
                    try { Transpiler.Translate(Convert.FromBase64String(test.GetProperty("Binary").GetString()!),"Subject"); }
                    catch(WasmException) { rejected++; break; }
                    throw new Exception($"Official i32.wast:{line}: accepted invalid module");
                case "skip": skipped++; break;
                default: throw new Exception("Unknown conformance command: "+kind);
            }
        }
        if (returned != 350 || trapped != 9 || rejected != 54 || skipped != 29) throw new Exception("Official conformance coverage unexpectedly changed.");
        Console.WriteLine($"PASS: official i32.wast: {returned} returns, {trapped} typed traps, {rejected} invalid modules; {skipped} explicit non-i32/text-only skips.");
    }
}
