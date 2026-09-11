using System;
using Wasm2Cs.Generated;

// Shared by the .NET sample, isolated NuGet consumer, Unity Editor and IL2CPP player.
public static class FloatingChecks
{
    private static float F32(int bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits),0); }
    private static double F64(long bits) { return BitConverter.ToDouble(BitConverter.GetBytes(bits),0); }
    private static int B32(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value),0); }
    private static long B64(double value) { return BitConverter.ToInt64(BitConverter.GetBytes(value),0); }
    private static void Equal(long actual, long expected, string operation)
    {
        if (actual != expected) throw new Exception(operation + ": actual bits " + actual.ToString("x16") + ", expected " + expected.ToString("x16"));
    }
    private static void Trap(Action action, Floating.TrapKind expected)
    {
        try { action(); }
        catch (Floating.TrapException e) { if (e.Kind == expected) return; throw; }
        throw new Exception("Missing floating-point trap: " + expected);
    }
    public static void Verify()
    {
        int observed32 = 0; long observed64 = 0;
        var f = Floating.__wasm_FromBits(v => { observed32 = v; return v; }, v => { observed64 = v; return v; });
        int[] patterns32 = { 0, int.MinValue, 1, unchecked((int)0x80000001), 0x7f7fffff, 0x7f800000,
            unchecked((int)0xff800000), 0x7f800001, unchecked((int)0xff812345), 0x7fc12345 };
        long[] patterns64 = { 0, long.MinValue, 1, unchecked((long)0x8000000000000001UL), 0x7fefffffffffffff, 0x7ff0000000000000,
            unchecked((long)0xfff0000000000000UL), 0x7ff0000000000001, unchecked((long)0xfff123456789abcdUL), 0x7ff8123456789abc };
        foreach (int bits in patterns32)
        {
            Equal(f.__wasm_bits_i32_reinterpret_f32(f.__wasm_bits_f32_reinterpret_i32(bits)),bits,"f32 reinterpret");
            Equal(f.host32(bits),bits,"f32 host result"); Equal(observed32,bits,"f32 host argument");
            Equal(f.__wasm_bits_f32_abs(bits),bits & int.MaxValue,"f32 abs");
            Equal(f.__wasm_bits_f32_neg(bits),bits ^ int.MinValue,"f32 neg");
            Equal(f.__wasm_bits_f32_copysign(bits,int.MinValue),bits | int.MinValue,"f32 copysign");
            Equal(f.__wasm_bits_state32(bits),bits,"f32 local"); Equal(f.__wasm_bits_g32,bits,"f32 global");
            f.__wasm_bits_store32(1,bits); Equal(f.__wasm_bits_load32(1),bits,"f32 unaligned memory");
            byte[] memory=f.ReadMemory(4,4);
            for (int i=0;i<4;i++) Equal(memory[i],unchecked((byte)((uint)bits>>(8*i))),"f32 little endian");
        }
        foreach (long bits in patterns64)
        {
            Equal(f.__wasm_bits_i64_reinterpret_f64(f.__wasm_bits_f64_reinterpret_i64(bits)),bits,"f64 reinterpret");
            Equal(f.host64(bits),bits,"f64 host result"); Equal(observed64,bits,"f64 host argument");
            Equal(f.__wasm_bits_f64_abs(bits),bits & long.MaxValue,"f64 abs");
            Equal(f.__wasm_bits_f64_neg(bits),bits ^ long.MinValue,"f64 neg");
            Equal(f.__wasm_bits_f64_copysign(bits,long.MinValue),bits | long.MinValue,"f64 copysign");
            Equal(f.__wasm_bits_state64(bits),bits,"f64 local"); Equal(f.__wasm_bits_g64,bits,"f64 global");
            f.__wasm_bits_store64(1,bits); Equal(f.__wasm_bits_load64(1),bits,"f64 unaligned memory");
            byte[] memory=f.ReadMemory(4,8);
            for (int i=0;i<8;i++) Equal(memory[i],unchecked((byte)((ulong)bits>>(8*i))),"f64 little endian");
        }
        Equal(f.__wasm_bits_constant32(),unchecked((int)0xff812345),"f32 constant");
        Equal(f.__wasm_bits_constant64(),unchecked((long)0xfff0000000012345UL),"f64 constant");
        Equal(B32(f.zero32()),0,"f32 default local"); Equal(B64(f.zero64()),0,"f64 default local");
        var pair=f.__wasm_bits_mixed(unchecked((int)0xff800001),unchecked((long)0xfff0000000000001UL));
        Equal(pair.Item1,unchecked((int)0xff800001),"mixed f32"); Equal(pair.Item2,unchecked((long)0xfff0000000000001UL),"mixed f64");
        Equal(B32(f.precision32()),0,"f32 intermediate precision");
        Equal(B32(f.f32_add(F32(1),F32(1))),2,"f32 subnormal add");
        Equal(B64(f.f64_add(F64(1),F64(1))),2,"f64 subnormal add");
        Equal(B32(f.f32_mul(F32(0x00800000),0.5f)),0x00400000,"f32 subnormal multiply");
        Equal(B64(f.f64_mul(F64(0x0010000000000000),0.5)),0x0008000000000000,"f64 subnormal multiply");
        Equal(B32(f.f32_div(1,F32(int.MinValue))),unchecked((int)0xff800000),"f32 division by zero");
        Equal(B64(f.f64_div(1,F64(long.MinValue))),unchecked((long)0xfff0000000000000UL),"f64 division by zero");
        if (!float.IsNaN(f.f32_div(0,0)) || !double.IsNaN(f.f64_sqrt(-1))) throw new Exception("Arithmetic NaN missing");
        Equal(B32(f.f32_min(0,F32(int.MinValue))),int.MinValue,"f32 min zero");
        Equal(B32(f.f32_max(F32(int.MinValue),0)),0,"f32 max zero");
        Equal(B64(f.f64_min(F64(long.MinValue),0)),long.MinValue,"f64 min zero");
        Equal(B64(f.f64_max(0,F64(long.MinValue))),0,"f64 max zero");
        if (!float.IsNaN(f.f32_min(1,F32(0x7f800001))) || !double.IsNaN(f.f64_max(F64(0x7ff0000000000001),1))) throw new Exception("min/max NaN missing");
        Equal(B32(f.f32_nearest(-0.5f)),int.MinValue,"f32 nearest negative zero");
        Equal(B64(f.f64_nearest(-0.5)),long.MinValue,"f64 nearest negative zero");
        Equal(B32(f.f32_nearest(-2.5f)),unchecked((int)0xc0000000),"f32 nearest even");
        Equal(B64(f.f64_nearest(-3.5)),unchecked((long)0xc010000000000000UL),"f64 nearest even");
        Equal(B32(f.f32_ceil(-0.25f)),int.MinValue,"f32 ceil"); Equal(B64(f.f64_trunc(-0.25)),long.MinValue,"f64 trunc");
        Equal(B32(f.f32_floor(-0.25f)),unchecked((int)0xbf800000),"f32 floor"); Equal(B64(f.f64_ceil(-0.25)),long.MinValue,"f64 ceil");
        Equal(B32(f.f32_sqrt(4)),0x40000000,"f32 sqrt"); Equal(B64(f.f64_sqrt(4)),0x4000000000000000,"f64 sqrt");
        Equal(f.f32_eq(F32(int.MinValue),0),1,"f32 compare zeros"); Equal(f.f64_ne(double.NaN,1),1,"f64 compare NaN");
        Equal(f.f32_lt(float.NaN,0),0,"f32 ordered compare"); Equal(f.f64_ge(double.NaN,0),0,"f64 ordered compare");
        Equal(B32(f.f32_convert_i64_s(18014399583223809L)),0x5a800001,"i64 to f32 above midpoint");
        Equal(B32(f.f32_convert_i64_s(-18014399583223809L)),unchecked((int)0xda800001),"negative i64 to f32 midpoint");
        Equal(B32(f.f32_convert_i64_u(unchecked((long)0x8000008000000001UL))),0x5f000001,"u64 to f32 midpoint");
        Equal(B64(f.f64_convert_i64_u(unchecked((long)0x8000000000000401UL))),0x43e0000000000001,"u64 to f64 midpoint");
        Equal(B32(f.f32_convert_i64_u(-1)),0x5f800000,"u64 to f32");
        Equal(B64(f.f64_convert_i64_u(-1)),0x43f0000000000000,"u64 to f64");
        Equal(B32(f.f32_convert_i32_u(-1)),0x4f800000,"u32 to f32");
        Equal(B64(f.f64_convert_i32_s(-1)),unchecked((long)0xbff0000000000000UL),"i32 to f64");
        Equal(B32(f.f32_demote_f64(F64(0x3ff0000010000000))),0x3f800000,"demote tie");
        Equal(B64(f.f64_promote_f32(F32(1))),0x36a0000000000000,"promote subnormal");
        Equal(f.i32_trunc_f64_s(-2147483648.75),int.MinValue,"signed lower fractional limit");
        Equal(f.i32_trunc_f64_u(-0.999),0,"unsigned negative fraction");
        Equal(f.i32_trunc_f64_u(4294967295.75),-1,"u32 upper fraction");
        Equal(f.i64_trunc_f64_s(F64(0x43dfffffffffffff)),9223372036854774784L,"i64 upper limit");
        Equal(f.i64_trunc_f64_u(F64(0x43efffffffffffff)),-2048,"u64 upper limit");
        Trap(()=>f.i32_trunc_f32_s(float.NaN),Floating.TrapKind.InvalidConversionToInteger);
        Trap(()=>f.i64_trunc_f64_u(double.NaN),Floating.TrapKind.InvalidConversionToInteger);
        Trap(()=>f.i32_trunc_f64_s(double.PositiveInfinity),Floating.TrapKind.IntegerOverflow);
        Trap(()=>f.i64_trunc_f64_s(F64(0x43e0000000000000)),Floating.TrapKind.IntegerOverflow);
        Trap(()=>f.i64_trunc_f64_u(F64(0x43f0000000000000)),Floating.TrapKind.IntegerOverflow);
        Equal(f.i32_trunc_sat_f32_s(float.NaN),0,"sat f32 NaN"); Equal(f.i64_trunc_sat_f64_u(double.NaN),0,"sat f64 NaN");
        Equal(f.i32_trunc_sat_f32_u(float.PositiveInfinity),-1,"sat u32 infinity");
        Equal(f.i32_trunc_sat_f64_s(double.NegativeInfinity),int.MinValue,"sat i32 infinity");
        Equal(f.i32_trunc_sat_f64_u(-1),0,"sat u32 negative");
        Equal(f.i64_trunc_sat_f32_s(float.PositiveInfinity),long.MaxValue,"sat i64 infinity");
        Equal(f.i64_trunc_sat_f32_u(float.PositiveInfinity),-1,"sat u64 infinity");
        Equal(f.i64_trunc_sat_f64_s(double.NegativeInfinity),long.MinValue,"sat i64 negative");
        Trap(()=>f.store64(65529,1),Floating.TrapKind.MemoryOutOfBounds);
        Trap(()=>f.offset32(1),Floating.TrapKind.MemoryOutOfBounds);
        Trap(()=>f.load32(-1),Floating.TrapKind.MemoryOutOfBounds);
    }
}
