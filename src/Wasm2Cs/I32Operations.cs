namespace Wasm2Cs;

internal static class I32Operations
{
    public static int Arity(byte opcode) => opcode == 0x45 || (opcode >= 0x67 && opcode <= 0x69) || opcode == 0xc0 || opcode == 0xc1 ? 1 :
        (opcode >= 0x46 && opcode <= 0x4f) || (opcode >= 0x6a && opcode <= 0x78) ? 2 : 0;

    public static string Expression(byte opcode, string a, string b)
    {
        string expression = opcode switch
        {
            0x45 => $"({a} == 0 ? 1 : 0)",
            0x46 => $"({a} == {b} ? 1 : 0)", 0x47 => $"({a} != {b} ? 1 : 0)",
            0x48 => $"({a} < {b} ? 1 : 0)", 0x49 => $"((uint){a} < (uint){b} ? 1 : 0)",
            0x4a => $"({a} > {b} ? 1 : 0)", 0x4b => $"((uint){a} > (uint){b} ? 1 : 0)",
            0x4c => $"({a} <= {b} ? 1 : 0)", 0x4d => $"((uint){a} <= (uint){b} ? 1 : 0)",
            0x4e => $"({a} >= {b} ? 1 : 0)", 0x4f => $"((uint){a} >= (uint){b} ? 1 : 0)",
            0x67 => $"__wasm_Count({a}, 0)", 0x68 => $"__wasm_Count({a}, 1)", 0x69 => $"__wasm_Count({a}, 2)",
            0x6a => $"{a} + {b}", 0x6b => $"{a} - {b}", 0x6c => $"{a} * {b}",
            0x6d => $"__wasm_Divide({a}, {b}, true, false)", 0x6e => $"__wasm_Divide({a}, {b}, false, false)",
            0x6f => $"__wasm_Divide({a}, {b}, true, true)", 0x70 => $"__wasm_Divide({a}, {b}, false, true)",
            0x71 => $"{a} & {b}", 0x72 => $"{a} | {b}", 0x73 => $"{a} ^ {b}",
            0x74 => $"{a} << ({b} & 31)", 0x75 => $"{a} >> ({b} & 31)",
            0x76 => $"(int)((uint){a} >> ({b} & 31))",
            0x77 => $"(int)(((uint){a} << ({b} & 31)) | ((uint){a} >> ((32 - {b}) & 31)))",
            0x78 => $"(int)(((uint){a} >> ({b} & 31)) | ((uint){a} << ((32 - {b}) & 31)))",
            0xc0 => $"(int)(sbyte){a}", 0xc1 => $"(int)(short){a}",
            _ => throw new InvalidOperationException("Unknown validated i32 instruction.")
        };
        return $"unchecked({expression})";
    }

    public const string Helpers = """
    public enum TrapKind { Unreachable, DivisionByZero, IntegerOverflow, MemoryOutOfBounds, InvalidConversionToInteger }
    public sealed class TrapException : global::System.Exception
    {
        public TrapKind Kind { get; private set; }
        public TrapException(TrapKind kind) : base(kind.ToString()) { Kind = kind; }
    }
    private static int __wasm_Divide(int a, int b, bool signed, bool remainder)
    {
        unchecked
        {
            if (b == 0) throw new TrapException(TrapKind.DivisionByZero);
            if (!signed) return remainder ? (int)((uint)a % (uint)b) : (int)((uint)a / (uint)b);
            if (a == int.MinValue && b == -1)
            {
                if (remainder) return 0;
                throw new TrapException(TrapKind.IntegerOverflow);
            }
            return remainder ? a % b : a / b;
        }
    }
    private static int __wasm_Count(int value, int mode)
    {
        unchecked
        {
            uint bits = (uint)value;
            int count = 0;
            if (mode == 2) { while (bits != 0) { bits &= bits - 1; count++; } return count; }
            if (bits == 0) return 32;
            if (mode == 0) { while ((bits & 0x80000000u) == 0) { count++; bits <<= 1; } }
            else { while ((bits & 1u) == 0) { count++; bits >>= 1; } }
            return count;
        }
    }

""";
}
