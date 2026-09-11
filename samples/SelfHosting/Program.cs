using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Wasm2Cs;

return 0;

// The JavaScript file is only the official browser-wasm host. The conversion
// itself happens here, inside the managed guest assembly.
[SupportedOSPlatform("browser")]
public static partial class SelfHostingDriver
{
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
}
