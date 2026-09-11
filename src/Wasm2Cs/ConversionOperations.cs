namespace Wasm2Cs;

internal static class ConversionOperations
{
    public static bool Supports(byte op) => (op >= 0xa8 && op <= 0xab) || (op >= 0xae && op <= 0xbf) || op == 0xfc;
    // The decoder permits only subopcodes 0..7 for the 0xfc prefix.
    private static int Normalize(byte op, int sub) => op != 0xfc ? op : sub < 4 ? 0xa8 + sub : 0xae + sub - 4;
    public static ValueType InputType(byte op, int sub = 0) => Normalize(op, sub) switch
    {
        0xa8 or 0xa9 or 0xae or 0xaf or 0xbb or 0xbc => ValueType.F32,
        0xaa or 0xab or 0xb0 or 0xb1 or 0xb6 or 0xbd => ValueType.F64,
        0xb2 or 0xb3 or 0xb7 or 0xb8 or 0xbe => ValueType.I32,
        _ => ValueType.I64
    };
    public static ValueType ResultType(byte op, int sub = 0) => Normalize(op, sub) switch
    {
        >= 0xa8 and <= 0xab or 0xbc => ValueType.I32,
        >= 0xae and <= 0xb1 or 0xbd => ValueType.I64,
        >= 0xb2 and <= 0xb6 or 0xbe => ValueType.F32,
        _ => ValueType.F64
    };
    public static string Expression(byte op, int sub, string value)
    {
        int normalized = Normalize(op, sub);
        if ((normalized >= 0xa8 && normalized <= 0xab) || (normalized >= 0xae && normalized <= 0xb1))
            return $"__wasm_Trunc{(ResultType(op, sub) == ValueType.I32 ? "32" : "64")}({value}, {(normalized % 2 == 0 ? "true" : "false")}, {(op == 0xfc ? "true" : "false")})";
        string expression = op switch
        {
            0xb2 => $"(float){value}", 0xb3 => $"(float)(uint){value}",
            0xb4 => $"(float){value}", 0xb5 => $"(float)(ulong){value}",
            0xb6 => $"(float){value}",
            0xb7 => $"(double){value}", 0xb8 => $"(double)(uint){value}",
            0xb9 => $"(double){value}", 0xba => $"(double)(ulong){value}",
            0xbb => $"(double){value}",
            0xbc => $"__wasm_Bits32({value})", 0xbd => $"__wasm_Bits64({value})",
            0xbe => $"__wasm_F32({value})", 0xbf => $"__wasm_F64({value})",
            _ => throw new InvalidOperationException("Unknown validated conversion instruction.")
        };
        return $"unchecked({expression})";
    }

    public const string Helpers = """
    private static int __wasm_Trunc32(double value, bool signed, bool saturate)
    {
        if (double.IsNaN(value))
        {
            if (saturate) return 0;
            throw new TrapException(TrapKind.InvalidConversionToInteger);
        }
        // Check the truncated integer: e.g. -0.9 is a valid unsigned zero.
        double integer = global::System.Math.Truncate(value);
        double lower = signed ? -2147483648.0 : 0.0, upper = signed ? 2147483648.0 : 4294967296.0;
        if (integer < lower)
        {
            if (saturate) return signed ? int.MinValue : 0;
            throw new TrapException(TrapKind.IntegerOverflow);
        }
        if (integer >= upper)
        {
            if (saturate) return signed ? int.MaxValue : -1;
            throw new TrapException(TrapKind.IntegerOverflow);
        }
        return signed ? unchecked((int)integer) : unchecked((int)(uint)integer);
    }
    private static long __wasm_Trunc64(double value, bool signed, bool saturate)
    {
        if (double.IsNaN(value))
        {
            if (saturate) return 0;
            throw new TrapException(TrapKind.InvalidConversionToInteger);
        }
        double integer = global::System.Math.Truncate(value);
        double lower = signed ? -9223372036854775808.0 : 0.0;
        double upper = signed ? 9223372036854775808.0 : 18446744073709551616.0;
        if (integer < lower)
        {
            if (saturate) return signed ? long.MinValue : 0;
            throw new TrapException(TrapKind.IntegerOverflow);
        }
        if (integer >= upper)
        {
            if (saturate) return signed ? long.MaxValue : -1;
            throw new TrapException(TrapKind.IntegerOverflow);
        }
        // Keep each cast in the signed range, including on Mono and IL2CPP.
        if (!signed && integer >= 9223372036854775808.0)
            return unchecked((long)(integer - 9223372036854775808.0)) | long.MinValue;
        return unchecked((long)integer);
    }

""";
}
