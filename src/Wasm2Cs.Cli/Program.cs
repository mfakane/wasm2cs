using Wasm2Cs;

if (args.Length != 1 && args.Length != 3 ||
    args.Length == 3 && (args[1] is not "--target-profile" and not "-p"))
{
    Console.Error.WriteLine("Usage: wasm2cs <module.wasm> [--target-profile <profile>] (writes C# to stdout)");
    return 2;
}
var targetProfile = WasmTargetProfile.PortableNetStandard20;
if (args.Length == 3 && !WasmTargetProfiles.TryParse(args[2], out targetProfile))
{
    Console.Error.WriteLine($"Unknown target profile '{args[2]}'.");
    return 2;
}
try
{
    Console.Write(Transpiler.Translate(File.ReadAllBytes(args[0]), Path.GetFileNameWithoutExtension(args[0]), targetProfile));
    return 0;
}
catch (Exception e) when (e is WasmException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
