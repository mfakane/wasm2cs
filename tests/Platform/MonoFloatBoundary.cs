using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Standalone reproducer, outside the console test project. Compile with Unity's
// Mono mcs and run with its mono.exe. Native floats can quiet signaling NaNs;
// generated WASM values therefore keep bits in structs until an arithmetic use.
internal static class MonoFloatBoundary
{
    [StructLayout(LayoutKind.Explicit)]
    private struct F32
    {
        [FieldOffset(0)] public int Bits;
        [FieldOffset(0)] public float Value;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct F64
    {
        [FieldOffset(0)] public long Bits;
        [FieldOffset(0)] public double Value;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float Native32(int bits) { return new F32 { Bits = bits }.Value; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double Native64(long bits) { return new F64 { Bits = bits }.Value; }
    public static void Main()
    {
        Console.WriteLine("f32 native: {0:x8}; retained bits: {1:x8}",
            new F32 { Value = Native32(0x7f800001) }.Bits, new F32 { Bits = 0x7f800001 }.Bits);
        Console.WriteLine("f64 native: {0:x16}; retained bits: {1:x16}",
            new F64 { Value = Native64(0x7ff0000000000001L) }.Bits, new F64 { Bits = 0x7ff0000000000001L }.Bits);
    }
}
