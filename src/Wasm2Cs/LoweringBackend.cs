namespace Wasm2Cs;

internal enum CanonicalVectorOperation
{
    AddF32x4,
    MultiplyF32x4,
    AndV128,
    BitselectV128,
    AddI32x4,
    MultiplyI32x4,
    LtF32x4,
    PminF32x4,
    MaxUI8x16,
    NarrowI16x8U,
    NarrowI32x4U,
    ExtendLowI8x16U,
    ExtendLowI16x8U,
    SplatI32x4,
    SplatF32x4,
    ExtractLaneI32x4,
    ExtractLaneUI8x16,
    ShuffleI8x16
}

internal sealed record VectorLowering(string TypeName, string Expression);

// Backends select target APIs after validation has established the WASM
// operation and its type. The default implementation deliberately delegates
// to the existing scalar helpers until a replacement has an explicit semantic
// check and a fallback.
internal class LoweringBackend(WasmTargetProfile profile)
{
    protected WasmTargetProfile Profile { get; } = profile;

    public virtual string MemoryHelpers(Module module) =>
        MemoryOperations.Helpers(Profile, module.Memory?.Maximum ?? 65536);

    public virtual string I32Expression(byte opcode, string left, string right) =>
        I32Operations.Expression(opcode, left, right);

    public virtual string I64Expression(byte opcode, string left, string right) =>
        I64Operations.Expression(opcode, left, right);

    public virtual string FloatExpression(byte opcode, string left, string right) =>
        FloatOperations.Expression(opcode, left, right);

    public virtual string ConversionExpression(byte opcode, int subopcode, string value) =>
        ConversionOperations.Expression(opcode, subopcode, value);

    public virtual string Expression(CanonicalOperation operation, string left, string right) => operation.Kind switch
    {
        CanonicalOperationKind.I32 => I32Expression(operation.Opcode, left, right),
        CanonicalOperationKind.I64 => I64Expression(operation.Opcode, left, right),
        CanonicalOperationKind.Float => FloatExpression(operation.Opcode, left, right),
        CanonicalOperationKind.Conversion => ConversionExpression(operation.Opcode, operation.Operand, left),
        _ => throw new WasmException("Unknown canonical operation.")
    };

    public virtual string ExtraHelpers(Module module) => string.Empty;

    public virtual bool TryLowerVector(CanonicalVectorOperation operation, string left, string right,
        out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }

    public virtual bool TryLowerVectorUnary(CanonicalVectorOperation operation, string value,
        out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }

    public virtual bool TryLowerVectorTernary(CanonicalVectorOperation operation, string a, string b, string c,
        out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }

    public virtual bool TryLowerVectorSplat(CanonicalVectorOperation operation, string scalar,
        out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }

    public virtual bool TryLowerVectorExtract(CanonicalVectorOperation operation, string vector, int lane,
        out string? expression)
    {
        expression = null;
        return false;
    }

    public virtual bool TryLowerVectorShuffle(string a, string b, byte[] lanes, out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }

    public virtual bool TryLowerVectorLoad(bool load32Zero, string address, uint offset, out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }

    public virtual bool TryLowerVectorStore(string address, string value, uint offset, out string? statement)
    {
        statement = null;
        return false;
    }

    public virtual string VectorTypeName => throw new WasmException(
        $"Target profile '{WasmTargetProfiles.Name(Profile)}' does not support v128 lowering.");

    public virtual bool TryLowerVectorConstant(byte[] bytes, out VectorLowering? lowering)
    {
        lowering = null;
        return false;
    }
}

internal class PortableLoweringBackend(WasmTargetProfile profile) : LoweringBackend(profile);

internal class DotNetNetStandard21LoweringBackend(WasmTargetProfile profile) : PortableLoweringBackend(profile)
{
    public override string FloatExpression(byte opcode, string left, string right)
    {
        if (opcode is >= 0x8d and <= 0x91 && FloatOperations.InputType(opcode) == ValueType.F32)
            return opcode == 0x91
                ? $"__wasm_Sqrt32({left}.Value)"
                : $"__wasm_Round32({left}.Value, {opcode - 0x8d})";
        return base.FloatExpression(opcode, left, right);
    }

