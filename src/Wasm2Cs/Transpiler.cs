namespace Wasm2Cs;
public sealed class WasmException(string message) : Exception(message);
public static class Transpiler
{
    public static string Translate(byte[] wasm, string className)
    {
        var module = Decoder.Decode(wasm);
        Validator.Validate(module, className);
        return CSharpEmitter.Emit(module, className);
    }
}
