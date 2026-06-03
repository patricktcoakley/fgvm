using System.Diagnostics;
using System.Runtime.InteropServices;
using Fgvm.Error;

namespace Fgvm.Tests.Integration;

public sealed class TestFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string> _baseEnvironment = new(StringComparer.Ordinal);
    private readonly string _repoRoot;
    private readonly string _testRoot;
    private string? _fgvmPath;
    private string? _fixtureManifestPath;

    public TestFixture()
    {
        _repoRoot = FindRepoRoot();
        _testRoot = Path.Combine(Path.GetTempPath(), "fgvm-integration", Guid.NewGuid().ToString("N"));
        HomePath = Path.Combine(_testRoot, "home");
        TempPath = Path.Combine(_testRoot, "tmp");
        RootPath = Path.Combine(HomePath, "fgvm");
    }

    private string FgvmPath => _fgvmPath ?? throw new InvalidOperationException("Native fixture was not initialized.");
    private string HomePath { get; }
    public string RootPath { get; }
    public string TempPath { get; }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(HomePath);
        Directory.CreateDirectory(TempPath);

        var platform = CurrentPlatform();
        _fgvmPath = System.Environment.GetEnvironmentVariable("FGVM_INTEGRATION_CLI_PATH")
                    ?? Path.Combine(_repoRoot, ".fgvm-integration-cli", platform, ExecutableName("fgvm"));
        _fixtureManifestPath = System.Environment.GetEnvironmentVariable("FGVM_INTEGRATION_FIXTURE_MANIFEST")
                               ?? Path.Combine(_repoRoot, "Fgvm.Tests.Integration", "Fixtures", "release-index-manifest.json");

        if (!File.Exists(_fgvmPath))
        {
            throw new InvalidOperationException(
                $"Native CLI publish was not found at '{_fgvmPath}'. Run `mise run integration:prepare:native` before direct `dotnet test`.");
        }

        if (!File.Exists(_fixtureManifestPath))
        {
            throw new InvalidOperationException(
                $"Integration fixture manifest was not found at '{_fixtureManifestPath}'.");
        }

        _baseEnvironment["FGVM_HOME"] = HomePath;
        _baseEnvironment["FGVM_INTEGRATION_FIXTURE_MANIFEST"] = _fixtureManifestPath;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }

        return Task.CompletedTask;
    }

    public async Task<CommandResult> ExecuteCommand(string[] args) =>
        await ExecuteProcess(FgvmPath, args, null, null);

    public async Task<CommandResult> ExecuteCommandWithEnvironment(string[] args, IReadOnlyDictionary<string, string> environment) =>
        await ExecuteProcess(FgvmPath, args, null, environment);

    public Task CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DirectoryExists(string path) => Task.FromResult(Directory.Exists(path));

    public Task<bool> FileExists(string path) => Task.FromResult(File.Exists(path));

    public async Task<string> ReadFile(string path) => await File.ReadAllTextAsync(path);

    public async Task WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<string> GetLogs()
    {
        var result = await ExecuteCommand(["logs"]);
        return result.Stdout;
    }

    public async Task AssertSuccessfulExecutionAsync(CommandResult result, string? expectedOutput = null)
    {
        if (result.ExitCode != ExitCodes.Success)
        {
            var logs = await GetLogs();
            result.AssertSuccessfulExecution(expectedOutput, [logs]);
        }
        else
        {
            result.AssertSuccessfulExecution(expectedOutput);
        }
    }

    private async Task<CommandResult> ExecuteProcess(string fileName,
        string[] args,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? _repoRoot
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in _baseEnvironment)
        {
            startInfo.Environment[key] = value;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"Command timed out: {fileName} {string.Join(' ', args)}");
        }

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fgvm.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string CurrentPlatform()
    {
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("Only Windows, macOS, and Linux are supported for native tests.");

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported test architecture: {RuntimeInformation.ProcessArchitecture}")
        };

        return $"{os}-{arch}";
    }

    private static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
}
