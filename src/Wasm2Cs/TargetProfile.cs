namespace Wasm2Cs;

// The profile selects the API surface used by generated C# code. It does not
// change WebAssembly validation or permit an operation with different semantics.
public enum WasmTargetProfile
{
    PortableNetStandard20,
    DotNetNetStandard21,
    DotNetVector,
    UnityMathematics
}

public static class WasmTargetProfiles
{
    public static string Name(WasmTargetProfile profile) => profile switch
    {
        WasmTargetProfile.PortableNetStandard20 => "portable-netstandard2.0",
        WasmTargetProfile.DotNetNetStandard21 => "dotnet-netstandard2.1",
        WasmTargetProfile.DotNetVector => "dotnet-vector",
        WasmTargetProfile.UnityMathematics => "unity-mathematics",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public static bool TryParse(string? value, out WasmTargetProfile profile)
    {
        string? normalized = value?.Trim().ToLowerInvariant();
        profile = normalized switch
        {
            null or "" or "portable-netstandard2.0" or "portablenetstandard20" => WasmTargetProfile.PortableNetStandard20,
            "dotnet-netstandard2.1" or "dotnetnetstandard21" => WasmTargetProfile.DotNetNetStandard21,
            "dotnet-vector" or "dotnetvector" => WasmTargetProfile.DotNetVector,
            "unity-mathematics" or "unitymathematics" => WasmTargetProfile.UnityMathematics,
            _ => default
        };
        return normalized == null || normalized.Length == 0 ||
            normalized == Name(profile) || normalized == profile.ToString().ToLowerInvariant();
    }
}

internal sealed class LoweringPlan(WasmTargetProfile profile, LoweringBackend backend)
{
    public WasmTargetProfile Profile { get; } = profile;
    public LoweringBackend Backend { get; } = backend;

    public string MemoryHelpers(Module module) => Backend.MemoryHelpers(module);
    public string I32Expression(byte opcode, string left, string right) => Backend.I32Expression(opcode, left, right);
    public string I64Expression(byte opcode, string left, string right) => Backend.I64Expression(opcode, left, right);
    public string FloatExpression(byte opcode, string left, string right) => Backend.FloatExpression(opcode, left, right);
    public string ConversionExpression(byte opcode, int subopcode, string value) => Backend.ConversionExpression(opcode, subopcode, value);
    public string Expression(CanonicalOperation operation, string left, string right) => Backend.Expression(operation, left, right);
    public string ExtraHelpers(Module module) => Backend.ExtraHelpers(module);
    public bool TryLowerVector(CanonicalVectorOperation operation, string left, string right, out VectorLowering? lowering) =>
        Backend.TryLowerVector(operation, left, right, out lowering);
    public string VectorTypeName => Backend.VectorTypeName;
    public bool TryLowerVectorConstant(byte[] bytes, out VectorLowering? lowering) =>
        Backend.TryLowerVectorConstant(bytes, out lowering);
}

internal static class Lowering
{
    public static LoweringPlan Create(Module module, WasmTargetProfile profile)
    {
        if (!Enum.IsDefined(typeof(WasmTargetProfile), profile))
            throw new WasmException($"Unknown WASM target profile {profile}.");
        LoweringBackend backend = profile switch
        {
            WasmTargetProfile.PortableNetStandard20 => new PortableLoweringBackend(profile),
            WasmTargetProfile.DotNetNetStandard21 => new DotNetNetStandard21LoweringBackend(profile),
            WasmTargetProfile.DotNetVector => new DotNetVectorLoweringBackend(profile),
            WasmTargetProfile.UnityMathematics => new UnityMathematicsLoweringBackend(profile),
            _ => throw new WasmException($"Unknown WASM target profile {profile}.")
        };
        return new LoweringPlan(profile, backend);
    }
}
