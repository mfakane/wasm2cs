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
        string width = single ? "32" : "64";
        string x = a + ".Value", y = b + ".Value";
        string Rounded(string expression) => $"__wasm_FromF{width}(({(single ? "float" : "double")})({expression}))";
        int normalized = op <= 0x66 ? (single ? op : op - 6) : (single ? op : op - 14);
        string expression = normalized switch
        {
            0x5b => $"({x} == {y} ? 1 : 0)", 0x5c => $"({x} != {y} ? 1 : 0)",
            0x5d => $"({x} < {y} ? 1 : 0)", 0x5e => $"({x} > {y} ? 1 : 0)",
            0x5f => $"({x} <= {y} ? 1 : 0)", 0x60 => $"({x} >= {y} ? 1 : 0)",
            0x8b => $"__wasm_F{width}(__wasm_Bits{width}({a}) & {(single ? "0x7fffffff" : "0x7fffffffffffffffL")})",
            0x8c => $"__wasm_F{width}(__wasm_Bits{width}({a}) ^ {(single ? "int.MinValue" : "long.MinValue")})",
            0x8d => Rounded($"__wasm_Round({x}, 0)"), 0x8e => Rounded($"__wasm_Round({x}, 1)"),
            0x8f => Rounded($"__wasm_Round({x}, 2)"), 0x90 => Rounded($"__wasm_Round({x}, 3)"),
            0x91 => Rounded($"global::System.Math.Sqrt({x})"),
            0x92 => Rounded($"{x} + {y}"), 0x93 => Rounded($"{x} - {y}"),
            0x94 => Rounded($"{x} * {y}"), 0x95 => Rounded($"{x} / {y}"),
            0x96 => $"__wasm_MinMax({a}, {b}, false)", 0x97 => $"__wasm_MinMax({a}, {b}, true)",
            0x98 => $"__wasm_F{width}((__wasm_Bits{width}({a}) & {(single ? "int.MaxValue" : "long.MaxValue")}) | (__wasm_Bits{width}({b}) & {(single ? "int.MinValue" : "long.MinValue")}))",
            _ => throw new InvalidOperationException("Unknown validated floating-point instruction.")
        };
        return $"unchecked({expression})";
    }

    // Keep values in explicit-layout structs: loading a CLR float can quiet signaling NaNs on Mono.
    // Only arithmetic and the native host API read Value; bit-preserving operations read Bits.
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
    private static __wasm_Float32 __wasm_F32(int bits) { return new __wasm_Float32 { Bits = bits }; }
    private static __wasm_Float32 __wasm_FromF32(float value) { return new __wasm_Float32 { Value = value }; }
    private static __wasm_Float64 __wasm_F64(long bits) { return new __wasm_Float64 { Bits = bits }; }
    private static __wasm_Float64 __wasm_FromF64(double value) { return new __wasm_Float64 { Value = value }; }
    private static int __wasm_Bits32(__wasm_Float32 value) { return value.Bits; }
    private static long __wasm_Bits64(__wasm_Float64 value) { return value.Bits; }
    private static double __wasm_Round(double value, int mode)
    {
        // Rounding is arithmetic, so quiet a signaling NaN. Abs/neg/copysign do not.
        if (double.IsNaN(value)) return __wasm_F64(0x7ff8000000000000L).Value;
        double rounded = mode == 0 ? global::System.Math.Ceiling(value) :
            mode == 1 ? global::System.Math.Floor(value) : mode == 2 ? global::System.Math.Truncate(value) :
            global::System.Math.Round(value, global::System.MidpointRounding.ToEven);
        return rounded == 0 ? __wasm_F64(__wasm_Bits64(__wasm_FromF64(value)) & long.MinValue).Value : rounded;
    }
    private static __wasm_Float32 __wasm_MinMax(__wasm_Float32 a, __wasm_Float32 b, bool maximum)
    {
        if (float.IsNaN(a.Value) || float.IsNaN(b.Value)) return __wasm_F32(0x7fc00000);
        if (a.Value == 0 && b.Value == 0) return __wasm_F32(maximum ? __wasm_Bits32(a) & __wasm_Bits32(b) : __wasm_Bits32(a) | __wasm_Bits32(b));
        return maximum ? (a.Value > b.Value ? a : b) : (a.Value < b.Value ? a : b);
    }
    private static __wasm_Float64 __wasm_MinMax(__wasm_Float64 a, __wasm_Float64 b, bool maximum)
    {
        if (double.IsNaN(a.Value) || double.IsNaN(b.Value)) return __wasm_F64(0x7ff8000000000000L);
        if (a.Value == 0 && b.Value == 0) return __wasm_F64(maximum ? __wasm_Bits64(a) & __wasm_Bits64(b) : __wasm_Bits64(a) | __wasm_Bits64(b));
        return maximum ? (a.Value > b.Value ? a : b) : (a.Value < b.Value ? a : b);
    }

""";
}