    public override string ExtraHelpers(Module module) => """
    private static __wasm_Float32 __wasm_Round32(float value, int mode)
    {
        if (float.IsNaN(value)) return __wasm_F32(0x7fc00000);
        float rounded = mode == 0 ? global::System.MathF.Ceiling(value) :
            mode == 1 ? global::System.MathF.Floor(value) : mode == 2 ? global::System.MathF.Truncate(value) :
            global::System.MathF.Round(value, global::System.MidpointRounding.ToEven);
        return rounded == 0 ? __wasm_F32(__wasm_Bits32(__wasm_FromF32(value)) & int.MinValue) : __wasm_FromF32(rounded);
    }
    private static __wasm_Float32 __wasm_Sqrt32(float value)
    {
        if (float.IsNaN(value)) return __wasm_F32(0x7fc00000);
        return __wasm_FromF32(global::System.MathF.Sqrt(value));
    }

""";
}

// This derives from the .NET Standard 2.1 backend so future Vector128
// lowering inherits the safe scalar fallback for operations without a vector
// implementation yet.
internal sealed class DotNetVectorLoweringBackend(WasmTargetProfile profile) : DotNetNetStandard21LoweringBackend(profile)
{
    private const string V = "global::System.Runtime.Intrinsics.Vector128";
    private const string VT = "global::System.Runtime.Intrinsics.Vector128<float>";

    public override string VectorTypeName => VT;

    public override string I32Expression(byte opcode, string left, string right)
    {
        return opcode switch
        {
            0x67 => $"global::System.Numerics.BitOperations.LeadingZeroCount(unchecked((uint){left}))",
            0x68 => $"global::System.Numerics.BitOperations.TrailingZeroCount(unchecked((uint){left}))",
            0x69 => $"global::System.Numerics.BitOperations.PopCount(unchecked((uint){left}))",
            _ => base.I32Expression(opcode, left, right)
        };
    }

    public override string I64Expression(byte opcode, string left, string right)
    {
        return opcode switch
        {
            0x79 => $"(long)global::System.Numerics.BitOperations.LeadingZeroCount(unchecked((ulong){left}))",
            0x7a => $"(long)global::System.Numerics.BitOperations.TrailingZeroCount(unchecked((ulong){left}))",
            0x7b => $"(long)global::System.Numerics.BitOperations.PopCount(unchecked((ulong){left}))",
            _ => base.I64Expression(opcode, left, right)
        };
    }

