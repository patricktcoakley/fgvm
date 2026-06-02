using System.Diagnostics;
using System.Runtime.InteropServices;
using Fgvm.Error;

namespace Fgvm.Tests.Integration;

public sealed class TestFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string> _baseEnvironment = new(StringComparer.Ordinal);
    private readonly HashSet<string> _installedVersionsCache = new(StringComparer.OrdinalIgnoreCase);
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
        BinPath = Path.Combine(RootPath, "bin");
        ShimPath = OperatingSystem.IsWindows()
            ? Path.Combine(BinPath, "godot.cmd")
            : Path.Combine(BinPath, "godot");
        SelectedExecutablePath = OperatingSystem.IsWindows()
            ? Path.Combine(RootPath, "Godot.exe")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(RootPath, "Godot.app")
                : Path.Combine(RootPath, "Godot");
    }

    public string FgvmPath => _fgvmPath ?? throw new InvalidOperationException("Native fixture was not initialized.");
    public string FixtureManifestPath => _fixtureManifestPath ?? throw new InvalidOperationException("Native fixture manifest was not initialized.");
    public string HomePath { get; }
    public string RootPath { get; }
    public string TempPath { get; }
    public string BinPath { get; }
    public string ShimPath { get; }
    public string SelectedExecutablePath { get; }
    public bool SupportsSymlinkAssertions => !OperatingSystem.IsWindows();
    public bool IsArm64 => CurrentPlatform().EndsWith("-arm64", StringComparison.Ordinal);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(HomePath);
        Directory.CreateDirectory(TempPath);

        var platform = CurrentPlatform();
        _fgvmPath = System.Environment.GetEnvironmentVariable("FGVM_INTEGRATION_CLI_PATH")
                    ?? Path.Combine(_repoRoot, ".fgvm-integration-cli", platform, ExecutableName("fgvm"));
        _fixtureManifestPath = System.Environment.GetEnvironmentVariable("FGVM_INTEGRATION_FIXTURE_MANIFEST")
                               ?? Path.Combine(_repoRoot, ".fgvm-integration-fixtures", platform, "manifest.json");

        if (!File.Exists(_fgvmPath))
        {
            throw new InvalidOperationException(
                $"Native CLI publish was not found at '{_fgvmPath}'. Run `mise run integration:prepare:native` before direct `dotnet test`.");
        }

        if (!File.Exists(_fixtureManifestPath))
        {
            throw new InvalidOperationException(
                $"Native fixture manifest was not found at '{_fixtureManifestPath}'. Run `mise run integration:prepare:native` before direct `dotnet test`.");
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

    public async Task<CommandResult> ExecuteCommandInDirectory(string workingDirectory, string[] args) =>
        await ExecuteProcess(FgvmPath, args, workingDirectory, null);

    public async Task<CommandResult> ExecuteCommandWithEnvironment(string[] args, IReadOnlyDictionary<string, string> environment) =>
        await ExecuteProcess(FgvmPath, args, null, environment);

    public async Task<CommandResult> ExecuteGodotShim(string[] args)
    {
        var currentPath = System.Environment.GetEnvironmentVariable("PATH");
        var path = string.IsNullOrEmpty(currentPath)
            ? BinPath
            : $"{BinPath}{Path.PathSeparator}{currentPath}";
        var launcher = OperatingSystem.IsWindows() ? "cmd.exe" : "env";
        string[] launcherArgs = OperatingSystem.IsWindows()
            ? ["/c", "godot", .. args]
            : ["godot", .. args];

        return await ExecuteProcess(launcher, launcherArgs, null, new Dictionary<string, string>
        {
            ["PATH"] = path
        });
    }

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

    public Task<bool> PathIsSymlink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
    }

    public Task<string?> ReadLink(string path)
    {
        if (File.Exists(path))
        {
            return Task.FromResult(new FileInfo(path).LinkTarget);
        }

        return Task.FromResult(Directory.Exists(path)
            ? new DirectoryInfo(path).LinkTarget
            : null);
    }

    public async Task<string> ReadFile(string path) => await File.ReadAllTextAsync(path);

    public async Task WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    public async Task EnsureVersionInstalled(string version)
    {
        if (_installedVersionsCache.Contains(version))
        {
            return;
        }

        if (!await HasVersionInstalled(version))
        {
            var installResult = await ExecuteCommand(["install", version]);
            await AssertSuccessfulExecutionAsync(installResult, "install");
        }

        _installedVersionsCache.Add(version);
    }

    public void MarkVersionUninstalled(string version) =>
        _installedVersionsCache.Remove(version);

    public async Task<string> GetLogs()
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

    public async Task AssertLogContains(string expectedText)
    {
        var logs = await GetLogs();
        Assert.Contains(expectedText, logs, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> HasVersionInstalled(string version)
    {
        var result = await ExecuteCommand(["list"]);
        return result.ExitCode == ExitCodes.Success && result.Stdout.Contains(version, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetCurrentVersion()
    {
        var result = await ExecuteCommand(["which"]);
        return result.ExitCode == ExitCodes.Success ? result.Stdout.Trim() : string.Empty;
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
