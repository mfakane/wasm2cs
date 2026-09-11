namespace Wasm2Cs;

internal static class FloatOperations
{
    public static int Arity(byte op) => (op >= 0x8b && op <= 0x91) || (op >= 0x99 && op <= 0x9f) ? 1 :
        (op >= 0x5b && op <= 0x66) || (op >= 0x92 && op <= 0x98) || (op >= 0xa0 && op <= 0xa6) ? 2 : 0;
    public static ValueType InputType(byte op) => (op >= 0x5b && op <= 0x60) || (op >= 0x8b && op <= 0x98) ? ValueType.F32 : ValueType.F64;
    public static ValueType ResultType(byte op) => op <= 0x66 ? ValueType.I32 : InputType(op);
    public static string Expression(byte op, string a, string b)
    {
        bool single = InputType(op) == ValueType.F32;
        string width = single ? "32" : "64", cast = single ? "float" : "double";
        int normalized = op <= 0x66 ? (single ? op : op - 6) : (single ? op : op - 14);
        string expression = normalized switch
        {
            0x5b => $"({a} == {b} ? 1 : 0)", 0x5c => $"({a} != {b} ? 1 : 0)",
            0x5d => $"({a} < {b} ? 1 : 0)", 0x5e => $"({a} > {b} ? 1 : 0)",
            0x5f => $"({a} <= {b} ? 1 : 0)", 0x60 => $"({a} >= {b} ? 1 : 0)",
            0x8b => $"__wasm_F{width}(__wasm_Bits{width}({a}) & {(single ? "0x7fffffff" : "0x7fffffffffffffffL")})",
            0x8c => $"__wasm_F{width}(__wasm_Bits{width}({a}) ^ {(single ? "int.MinValue" : "long.MinValue")})",
            0x8d => $"({cast})__wasm_Round({a}, 0)", 0x8e => $"({cast})__wasm_Round({a}, 1)",
            0x8f => $"({cast})__wasm_Round({a}, 2)", 0x90 => $"({cast})__wasm_Round({a}, 3)",
            0x91 => $"({cast})global::System.Math.Sqrt({a})",
            0x92 => $"({cast})({a} + {b})", 0x93 => $"({cast})({a} - {b})",
            0x94 => $"({cast})({a} * {b})", 0x95 => $"({cast})({a} / {b})",
            0x96 => $"__wasm_MinMax({a}, {b}, false)", 0x97 => $"__wasm_MinMax({a}, {b}, true)",
            0x98 => $"__wasm_F{width}((__wasm_Bits{width}({a}) & {(single ? "int.MaxValue" : "long.MaxValue")}) | (__wasm_Bits{width}({b}) & {(single ? "int.MinValue" : "long.MinValue")}))",
            _ => throw new InvalidOperationException("Unknown validated floating-point instruction.")
        };
        return $"unchecked({expression})";
    }

    // Explicit-layout unions are available in .NET Standard 2.0 and preserve signaling NaNs.
    // No numeric literal, string conversion, allocation, or machine byte order enters a bitcast.
    public const string Helpers = """
    [global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct __wasm_Float32
    {
        [global::System.Runtime.InteropServices.FieldOffset(0)] public int Bits;
        [global::System.Runtime.InteropServices.FieldOffset(0)] public float Value;
    }
    [global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct __wasm_Float64
    {
        [global::System.Runtime.InteropServices.FieldOffset(0)] public long Bits;
        [global::System.Runtime.InteropServices.FieldOffset(0)] public double Value;
    }
    private static float __wasm_F32(int bits) { return new __wasm_Float32 { Bits = bits }.Value; }
    private static double __wasm_F64(long bits) { return new __wasm_Float64 { Bits = bits }.Value; }
    private static int __wasm_Bits32(float value) { return new __wasm_Float32 { Value = value }.Bits; }
    private static long __wasm_Bits64(double value) { return new __wasm_Float64 { Value = value }.Bits; }
    private static double __wasm_Round(double value, int mode)
    {
        // Rounding is arithmetic, so quiet a signaling NaN. Abs/neg/copysign do not.
        if (double.IsNaN(value)) return __wasm_F64(0x7ff8000000000000L);
        double rounded = mode == 0 ? global::System.Math.Ceiling(value) :
            mode == 1 ? global::System.Math.Floor(value) : mode == 2 ? global::System.Math.Truncate(value) :
            global::System.Math.Round(value, global::System.MidpointRounding.ToEven);
        return rounded == 0 ? __wasm_F64(__wasm_Bits64(value) & long.MinValue) : rounded;
    }
    private static float __wasm_MinMax(float a, float b, bool maximum)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return __wasm_F32(0x7fc00000);
        if (a == 0 && b == 0) return __wasm_F32(maximum ? __wasm_Bits32(a) & __wasm_Bits32(b) : __wasm_Bits32(a) | __wasm_Bits32(b));
        return maximum ? (a > b ? a : b) : (a < b ? a : b);
    }
    private static double __wasm_MinMax(double a, double b, bool maximum)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return __wasm_F64(0x7ff8000000000000L);
        if (a == 0 && b == 0) return __wasm_F64(maximum ? __wasm_Bits64(a) & __wasm_Bits64(b) : __wasm_Bits64(a) | __wasm_Bits64(b));
        return maximum ? (a > b ? a : b) : (a < b ? a : b);
    }

""";
}
