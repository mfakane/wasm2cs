namespace Wasm2Cs;

// Opcode metadata for the T04/T05 SIMD group (Clang Simd.wasm) plus the three
// already-supported vector ops. Decode/validate/lower consult this table.
internal enum SimdOpKind
{
    Const,
    Binary,
    Unary,
    Ternary,
    SplatI32,
    SplatF32,
    ExtractI32,
    Shuffle,
    Load,
    Load32Zero,
    Store
}

internal static class SimdOperations
{
    public static bool TryDescribe(int opcode, out CanonicalVectorOperation? vectorOp, out SimdOpKind kind,
        out int naturalAlignLog2, out int maxLane)
    {
        vectorOp = null;
        naturalAlignLog2 = -1;
        maxLane = -1;
        switch (opcode)
        {
            case 0:
                kind = SimdOpKind.Load; naturalAlignLog2 = 4; return true;
            case 11:
                kind = SimdOpKind.Store; naturalAlignLog2 = 4; return true;
            case 12:
                kind = SimdOpKind.Const; return true;
            case 13:
                kind = SimdOpKind.Shuffle; vectorOp = CanonicalVectorOperation.ShuffleI8x16; return true;
            case 17:
                kind = SimdOpKind.SplatI32; vectorOp = CanonicalVectorOperation.SplatI32x4; return true;
            case 19:
                kind = SimdOpKind.SplatF32; vectorOp = CanonicalVectorOperation.SplatF32x4; return true;
            case 22:
                kind = SimdOpKind.ExtractI32; vectorOp = CanonicalVectorOperation.ExtractLaneUI8x16; maxLane = 15; return true;
            case 27:
                kind = SimdOpKind.ExtractI32; vectorOp = CanonicalVectorOperation.ExtractLaneI32x4; maxLane = 3; return true;
            case 67:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.LtF32x4; return true;
            case 78:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.AndV128; return true;
            case 82:
                kind = SimdOpKind.Ternary; vectorOp = CanonicalVectorOperation.BitselectV128; return true;
            case 92:
                kind = SimdOpKind.Load32Zero; naturalAlignLog2 = 2; return true;
            case 102:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.NarrowI16x8U; return true;
            case 121:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.MaxUI8x16; return true;
            case 134:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.NarrowI32x4U; return true;
            case 137:
                kind = SimdOpKind.Unary; vectorOp = CanonicalVectorOperation.ExtendLowI8x16U; return true;
            case 169:
                kind = SimdOpKind.Unary; vectorOp = CanonicalVectorOperation.ExtendLowI16x8U; return true;
            case 174:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.AddI32x4; return true;
            case 181:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.MultiplyI32x4; return true;
            case 228:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.AddF32x4; return true;
            case 230:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.MultiplyF32x4; return true;
            case 234:
                kind = SimdOpKind.Binary; vectorOp = CanonicalVectorOperation.PminF32x4; return true;
            default:
                kind = default;
                return false;
        }
    }
}
