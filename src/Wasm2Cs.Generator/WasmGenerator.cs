using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Wasm2Cs;

[Generator]
public sealed class WasmGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidModule = new(
        "WASM001", "WASM translation failed", "{0}: {1}", "Wasm2Cs", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateModule = new(
        "WASM002", "Duplicate WASM module", "{0}: {1}", "Wasm2Cs", DiagnosticSeverity.Error, true);
    private const string UnitySuffix = ".Wasm2Cs.Generator.additionalfile";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".wasm.base64", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith(UnitySuffix, StringComparison.Ordinal))
            .Select((file, cancellationToken) => (file.Path, Text: file.GetText(cancellationToken)?.ToString()));
        var assemblyName = context.CompilationProvider.Select((compilation, _) => compilation.AssemblyName);
        context.RegisterSourceOutput(inputs.Combine(assemblyName), (output, combined) =>
        {
            var input = combined.Left;
            bool unity = input.Path.EndsWith(UnitySuffix, StringComparison.Ordinal);
            if (unity && combined.Right != "Wasm2Cs.Modules") return;
            string filename = Path.GetFileName(input.Path);
            string name = filename.Substring(0, filename.Length - (unity ? UnitySuffix.Length : ".wasm.base64".Length));
            try
            {
                if (input.Text is null) throw new WasmException("Cannot read WASM intermediate input.");
                if (input.Text.StartsWith("!WASM002:", StringComparison.Ordinal))
                {
                    output.ReportDiagnostic(Diagnostic.Create(DuplicateModule, Location.None, input.Path, input.Text.Substring(9)));
                    return;
                }
                if (input.Text.StartsWith("!WASM001:", StringComparison.Ordinal)) throw new WasmException(input.Text.Substring(9));
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
