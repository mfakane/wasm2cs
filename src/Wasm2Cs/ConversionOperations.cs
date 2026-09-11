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
        string numeric = InputType(op, sub) == ValueType.F32 || InputType(op, sub) == ValueType.F64 ? value + ".Value" : value;
        if ((normalized >= 0xa8 && normalized <= 0xab) || (normalized >= 0xae && normalized <= 0xb1))
            return $"__wasm_Trunc{(ResultType(op, sub) == ValueType.I32 ? "32" : "64")}({numeric}, {(normalized % 2 == 0 ? "true" : "false")}, {(op == 0xfc ? "true" : "false")})";
        if ((op >= 0xb2 && op <= 0xb5) || (op >= 0xb7 && op <= 0xba))
        {
            bool single = op <= 0xb5, signed = single ? op % 2 == 0 : op % 2 != 0;
            string integer = op == 0xb3 || op == 0xb8 ? $"(long)(uint){value}" : value;
            return $"unchecked(__wasm_F{(single ? "32" : "64")}(({(single ? "int" : "long")})__wasm_IntegerFloat({integer}, {(signed ? "true" : "false")}, {(single ? "23, 127" : "52, 1023")})))";
        }
        string expression = op switch
        {
            0xb6 => $"(float){numeric}",
            0xbb => $"(double){numeric}",
            0xbc => $"__wasm_Bits32({value})", 0xbd => $"__wasm_Bits64({value})",
            0xbe => $"__wasm_F32({value})", 0xbf => $"__wasm_F64({value})",
            _ => throw new InvalidOperationException("Unknown validated conversion instruction.")
        };
        if (ResultType(op, sub) == ValueType.F32 && op != 0xbe) expression = $"__wasm_FromF32({expression})";
        if (ResultType(op, sub) == ValueType.F64 && op != 0xbf) expression = $"__wasm_FromF64({expression})";
        return $"unchecked({expression})";
    }

    public const string Helpers = """
    private static ulong __wasm_IntegerFloat(long value, bool signed, int fractionBits, int bias)
    {
        // Round once from integer bits. Mono's ulong -> double -> float path
        // can round a value above a midpoint down to the midpoint first.
        unchecked
        {
            bool negative = signed && value < 0;
            ulong magnitude = negative ? 0UL - (ulong)value : (ulong)value;
            if (magnitude == 0) return 0;
            int exponent = 63;
            while ((magnitude >> exponent) == 0) exponent--;
            ulong significand;
            if (exponent <= fractionBits) significand = magnitude << (fractionBits - exponent);
            else
            {
                int shift = exponent - fractionBits;
                significand = magnitude >> shift;
                ulong lost = magnitude & ((1UL << shift) - 1), halfway = 1UL << (shift - 1);
                if (lost > halfway || (lost == halfway && (significand & 1) != 0))
                {
                    significand++;
                    if (significand == (1UL << (fractionBits + 1))) { significand >>= 1; exponent++; }
                }
            }
            ulong sign = negative ? (fractionBits == 23 ? 0x80000000UL : 0x8000000000000000UL) : 0;
            return sign | ((ulong)(exponent + bias) << fractionBits) | (significand & ((1UL << fractionBits) - 1));
        }
    }
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
