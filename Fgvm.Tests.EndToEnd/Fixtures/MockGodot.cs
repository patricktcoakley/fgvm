#:property TargetFramework=net10.0
#:property LangVersion=14

const string Version = "4.6.2.stable.standard.mock";

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

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(Version);
    return 0;
}

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Mock Godot");
    Console.WriteLine("Usage: godot [options]");
    return 0;
}

Console.WriteLine(args.Length == 0
    ? "Mock Godot launched."
    : $"Mock Godot launched with: {string.Join(' ', args)}");

return 0;
