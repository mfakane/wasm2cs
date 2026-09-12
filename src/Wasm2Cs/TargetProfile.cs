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

internal sealed class LoweringPlan(WasmTargetProfile profile)
{
    public WasmTargetProfile Profile { get; } = profile;

    public string MemoryHelpers(Module module)
    {
        if (module.Memory == null) throw new WasmException("Memory lowering requires a memory.");
        return MemoryOperations.Helpers(Profile, module.Memory.Maximum ?? 65536);
    }
}

internal static class Lowering
{
    public static LoweringPlan Create(Module module, WasmTargetProfile profile)
    {
        if (!Enum.IsDefined(typeof(WasmTargetProfile), profile))
            throw new WasmException($"Unknown WASM target profile {profile}.");
        return new LoweringPlan(profile);
    }
}
