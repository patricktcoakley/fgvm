#:property TargetFramework=net10.0
#:property LangVersion=14

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

var version = Assembly.GetExecutingAssembly()
                  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                  ?.InformationalVersion
              ?? throw new InvalidOperationException("Mock Godot version metadata is missing.");

if (System.Environment.GetEnvironmentVariable("FGVM_MOCK_INVOCATION_PATH") is { Length: > 0 } invocationPath)
{
    var invocationDirectory = Path.GetDirectoryName(Path.GetFullPath(invocationPath));
    if (invocationDirectory is not null)
    {
        Directory.CreateDirectory(invocationDirectory);
    }

    var tempPath = $"{invocationPath}.{Guid.NewGuid():N}.tmp";
    File.WriteAllText(tempPath, JsonSerializer.Serialize(new
    {
        ProcessId = System.Environment.ProcessId,
        Arguments = args,
        BaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        WorkingDirectory = Directory.GetCurrentDirectory()
    }));
    File.Move(tempPath, invocationPath, true);
}

if (args.Contains("--fgvm-mock-invalid-arg", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Mock Godot invalid argument.");
    return 2;
}

if (args.Contains("--fgvm-mock-fail", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Mock Godot failure.");
    return 42;
}

if (args.Contains("--fgvm-mock-print-directory", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return 0;
}

var delayArgumentIndex = Array.FindIndex(
    args,
    argument => string.Equals(argument, "--fgvm-mock-delay-ms", StringComparison.OrdinalIgnoreCase));
if (delayArgumentIndex >= 0)
{
    if (delayArgumentIndex + 1 >= args.Length ||
        !int.TryParse(args[delayArgumentIndex + 1], out var delayMilliseconds) ||
        delayMilliseconds < 0)
    {
        Console.Error.WriteLine("Mock Godot requires a non-negative delay in milliseconds.");
        return 2;
    }

    await Task.Delay(delayMilliseconds);
    return 0;
}

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(version);
    return 0;
}

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Mock Godot");
    Console.WriteLine("Usage: godot [options]");
    return 0;
}

var exportIndex = Array.FindIndex(
    args,
    argument => argument is "--export-release" or "--export-pack");
if (exportIndex >= 0)
{
    if (exportIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("Mock Godot export requires a preset and destination.");
        return 2;
    }

    var preset = args[exportIndex + 1];
    var destination = Path.GetFullPath(args[exportIndex + 2]);
    var destinationDirectory = Path.GetDirectoryName(destination)
                               ?? throw new InvalidOperationException("Mock export destination has no parent directory.");
    Directory.CreateDirectory(destinationDirectory);

    var extension = Path.GetExtension(destination).ToLowerInvariant();
    switch (extension)
    {
        case ".app":
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "mock-game"), preset);
            break;
        case ".zip":
            using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("mock-game.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(preset);
            }

            break;
        default:
            File.WriteAllText(destination, preset);
            if (extension is ".html")
            {
                File.WriteAllText(Path.ChangeExtension(destination, ".wasm"), "wasm");
            }

            break;
    }
}

Console.WriteLine(args.Length == 0
    ? "Mock Godot launched."
    : $"Mock Godot launched with: {string.Join(' ', args)}");

return 0;
