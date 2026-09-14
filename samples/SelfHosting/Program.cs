using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Wasm2Cs;

return 0;

// The JavaScript file is only the official browser-wasm host. The conversion
// itself happens here, inside the managed guest assembly.
[SupportedOSPlatform("browser")]
public static partial class SelfHostingDriver
{
    private static IEnumerator<GeneratedSource>? sourceEnumerator;

    [JSExport]
    internal static string Hello() => "Hello, World!";

    [JSExport]
    internal static string TranslateBase64(string wasmBase64, string className)
    {
        try
        {
            return Translate(Convert.FromBase64String(wasmBase64), className);
        }
        catch (Exception exception)
        {
            return $"ERROR: {exception.GetType().FullName}: {exception.Message}";
        }
    }

    internal static string Translate(byte[] wasm, string className)
    {
        try
        {
            return "OK\n" + Transpiler.Translate(wasm, className);
        }
        catch (Exception exception)
        {
            return $"ERROR: {exception.GetType().FullName}: {exception.Message}";
        }
    }

    [JSExport]
    internal static string TranslateSourcesBase64(string wasmBase64, string className)
    {
        try
        {
            var sources = Transpiler.TranslateSources(Convert.FromBase64String(wasmBase64), className);
            return JsonSerializer.Serialize(sources);
        }
        catch (Exception exception)
        {
            return $"ERROR: {exception.GetType().FullName}: {exception.Message}";
        }
    }

    [JSExport]
    internal static string BeginTranslateSourcesBase64(string wasmBase64, string className)
    {
        try
        {
            sourceEnumerator?.Dispose();
            sourceEnumerator = Transpiler.TranslateSourceChunkSequence(Convert.FromBase64String(wasmBase64), className).GetEnumerator();
            return "OK";
        }
        catch (Exception exception)
        {
            sourceEnumerator = null;
            return $"ERROR: {exception.GetType().FullName}: {exception.Message}";
        }
    }

    [JSExport]
    internal static string NextTranslateSource()
    {
        try
        {
            if (sourceEnumerator is null) return "ERROR: no active source translation";
            if (!sourceEnumerator.MoveNext())
            {
                sourceEnumerator.Dispose();
                sourceEnumerator = null;
                return string.Empty;
            }
            return sourceEnumerator.Current.Text;
        }
        catch (Exception exception)
        {
            sourceEnumerator?.Dispose();
            sourceEnumerator = null;
            return $"ERROR: {exception.GetType().FullName}: {exception.Message}";
        }
    }

    [JSExport]
    internal static string CurrentTranslateSourceName() => sourceEnumerator?.Current.Name ?? string.Empty;
}
