using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Wasm2Cs;

[Generator]
public sealed class WasmGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidModule = new(
        "WASM001", "WASM translation failed", "{0}: {1}", "Wasm2Cs", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".wasm.base64", StringComparison.OrdinalIgnoreCase))
            .Select((file, cancellationToken) => (file.Path, Text: file.GetText(cancellationToken)?.ToString()));
        context.RegisterSourceOutput(inputs, (output, input) =>
        {
            string name = Path.GetFileName(input.Path)[..^".wasm.base64".Length];
            try
            {
                if (input.Text is null) throw new WasmException("Cannot read WASM intermediate input.");
                string source = Transpiler.Translate(Convert.FromBase64String(input.Text), name);
                output.AddSource(name + ".g.cs", SourceText.From(source, Encoding.UTF8));
            }
            catch (Exception e) when (e is WasmException or FormatException)
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidModule, Location.None, input.Path, e.Message));
            }
        });
    }
}
