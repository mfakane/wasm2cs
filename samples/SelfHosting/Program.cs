using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Wasm2Cs;

#pragma warning disable CA1416

return SelfHostingDriver.RunManagedEntry();

// The JavaScript file is only the official browser-wasm host. The conversion
// itself happens here, inside the managed guest assembly.
[SupportedOSPlatform("browser")]
public static partial class SelfHostingDriver
{
    private static IEnumerator<GeneratedSource>? sourceEnumerator;
    private static string activeSourceName = "";
    private static string? pendingSourceText;
    private static int pendingSourceOffset;
    private static string lastManagedProbe = "";

    // SH-10 invokes this managed method through Mono's exported method bridge.
    // The top-level entry point and the bridge share the same managed body.
    [JSExport]
    internal static int RunManagedMain() => RunManagedEntry();

    [JSExport]
    internal static int RunManagedProbe()
    {
        try
        {
            lastManagedProbe = ManagedProbes.Run();
            Console.WriteLine("SH11_PROBE:" + lastManagedProbe);
            Console.Error.WriteLine("SH11_STDERR");
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    [JSExport]
    internal static string GetManagedProbeResult() => lastManagedProbe;

    [JSExport]
    internal static int ReenterManagedProbe() => ManagedProbes.Reenter();

    [JSExport]
    internal static int ThrowManagedProbe() => throw new InvalidOperationException("SH-11 deliberate uncaught exception");

    internal static int RunManagedEntry()
    {
        try
        {
            Console.WriteLine("Hello, World!");
            Console.WriteLine(40 + 2);
            return 0;
        }
        catch
        {
            return -1;
        }
    }

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
            activeSourceName = "";
            pendingSourceText = null;
            pendingSourceOffset = 0;
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
            if (pendingSourceText is null || pendingSourceOffset >= pendingSourceText.Length)
            {
                if (sourceEnumerator is null) return "ERROR: no active source translation";
                if (!sourceEnumerator.MoveNext())
                {
                    sourceEnumerator.Dispose();
                    sourceEnumerator = null;
                    pendingSourceText = null;
                    return string.Empty;
                }
                pendingSourceText = sourceEnumerator.Current.Text;
                activeSourceName = sourceEnumerator.Current.Name;
                pendingSourceOffset = 0;
            }
            // JSExport truncates a large return on some runtimes. Keep each return short.
            int length = Math.Min(1024, pendingSourceText.Length - pendingSourceOffset);
            string slice = pendingSourceText.Substring(pendingSourceOffset, length);
            pendingSourceOffset += length;
            return slice;
        }
        catch (Exception exception)
        {
            sourceEnumerator?.Dispose();
            sourceEnumerator = null;
            pendingSourceText = null;
            return $"ERROR: {exception.GetType().FullName}: {exception.Message}";
        }
    }

    [JSExport]
    internal static string CurrentTranslateSourceName() => activeSourceName;
}