    public override string ExtraHelpers(Module module)
    {
        string helpers = module.HasFloatHelpers ? base.ExtraHelpers(module) : "";
        helpers += $$"""
    private static {{VT}} __wasm_V128FromBytes(byte[] bytes)
    {
        return {{V}}.Create(
            global::System.BitConverter.ToSingle(bytes, 0),
            global::System.BitConverter.ToSingle(bytes, 4),
            global::System.BitConverter.ToSingle(bytes, 8),
            global::System.BitConverter.ToSingle(bytes, 12));
    }
    private static void __wasm_V128ToBytes({{VT}} value, byte[] bytes)
    {
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 0, 4), {{V}}.GetElement(value, 0));
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 4, 4), {{V}}.GetElement(value, 1));
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 8, 4), {{V}}.GetElement(value, 2));
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 12, 4), {{V}}.GetElement(value, 3));
    }
""";
        if (module.Memory != null)
        {
            helpers += "\n";
            helpers += $$"""
    private {{VT}} __wasm_V128Load(int address, uint offset, int width)
    {
        int index = __wasm_Address(address, offset, width);
        byte[] bytes = new byte[16];
        __wasm_memory.ReadMemory(unchecked((uint)index), bytes, 0, width);
        return __wasm_V128FromBytes(bytes);
    }
    private void __wasm_V128Store(int address, {{VT}} value, uint offset)
    {
        int index = __wasm_Address(address, offset, 16);
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(value, bytes);
        __wasm_memory.WriteMemory(unchecked((uint)index), bytes);
    }
""";
        }
        helpers += "\n";
        helpers += $$"""
    private static {{VT}} __wasm_V128Shuffle({{VT}} a, {{VT}} b, byte[] lanes)
    {
        byte[] source = new byte[32];
        __wasm_V128ToBytes(a, source);
        byte[] high = new byte[16];
        __wasm_V128ToBytes(b, high);
        global::System.Buffer.BlockCopy(high, 0, source, 16, 16);
        byte[] result = new byte[16];
        for (int i = 0; i < 16; i++) result[i] = source[lanes[i]];
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128NarrowI16x8U({{VT}} a, {{VT}} b)
    {
        short[] source = new short[16];
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(a, bytes);
        for (int i = 0; i < 8; i++) source[i] = global::System.BitConverter.ToInt16(bytes, i * 2);
        __wasm_V128ToBytes(b, bytes);
        for (int i = 0; i < 8; i++) source[8 + i] = global::System.BitConverter.ToInt16(bytes, i * 2);
        byte[] result = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            int v = source[i];
            if (v < 0) v = 0; else if (v > 255) v = 255;
            result[i] = unchecked((byte)v);
        }
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128NarrowI32x4U({{VT}} a, {{VT}} b)
    {
        int[] source = new int[8];
        for (int i = 0; i < 4; i++) source[i] = global::System.BitConverter.SingleToInt32Bits({{V}}.GetElement(a, i));
        for (int i = 0; i < 4; i++) source[4 + i] = global::System.BitConverter.SingleToInt32Bits({{V}}.GetElement(b, i));
        byte[] result = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            int v = source[i];
            if (v < 0) v = 0; else if (v > 65535) v = 65535;
            result[i * 2] = unchecked((byte)v);
            result[i * 2 + 1] = unchecked((byte)(v >> 8));
        }
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128ExtendLowI8x16U({{VT}} value)
    {
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(value, bytes);
        byte[] result = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            result[i * 2] = bytes[i];
            result[i * 2 + 1] = 0;
        }
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128ExtendLowI16x8U({{VT}} value)
    {
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(value, bytes);
        return {{V}}.Create(
            global::System.BitConverter.Int32BitsToSingle(bytes[0] | (bytes[1] << 8)),
            global::System.BitConverter.Int32BitsToSingle(bytes[2] | (bytes[3] << 8)),
            global::System.BitConverter.Int32BitsToSingle(bytes[4] | (bytes[5] << 8)),
            global::System.BitConverter.Int32BitsToSingle(bytes[6] | (bytes[7] << 8)));
    }
    private static {{VT}} __wasm_V128MaxUI8x16({{VT}} a, {{VT}} b)
    {
        byte[] left = new byte[16], right = new byte[16], result = new byte[16];
        __wasm_V128ToBytes(a, left);
        __wasm_V128ToBytes(b, right);
        for (int i = 0; i < 16; i++) result[i] = left[i] >= right[i] ? left[i] : right[i];
        return __wasm_V128FromBytes(result);
    }

""";
        return helpers;
    }

    public override bool TryLowerVectorConstant(byte[] bytes, out VectorLowering? lowering)
    {
        if (bytes.Length != 16)
        {
            lowering = null;
            return false;
        }
        string lanes = string.Join(", ", Enumerable.Range(0, 4).Select(i =>
            $"global::System.BitConverter.Int32BitsToSingle(unchecked((int)0x{Bits(bytes, i * 4):x8}U))"));
        lowering = new VectorLowering(VectorTypeName, $"{V}.Create({lanes})");
        return true;
    }

