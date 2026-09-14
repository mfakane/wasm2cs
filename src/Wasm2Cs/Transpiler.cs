namespace Wasm2Cs;
public sealed class WasmException(string message) : Exception(message);
public sealed record GeneratedSource(string Name, string Text);
public static class Transpiler
{
    public static string Translate(byte[] wasm, string className)
        => Translate(wasm, className, WasmTargetProfile.PortableNetStandard20);

    public static string Translate(byte[] wasm, string className, WasmTargetProfile profile)
        => string.Concat(TranslateSourceSequence(wasm, className, profile).Select(source => source.Text));

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className)
        => TranslateSources(wasm, className, WasmTargetProfile.PortableNetStandard20, default);

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className, WasmTargetProfile profile)
        => TranslateSources(wasm, className, profile, default);

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className, CancellationToken cancellationToken)
        => TranslateSources(wasm, className, WasmTargetProfile.PortableNetStandard20, cancellationToken);

    public static IReadOnlyList<GeneratedSource> TranslateSources(byte[] wasm, string className,
        WasmTargetProfile profile, CancellationToken cancellationToken)
        => TranslateSourceSequence(wasm, className, profile, cancellationToken).ToArray();

    public static IEnumerable<GeneratedSource> TranslateSourceSequence(byte[] wasm, string className,
        WasmTargetProfile profile = WasmTargetProfile.PortableNetStandard20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = Decoder.Decode(wasm, cancellationToken);
        Validator.Validate(module, className, cancellationToken);
        var lowering = Lowering.Create(module, profile);
        int index = 0;
        foreach (var text in CSharpEmitter.EmitSourceSequence(module, className, lowering, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GeneratedSource(index == 0 ? $"{className}.g.cs" : $"{className}.Functions.{index - 1:D4}.g.cs", text);
            index++;
        }
    }
}
