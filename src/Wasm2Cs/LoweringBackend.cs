namespace Wasm2Cs;

internal enum CanonicalVectorOperation
{
    AddF32x4,
    MultiplyF32x4
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
    public override string VectorTypeName => "global::System.Runtime.Intrinsics.Vector128<float>";

    public override bool TryLowerVectorConstant(byte[] bytes, out VectorLowering? lowering)
    {
        if (bytes.Length != 16)
        {
            lowering = null;
            return false;
        }
        string lanes = string.Join(", ", Enumerable.Range(0, 4).Select(i =>
            $"global::System.BitConverter.Int32BitsToSingle(unchecked((int)0x{Bits(bytes, i * 4):x8}U))"));
        lowering = new VectorLowering(VectorTypeName, $"global::System.Runtime.Intrinsics.Vector128.Create({lanes})");
        return true;
    }

    public override bool TryLowerVector(CanonicalVectorOperation operation, string left, string right,
        out VectorLowering? lowering)
    {
        string? laneOperation = operation switch
        {
            CanonicalVectorOperation.AddF32x4 => "+",
            CanonicalVectorOperation.MultiplyF32x4 => "*",
            _ => null
        };
        if (laneOperation is null)
        {
            lowering = null;
            return false;
        }
        string lanes = string.Join(", ", Enumerable.Range(0, 4).Select(i =>
            $"global::System.Runtime.Intrinsics.Vector128.GetElement({left}, {i}) {laneOperation} " +
            $"global::System.Runtime.Intrinsics.Vector128.GetElement({right}, {i})"));
        lowering = new VectorLowering(VectorTypeName,
            $"global::System.Runtime.Intrinsics.Vector128.Create({lanes})");
        return true;
    }

    private static uint Bits(byte[] bytes, int offset) => (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
}

internal sealed class UnityMathematicsLoweringBackend(WasmTargetProfile profile) : PortableLoweringBackend(profile)
{
    public override string VectorTypeName => "global::Unity.Mathematics.float4";

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
            _ => null
        };
        lowering = expression is null ? null : new VectorLowering("global::Unity.Mathematics.float4", expression);
        return lowering is not null;
    }

    private static uint Bits(byte[] bytes, int offset) => (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
}
