#:property TargetFramework=net10.0
#:property LangVersion=14

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

const string delayArgument = "--fgvm-mock-delay-ms";
var effectiveArguments = new List<string>(args.Length);
int? delayMilliseconds = null;

for (var index = 0; index < args.Length; index++)
{
    if (!string.Equals(args[index], delayArgument, StringComparison.OrdinalIgnoreCase))
    {
        effectiveArguments.Add(args[index]);
        continue;
    }

    if (index + 1 >= args.Length ||
        !int.TryParse(args[++index], out var parsedDelayMilliseconds) ||
        parsedDelayMilliseconds < 0)
    {
        Console.Error.WriteLine("Mock Godot requires a non-negative delay in milliseconds.");
        return 2;
    }

    delayMilliseconds = parsedDelayMilliseconds;
}

args = [.. effectiveArguments];

var version = Assembly.GetExecutingAssembly()
                  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                  ?.InformationalVersion
              ?? throw new InvalidOperationException("Mock Godot version metadata is missing.");

var invocationPath = Path.Combine(AppContext.BaseDirectory, ".fgvm-mock-invocation.json");
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

if (args.Contains("--fgvm-mock-noisy-fail", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Mock Godot cause: the export template is missing.");
    for (var line = 1; line <= 40; line++)
    {
        Console.Error.WriteLine($"Mock Godot follow-up {line}.");
    }

    return 42;
}

if (args.Contains("--fgvm-mock-print-directory", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return 0;
}

// An editor normally remains alive after its launcher exits. Preserve that behavior
// long enough for detached-startup tests without adding explicit arguments to fgvm.
if (delayMilliseconds is null && args.Contains("--editor", StringComparer.OrdinalIgnoreCase))
{
    delayMilliseconds = 1000;
}

if (delayMilliseconds is not null)
{
    await Task.Delay(delayMilliseconds.Value);
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