    public override bool TryLowerVector(CanonicalVectorOperation operation, string left, string right,
        out VectorLowering? lowering)
    {
        string? expression = operation switch
        {
            CanonicalVectorOperation.AddF32x4 => CreateF32(left, right, "+"),
            CanonicalVectorOperation.MultiplyF32x4 => CreateF32(left, right, "*"),
            CanonicalVectorOperation.AndV128 =>
                $"{V}.AsSingle({V}.BitwiseAnd({V}.AsUInt32({left}), {V}.AsUInt32({right})))",
            CanonicalVectorOperation.AddI32x4 =>
                $"{V}.AsSingle({V}.AsInt32({left}) + {V}.AsInt32({right}))",
            CanonicalVectorOperation.MultiplyI32x4 =>
                $"{V}.AsSingle({V}.AsInt32({left}) * {V}.AsInt32({right}))",
            CanonicalVectorOperation.LtF32x4 =>
                $"{V}.LessThan({left}, {right})",
            CanonicalVectorOperation.PminF32x4 =>
                $"{V}.ConditionalSelect({V}.LessThan({right}, {left}), {right}, {left})",
            CanonicalVectorOperation.MaxUI8x16 => $"__wasm_V128MaxUI8x16({left}, {right})",
            CanonicalVectorOperation.NarrowI16x8U => $"__wasm_V128NarrowI16x8U({left}, {right})",
            CanonicalVectorOperation.NarrowI32x4U => $"__wasm_V128NarrowI32x4U({left}, {right})",
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering(VectorTypeName, expression);
        return lowering is not null;
    }

    public override bool TryLowerVectorUnary(CanonicalVectorOperation operation, string value,
        out VectorLowering? lowering)
    {
        string? expression = operation switch
        {
            CanonicalVectorOperation.ExtendLowI8x16U => $"__wasm_V128ExtendLowI8x16U({value})",
            CanonicalVectorOperation.ExtendLowI16x8U => $"__wasm_V128ExtendLowI16x8U({value})",
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering(VectorTypeName, expression);
        return lowering is not null;
    }

    public override bool TryLowerVectorTernary(CanonicalVectorOperation operation, string a, string b, string c,
        out VectorLowering? lowering)
    {
        if (operation != CanonicalVectorOperation.BitselectV128)
        {
            lowering = null;
            return false;
        }
        lowering = new VectorLowering(VectorTypeName,
            $"{V}.AsSingle({V}.ConditionalSelect({V}.AsByte({c}), {V}.AsByte({a}), {V}.AsByte({b})))");
        return true;
    }

    public override bool TryLowerVectorSplat(CanonicalVectorOperation operation, string scalar,
        out VectorLowering? lowering)
    {
        string? expression = operation switch
        {
            CanonicalVectorOperation.SplatI32x4 =>
                $"{V}.Create(global::System.BitConverter.Int32BitsToSingle({scalar}))",
            CanonicalVectorOperation.SplatF32x4 => $"{V}.Create(({scalar}).Value)",
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering(VectorTypeName, expression);
        return lowering is not null;
    }

    public override bool TryLowerVectorExtract(CanonicalVectorOperation operation, string vector, int lane,
        out string? expression)
    {
        expression = operation switch
        {
            CanonicalVectorOperation.ExtractLaneI32x4 =>
                $"global::System.BitConverter.SingleToInt32Bits({V}.GetElement({vector}, {lane}))",
            CanonicalVectorOperation.ExtractLaneUI8x16 =>
                $"({V}.GetElement({V}.AsByte({vector}), {lane}))",
            _ => null
        };
        return expression is not null;
    }

    public override bool TryLowerVectorShuffle(string a, string b, byte[] lanes, out VectorLowering? lowering)
    {
        string laneArray = "new byte[] { " + string.Join(", ", lanes.Select(l => l.ToString())) + " }";
        lowering = new VectorLowering(VectorTypeName, $"__wasm_V128Shuffle({a}, {b}, {laneArray})");
        return true;
    }

    public override bool TryLowerVectorLoad(bool load32Zero, string address, uint offset, out VectorLowering? lowering)
    {
        int width = load32Zero ? 4 : 16;
        lowering = new VectorLowering(VectorTypeName, $"__wasm_V128Load({address}, {offset}u, {width})");
        return true;
    }

    public override bool TryLowerVectorStore(string address, string value, uint offset, out string? statement)
    {
        statement = $"__wasm_V128Store({address}, {value}, {offset}u)";
        return true;
    }

    private string CreateF32(string left, string right, string op)
    {
        string lanes = string.Join(", ", Enumerable.Range(0, 4).Select(i =>
            $"{V}.GetElement({left}, {i}) {op} {V}.GetElement({right}, {i})"));
        return $"{V}.Create({lanes})";
    }

    private static uint Bits(byte[] bytes, int offset) => unchecked((uint)(bytes[offset] | (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24)));
}

internal sealed class UnityMathematicsLoweringBackend(WasmTargetProfile profile) : PortableLoweringBackend(profile)
{
    private const string VT = "global::Unity.Mathematics.float4";

    public override string VectorTypeName => VT;

    public override string ExtraHelpers(Module module)
    {
        string helpers = $$"""
    private static {{VT}} __wasm_V128FromBytes(byte[] bytes)
    {
        return new {{VT}}(
            global::Unity.Mathematics.math.asfloat(global::System.BitConverter.ToInt32(bytes, 0)),
            global::Unity.Mathematics.math.asfloat(global::System.BitConverter.ToInt32(bytes, 4)),
            global::Unity.Mathematics.math.asfloat(global::System.BitConverter.ToInt32(bytes, 8)),
            global::Unity.Mathematics.math.asfloat(global::System.BitConverter.ToInt32(bytes, 12)));
    }
    private static void __wasm_V128ToBytes({{VT}} value, byte[] bytes)
    {
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 0, 4), global::Unity.Mathematics.math.asint(value.x));
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 4, 4), global::Unity.Mathematics.math.asint(value.y));
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 8, 4), global::Unity.Mathematics.math.asint(value.z));
        global::System.BitConverter.TryWriteBytes(new global::System.Span<byte>(bytes, 12, 4), global::Unity.Mathematics.math.asint(value.w));
    }
""";
        if (module.Memory != null)
        {
            helpers += "\n";
            helpers += $$"""
    private {{VT}} __wasm_V128Load(int address, uint offset, int width)
    {
        int index = __wasm_Address(address, offset, width);
        byte[] bytes = new byte[16];
        __wasm_memory.ReadMemory(unchecked((uint)index), bytes, 0, width);
        return __wasm_V128FromBytes(bytes);
    }
    private void __wasm_V128Store(int address, {{VT}} value, uint offset)
    {
        int index = __wasm_Address(address, offset, 16);
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(value, bytes);
        __wasm_memory.WriteMemory(unchecked((uint)index), bytes);
    }
""";
        }
        helpers += "\n";
        helpers += $$"""
    private static {{VT}} __wasm_V128Shuffle({{VT}} a, {{VT}} b, byte[] lanes)
    {
        byte[] source = new byte[32];
        __wasm_V128ToBytes(a, source);
        byte[] high = new byte[16];
        __wasm_V128ToBytes(b, high);
        global::System.Buffer.BlockCopy(high, 0, source, 16, 16);
        byte[] result = new byte[16];
        for (int i = 0; i < 16; i++) result[i] = source[lanes[i]];
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128NarrowI16x8U({{VT}} a, {{VT}} b)
    {
        short[] source = new short[16];
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(a, bytes);
        for (int i = 0; i < 8; i++) source[i] = global::System.BitConverter.ToInt16(bytes, i * 2);
        __wasm_V128ToBytes(b, bytes);
        for (int i = 0; i < 8; i++) source[8 + i] = global::System.BitConverter.ToInt16(bytes, i * 2);
        byte[] result = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            int v = source[i];
            if (v < 0) v = 0; else if (v > 255) v = 255;
            result[i] = unchecked((byte)v);
        }
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128NarrowI32x4U({{VT}} a, {{VT}} b)
    {
        int[] source = new int[] {
            global::Unity.Mathematics.math.asint(a.x), global::Unity.Mathematics.math.asint(a.y),
            global::Unity.Mathematics.math.asint(a.z), global::Unity.Mathematics.math.asint(a.w),
            global::Unity.Mathematics.math.asint(b.x), global::Unity.Mathematics.math.asint(b.y),
            global::Unity.Mathematics.math.asint(b.z), global::Unity.Mathematics.math.asint(b.w) };
        byte[] result = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            int v = source[i];
            if (v < 0) v = 0; else if (v > 65535) v = 65535;
            result[i * 2] = unchecked((byte)v);
            result[i * 2 + 1] = unchecked((byte)(v >> 8));
        }
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128ExtendLowI8x16U({{VT}} value)
    {
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(value, bytes);
        byte[] result = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            result[i * 2] = bytes[i];
            result[i * 2 + 1] = 0;
        }
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128ExtendLowI16x8U({{VT}} value)
    {
        byte[] bytes = new byte[16];
        __wasm_V128ToBytes(value, bytes);
        return new {{VT}}(
            global::Unity.Mathematics.math.asfloat(bytes[0] | (bytes[1] << 8)),
            global::Unity.Mathematics.math.asfloat(bytes[2] | (bytes[3] << 8)),
            global::Unity.Mathematics.math.asfloat(bytes[4] | (bytes[5] << 8)),
            global::Unity.Mathematics.math.asfloat(bytes[6] | (bytes[7] << 8)));
    }
    private static {{VT}} __wasm_V128MaxUI8x16({{VT}} a, {{VT}} b)
    {
        byte[] left = new byte[16], right = new byte[16], result = new byte[16];
        __wasm_V128ToBytes(a, left);
        __wasm_V128ToBytes(b, right);
        for (int i = 0; i < 16; i++) result[i] = left[i] >= right[i] ? left[i] : right[i];
        return __wasm_V128FromBytes(result);
    }
    private static {{VT}} __wasm_V128And({{VT}} a, {{VT}} b)
    {
        return global::Unity.Mathematics.math.asfloat(global::Unity.Mathematics.math.asuint(a) & global::Unity.Mathematics.math.asuint(b));
    }
    private static {{VT}} __wasm_V128Bitselect({{VT}} a, {{VT}} b, {{VT}} c)
    {
        global::Unity.Mathematics.uint4 bits = (global::Unity.Mathematics.math.asuint(a) & global::Unity.Mathematics.math.asuint(c)) |
            (global::Unity.Mathematics.math.asuint(b) & ~global::Unity.Mathematics.math.asuint(c));
        return global::Unity.Mathematics.math.asfloat(bits);
    }
    private static {{VT}} __wasm_V128AddI32x4({{VT}} a, {{VT}} b)
    {
        return global::Unity.Mathematics.math.asfloat(global::Unity.Mathematics.math.asint(a) + global::Unity.Mathematics.math.asint(b));
    }
    private static {{VT}} __wasm_V128MulI32x4({{VT}} a, {{VT}} b)
    {
        return global::Unity.Mathematics.math.asfloat(global::Unity.Mathematics.math.asint(a) * global::Unity.Mathematics.math.asint(b));
    }
    private static {{VT}} __wasm_V128LtF32x4({{VT}} a, {{VT}} b)
    {
        global::Unity.Mathematics.bool4 mask = a < b;
        return new {{VT}}(
            mask.x ? global::Unity.Mathematics.math.asfloat(unchecked((int)0xffffffff)) : 0f,
            mask.y ? global::Unity.Mathematics.math.asfloat(unchecked((int)0xffffffff)) : 0f,
            mask.z ? global::Unity.Mathematics.math.asfloat(unchecked((int)0xffffffff)) : 0f,
            mask.w ? global::Unity.Mathematics.math.asfloat(unchecked((int)0xffffffff)) : 0f);
    }
    private static {{VT}} __wasm_V128PminF32x4({{VT}} a, {{VT}} b)
    {
        return new {{VT}}(b.x < a.x ? b.x : a.x, b.y < a.y ? b.y : a.y, b.z < a.z ? b.z : a.z, b.w < a.w ? b.w : a.w);
    }

""";
        return helpers;
    }

    public override bool TryLowerVectorConstant(byte[] bytes, out VectorLowering? lowering)
    {
        if (bytes.Length != 16)
        {
            lowering = null;
            return false;
        }
        string lanes = string.Join(", ", Enumerable.Range(0, 4).Select(i =>
            $"global::Unity.Mathematics.math.asfloat(0x{Bits(bytes, i * 4):x8}u)"));
        lowering = new VectorLowering(VectorTypeName, $"new {VectorTypeName}({lanes})");
        return true;
    }

    public override bool TryLowerVector(CanonicalVectorOperation operation, string left, string right,
        out VectorLowering? lowering)
    {
        string? expression = operation switch
        {
            CanonicalVectorOperation.AddF32x4 => $"{left} + {right}",
            CanonicalVectorOperation.MultiplyF32x4 => $"{left} * {right}",
            CanonicalVectorOperation.AndV128 => $"__wasm_V128And({left}, {right})",
            CanonicalVectorOperation.AddI32x4 => $"__wasm_V128AddI32x4({left}, {right})",
            CanonicalVectorOperation.MultiplyI32x4 => $"__wasm_V128MulI32x4({left}, {right})",
            CanonicalVectorOperation.LtF32x4 => $"__wasm_V128LtF32x4({left}, {right})",
            CanonicalVectorOperation.PminF32x4 => $"__wasm_V128PminF32x4({left}, {right})",
            CanonicalVectorOperation.MaxUI8x16 => $"__wasm_V128MaxUI8x16({left}, {right})",
            CanonicalVectorOperation.NarrowI16x8U => $"__wasm_V128NarrowI16x8U({left}, {right})",
            CanonicalVectorOperation.NarrowI32x4U => $"__wasm_V128NarrowI32x4U({left}, {right})",
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering(VectorTypeName, expression);
        return lowering is not null;
    }

    public override bool TryLowerVectorUnary(CanonicalVectorOperation operation, string value,
        out VectorLowering? lowering)
    {
        string? expression = operation switch
        {
            CanonicalVectorOperation.ExtendLowI8x16U => $"__wasm_V128ExtendLowI8x16U({value})",
            CanonicalVectorOperation.ExtendLowI16x8U => $"__wasm_V128ExtendLowI16x8U({value})",
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering(VectorTypeName, expression);
        return lowering is not null;
    }

    public override bool TryLowerVectorTernary(CanonicalVectorOperation operation, string a, string b, string c,
        out VectorLowering? lowering)
    {
        if (operation != CanonicalVectorOperation.BitselectV128)
        {
            lowering = null;
            return false;
        }
        lowering = new VectorLowering(VectorTypeName, $"__wasm_V128Bitselect({a}, {b}, {c})");
        return true;
    }

    public override bool TryLowerVectorSplat(CanonicalVectorOperation operation, string scalar,
        out VectorLowering? lowering)
    {
        string? expression = operation switch
        {
            CanonicalVectorOperation.SplatI32x4 =>
                $"new {VT}(global::Unity.Mathematics.math.asfloat({scalar}))",
            CanonicalVectorOperation.SplatF32x4 => $"new {VT}(({scalar}).Value)",
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering(VectorTypeName, expression);
        return lowering is not null;
    }

    public override bool TryLowerVectorExtract(CanonicalVectorOperation operation, string vector, int lane,
        out string? expression)
    {
        string[] components = ["x", "y", "z", "w"];
        if (operation == CanonicalVectorOperation.ExtractLaneI32x4 && lane is >= 0 and <= 3)
        {
            expression = $"global::Unity.Mathematics.math.asint({vector}.{components[lane]})";
            return true;
        }
        if (operation == CanonicalVectorOperation.ExtractLaneUI8x16 && lane is >= 0 and <= 15)
        {
            int word = lane / 4;
            int shift = (lane % 4) * 8;
            expression = $"((global::Unity.Mathematics.math.asint({vector}.{components[word]}) >> {shift}) & 255)";
            return true;
        }
        expression = null;
        return false;
    }

    public override bool TryLowerVectorShuffle(string a, string b, byte[] lanes, out VectorLowering? lowering)
    {
        string laneArray = "new byte[] { " + string.Join(", ", lanes.Select(l => l.ToString())) + " }";
        lowering = new VectorLowering(VectorTypeName, $"__wasm_V128Shuffle({a}, {b}, {laneArray})");
        return true;
    }

    public override bool TryLowerVectorLoad(bool load32Zero, string address, uint offset, out VectorLowering? lowering)
    {
        int width = load32Zero ? 4 : 16;
        lowering = new VectorLowering(VectorTypeName, $"__wasm_V128Load({address}, {offset}u, {width})");
        return true;
    }

    public override bool TryLowerVectorStore(string address, string value, uint offset, out string? statement)
    {
        statement = $"__wasm_V128Store({address}, {value}, {offset}u)";
        return true;
    }

    private static uint Bits(byte[] bytes, int offset) => unchecked((uint)(bytes[offset] | (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24)));
}
