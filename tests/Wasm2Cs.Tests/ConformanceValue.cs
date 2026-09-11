using System.Globalization;
using System.Text.Json;

internal sealed record ConformanceValue(string Type, string? Bits = null, string? NaN = null)
{
    internal static ConformanceValue Read(JsonElement element) => element.Deserialize<ConformanceValue>()
        ?? throw new Exception("Missing conformance value.");
    private int Width => Type switch { "i32" or "f32" => 32, "i64" or "f64" => 64, _ => throw new Exception("Unsupported conformance value type: " + Type) };
    private ulong BitPattern()
    {
        if (NaN != null || Bits == null || Bits.Length != Width / 4 || Bits.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new Exception("Invalid conformance value bits.");
        return ulong.Parse(Bits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
    public object ToRuntime()
    {
        ulong bits = BitPattern();
        return Type switch
        {
            "i32" => (object)unchecked((int)bits), "i64" => unchecked((long)bits),
            "f32" => BitConverter.Int32BitsToSingle(unchecked((int)bits)),
            "f64" => BitConverter.Int64BitsToDouble(unchecked((long)bits)),
            _ => throw new Exception("Unsupported conformance value type: " + Type)
        };
    }
    public static ConformanceValue FromRuntime(string type, object? value)
    {
        ulong bits = (type, value) switch
        {
            ("i32", int n) => unchecked((uint)n), ("i64", long n) => unchecked((ulong)n),
            ("f32", float n) => unchecked((uint)BitConverter.SingleToInt32Bits(n)),
            ("f64", double n) => unchecked((ulong)BitConverter.DoubleToInt64Bits(n)),
            _ => throw new Exception($"Expected {type}, got {value?.GetType().Name ?? "null"}.")
        };
        return new(type, bits.ToString(type is "i32" or "f32" ? "x8" : "x16", CultureInfo.InvariantCulture));
    }
    public bool Matches(object? actual)
    {
        var encoded = FromRuntime(Type, actual);
        if (NaN == null) { BitPattern(); return Bits == encoded.Bits; }
        if (Bits != null || Type is not ("f32" or "f64") || NaN is not ("canonical" or "arithmetic"))
            throw new Exception("Invalid conformance NaN expectation.");
        ulong quiet = Width == 32 ? 0x00400000UL : 0x0008000000000000UL;
        ulong exponent = Width == 32 ? 0x7f800000UL : 0x7ff0000000000000UL;
        ulong magnitude = encoded.BitPattern() & (Width == 32 ? 0x7fffffffUL : 0x7fffffffffffffffUL);
        return NaN == "canonical" ? magnitude == (exponent | quiet) : (magnitude & (exponent | quiet)) == (exponent | quiet);
    }
}
