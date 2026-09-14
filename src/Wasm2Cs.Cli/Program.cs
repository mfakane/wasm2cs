using System.Text;
using Wasm2Cs;

if (args.Length == 0)
{
    Usage();
    return 2;
}

string? inputPath = null, outputDirectory = null, className = null;
var targetProfile = WasmTargetProfile.PortableNetStandard20;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--target-profile" or "-p")
    {
        if (++i >= args.Length || !WasmTargetProfiles.TryParse(args[i], out targetProfile))
        {
            Console.Error.WriteLine("Unknown or missing target profile.");
            Usage();
            return 2;
        }
    }
    else if (args[i] is "--class-name" or "-n")
    {
        if (++i >= args.Length) { Console.Error.WriteLine("Missing class name."); Usage(); return 2; }
        className = args[i];
    }
    else if (args[i] is "--output-directory" or "-o")
    {
        if (++i >= args.Length) { Console.Error.WriteLine("Missing output directory."); Usage(); return 2; }
        outputDirectory = args[i];
    }
    else if (args[i].StartsWith("-", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Unknown option '{args[i]}'.");
        Usage();
        return 2;
    }
    else if (inputPath is not null)
    {
        Console.Error.WriteLine("Only one input module is supported.");
        Usage();
        return 2;
    }
    else inputPath = args[i];
}

if (inputPath is null)
{
    Console.Error.WriteLine("Missing input module.");
    Usage();
    return 2;
}

className ??= Path.GetFileNameWithoutExtension(inputPath);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (outputDirectory is null)
    {
        var sources = Transpiler.TranslateSources(File.ReadAllBytes(inputPath), className, targetProfile, cancellation.Token);
        Console.Write(string.Concat(sources.Select(source => source.Text)));
    }
    else
    {
        var sources = Transpiler.TranslateSources(File.ReadAllBytes(inputPath), className, targetProfile, cancellation.Token);
        Directory.CreateDirectory(outputDirectory);
        foreach (var path in Directory.EnumerateFiles(outputDirectory, className + ".g.cs")
            .Concat(Directory.EnumerateFiles(outputDirectory, className + ".Functions.*.g.cs")))
            File.Delete(path);
        foreach (var source in sources)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(outputDirectory, source.Name), source.Text, new UTF8Encoding(false));
        }
    }
    return 0;
}
catch (Exception e) when (e is WasmException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Translation canceled.");
    return 1;
}

static void Usage() => Console.Error.WriteLine("Usage: wasm2cs <module.wasm> [--class-name <name>] [--output-directory <dir>] [--target-profile <profile>]");
