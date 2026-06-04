#:property TargetFramework=net10.0
#:property LangVersion=14

using System.Diagnostics;
using System.Runtime.InteropServices;

var context = PublishContext.Create(PublishOptions.Parse(args));
var cliPath = await new CliPublisher(context).PublishAsync();
Console.WriteLine(cliPath);

internal sealed class CliPublisher(PublishContext context)
{
    public async Task<string> PublishAsync()
    {
        if (Directory.Exists(context.OutputPath))
        {
            Directory.Delete(context.OutputPath, true);
        }

        Directory.CreateDirectory(context.OutputPath);

        await DotNet.PublishCliAsync(
            context.ProjectPath,
            context.Platform.RuntimeIdentifier,
            context.OutputPath);

        return Path.Combine(context.OutputPath, context.Platform.ExecutableName("fgvm"));
    }
}

internal sealed record PublishContext(string RepoRoot, string OutputRoot, BuildPlatform Platform)
{
    public string OutputPath => Path.Combine(OutputRoot, Platform.Name);

    public string ProjectPath => Path.Combine(RepoRoot, "Fgvm.Cli", "Fgvm.Cli.csproj");

    public static PublishContext Create(PublishOptions options)
    {
        var repoRoot = options.RepoRoot ?? RepositoryPaths.FindRoot();
        var outputRoot = options.OutputRoot ?? Path.Combine(repoRoot, ".fgvm-integration-cli");
        var platform = options.Platform is null ? BuildPlatform.Current() : BuildPlatform.Parse(options.Platform);

        return new PublishContext(repoRoot, outputRoot, platform);
    }
}

internal readonly record struct BuildPlatform(string Name)
{
    public string RuntimeIdentifier => Name.ToLowerInvariant() switch
    {
        "linux-x64" => "linux-x64",
        "linux-arm64" => "linux-arm64",
        "macos-x64" => "osx-x64",
        "macos-arm64" => "osx-arm64",
        "windows-x64" => "win-x64",
        "windows-arm64" => "win-arm64",
        _ => throw new InvalidOperationException($"Unsupported CLI publish platform '{Name}'.")
    };

    public string ExecutableName(string baseName) =>
        Name.StartsWith("windows-", StringComparison.OrdinalIgnoreCase) ? $"{baseName}.exe" : baseName;

    public static BuildPlatform Current()
    {
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("Only Windows, macOS, and Linux are supported for integration tests.");

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
        };

        return new BuildPlatform($"{os}-{arch}");
    }

    public static BuildPlatform Parse(string name)
    {
        var platform = new BuildPlatform(name);
        _ = platform.RuntimeIdentifier;
        return platform;
    }
}

internal static class DotNet
{
    public static Task PublishCliAsync(string projectPath, string rid, string outputPath) =>
        CommandRunner.RunAsync("dotnet",
        [
            "publish",
            projectPath,
            "--nologo",
            "-c",
            "Debug",
            "-r",
            rid,
            "-o",
            outputPath
        ]);
}

internal static class RepositoryPaths
{
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fgvm.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}

internal static class CommandRunner
{
    public static async Task RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{fileName} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }
}

internal sealed record PublishOptions(string? RepoRoot, string? OutputRoot, string? Platform)
{
    public static PublishOptions Parse(string[] args)
    {
        string? repoRoot = null;
        string? outputRoot = null;
        string? platform = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i);
                    break;
                case "--output":
                    outputRoot = RequireValue(args, ref i);
                    break;
                case "--platform":
                    platform = RequireValue(args, ref i);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return new PublishOptions(repoRoot, outputRoot, platform);
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for '{args[index]}'.");
        }

        return args[++index];
    }
}
