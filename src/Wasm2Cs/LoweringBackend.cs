namespace Wasm2Cs;

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

    public virtual string ExtraHelpers(Module module) => string.Empty;
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
internal sealed class DotNetVectorLoweringBackend(WasmTargetProfile profile) : DotNetNetStandard21LoweringBackend(profile);

internal sealed class UnityMathematicsLoweringBackend(WasmTargetProfile profile) : PortableLoweringBackend(profile);
