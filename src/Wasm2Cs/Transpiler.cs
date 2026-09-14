namespace Wasm2Cs;
public sealed class WasmException(string message) : Exception(message);
public sealed record GeneratedSource(string Name, string Text);
public static class Transpiler
{
    public static string Translate(byte[] wasm, string className)
        => Translate(wasm, className, WasmTargetProfile.PortableNetStandard20);

    public static string Translate(byte[] wasm, string className, WasmTargetProfile profile)
    {
        return string.Concat(TranslateSources(wasm, className, profile).Select(source => source.Text));
    }

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className)
        => TranslateSources(wasm, className, WasmTargetProfile.PortableNetStandard20, default);

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className, WasmTargetProfile profile)
        => TranslateSources(wasm, className, profile, default);

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className, CancellationToken cancellationToken)
        => TranslateSources(wasm, className, WasmTargetProfile.PortableNetStandard20, cancellationToken);

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className,
        WasmTargetProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = Decoder.Decode(wasm, cancellationToken);
        Validator.Validate(module, className, cancellationToken);
        var lowering = Lowering.Create(module, profile);
        var texts = CSharpEmitter.EmitSources(module, className, lowering, cancellationToken);
        var sources = new List<GeneratedSource>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sources.Add(new GeneratedSource(i == 0 ? $"{className}.g.cs" : $"{className}.Functions.{i - 1:D4}.g.cs", texts[i]));
        }
        return sources;
    }
}
