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
        => TranslateSourceSequenceCore(wasm, className, profile, cancellationToken, streamFunctions: false);

    // Streams function source parts under the same generated file name so a host
    // can avoid holding a large generated method in the browser-wasm guest.
    public static IEnumerable<GeneratedSource> TranslateSourceChunkSequence(byte[] wasm, string className,
        WasmTargetProfile profile = WasmTargetProfile.PortableNetStandard20, CancellationToken cancellationToken = default)
        => TranslateSourceSequenceCore(wasm, className, profile, cancellationToken, streamFunctions: true);

    private static IEnumerable<GeneratedSource> TranslateSourceSequenceCore(byte[] wasm, string className,
        WasmTargetProfile profile, CancellationToken cancellationToken, bool streamFunctions)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = Decoder.Decode(wasm, cancellationToken, retainBodies: false);
        wasm = Array.Empty<byte>();
        Validator.Validate(module, className, cancellationToken);
        var lowering = Lowering.Create(module, profile);
        foreach (var source in CSharpEmitter.EmitSourceSequence(module, className, lowering, cancellationToken, streamFunctions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return source;
        }
    }
}
