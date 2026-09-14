using System.Globalization;
using System.Text;

namespace Wasm2Cs;

internal static class ExportNames
{
    public static bool IsIdentifier(string name) => name.Length > 0 &&
        (IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => IsLetter(c) || c == '_' || c >= '0' && c <= '9');

    public static bool IsReserved(string name) => name.StartsWith("__wasm_", StringComparison.Ordinal) ||
        name is "TrapKind" or "TrapException" or "ReadMemory" or "WriteMemory" or "MemorySize";

    public static string For(string name)
    {
        if (IsIdentifier(name) && !IsReserved(name)) return name;
        if (IsIdentifier(name)) return "wasm_export_" + name;
        var bytes = Encoding.UTF8.GetBytes(name);
        var mapped = new StringBuilder("wasm_export_");
        foreach (var value in bytes) mapped.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return mapped.ToString();
    }

    private static bool IsLetter(char value) => value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';
}
