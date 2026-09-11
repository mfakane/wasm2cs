namespace Wasm2Cs;

internal static class I64Operations
{
    public static int Arity(byte opcode) => opcode == 0x50 || (opcode >= 0x79 && opcode <= 0x7b) ||
        opcode == 0xa7 || opcode == 0xac || opcode == 0xad || (opcode >= 0xc2 && opcode <= 0xc4) ? 1 :
        (opcode >= 0x51 && opcode <= 0x5a) || (opcode >= 0x7c && opcode <= 0x8a) ? 2 : 0;
    public static ValueType InputType(byte opcode) => opcode == 0xac || opcode == 0xad ? ValueType.I32 : ValueType.I64;
    public static ValueType ResultType(byte opcode) => (opcode >= 0x50 && opcode <= 0x5a) || opcode == 0xa7 ? ValueType.I32 : ValueType.I64;

    public static string Expression(byte opcode, string a, string b)
    {
        // Cast shift counts only after masking: the WASM operand is itself an i64.
        string shift = $"(int)({b} & 63L)";
        string expression = opcode switch
        {
            0x50 => $"({a} == 0 ? 1 : 0)",
            0x51 => $"({a} == {b} ? 1 : 0)", 0x52 => $"({a} != {b} ? 1 : 0)",
            0x53 => $"({a} < {b} ? 1 : 0)", 0x54 => $"((ulong){a} < (ulong){b} ? 1 : 0)",
            0x55 => $"({a} > {b} ? 1 : 0)", 0x56 => $"((ulong){a} > (ulong){b} ? 1 : 0)",
            0x57 => $"({a} <= {b} ? 1 : 0)", 0x58 => $"((ulong){a} <= (ulong){b} ? 1 : 0)",
            0x59 => $"({a} >= {b} ? 1 : 0)", 0x5a => $"((ulong){a} >= (ulong){b} ? 1 : 0)",
            0x79 => $"__wasm_Count64({a}, 0)", 0x7a => $"__wasm_Count64({a}, 1)", 0x7b => $"__wasm_Count64({a}, 2)",
            0x7c => $"{a} + {b}", 0x7d => $"{a} - {b}", 0x7e => $"{a} * {b}",
            0x7f => $"__wasm_Divide64({a}, {b}, true, false)", 0x80 => $"__wasm_Divide64({a}, {b}, false, false)",
            0x81 => $"__wasm_Divide64({a}, {b}, true, true)", 0x82 => $"__wasm_Divide64({a}, {b}, false, true)",
            0x83 => $"{a} & {b}", 0x84 => $"{a} | {b}", 0x85 => $"{a} ^ {b}",
            0x86 => $"{a} << {shift}", 0x87 => $"{a} >> {shift}", 0x88 => $"(long)((ulong){a} >> {shift})",
            0x89 => $"(long)(((ulong){a} << {shift}) | ((ulong){a} >> ((64 - {shift}) & 63)))",
            0x8a => $"(long)(((ulong){a} >> {shift}) | ((ulong){a} << ((64 - {shift}) & 63)))",
            0xa7 => $"(int){a}", 0xac => $"(long){a}", 0xad => $"(long)(uint){a}",
            0xc2 => $"(long)(sbyte){a}", 0xc3 => $"(long)(short){a}", 0xc4 => $"(long)(int){a}",
            _ => throw new InvalidOperationException("Unknown validated i64 instruction.")
        };
        return $"unchecked({expression})";
    }

    public const string Helpers = """
    private static long __wasm_Divide64(long a, long b, bool signed, bool remainder)
    {
        unchecked
        {
            if (b == 0) throw new TrapException(TrapKind.DivisionByZero);
            if (!signed) return remainder ? (long)((ulong)a % (ulong)b) : (long)((ulong)a / (ulong)b);
            if (a == long.MinValue && b == -1)
            {
                if (remainder) return 0;
                throw new TrapException(TrapKind.IntegerOverflow);
            }
            return remainder ? a % b : a / b;
        }
    }
    private static long __wasm_Count64(long value, int mode)
    {
        unchecked
        {
            ulong bits = (ulong)value;
            long count = 0;
            if (mode == 2) { while (bits != 0) { bits &= bits - 1; count++; } return count; }
            if (bits == 0) return 64;
            if (mode == 0) { while ((bits & 0x8000000000000000UL) == 0) { count++; bits <<= 1; } }
            else { while ((bits & 1UL) == 0) { count++; bits >>= 1; } }
            return count;
        }
    }

""";
}
