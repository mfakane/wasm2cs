using Wasm2Cs;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: wasm2cs <module.wasm> (writes C# to stdout)");
    return 2;
}
try
{
    Console.Write(Transpiler.Translate(File.ReadAllBytes(args[0]), Path.GetFileNameWithoutExtension(args[0])));
    return 0;
}
catch (Exception e) when (e is WasmException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
