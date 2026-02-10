using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Fgvm.Error;

namespace Fgvm.Tests.EndToEnd;

public class TestContainerFixture : IAsyncLifetime
{
    private IContainer? _container;
    private string? _publishContainerPath;
    private string? _publishHostPath;
    private string? _rid;

    public IContainer Container => _container ?? throw new InvalidOperationException("Container not initialized");
    public string Rid => _rid ?? throw new InvalidOperationException("RID not initialized");
    public string FgvmPath => Path.Combine(_publishContainerPath ?? throw new InvalidOperationException("Fgvm publish path not initialized"), "fgvm");

    public async Task InitializeAsync()
    {
        // Find the solution root directory by looking for the .sln file
        var currentDir = Directory.GetCurrentDirectory();
        var solutionDir = currentDir;
        while (!string.IsNullOrEmpty(solutionDir) && !File.Exists(Path.Combine(solutionDir, "Fgvm.slnx")))
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }

        if (string.IsNullOrEmpty(solutionDir))
        {
            throw new InvalidOperationException("Could not find Fgvm.sln to determine solution root");
        }

        _container = new ContainerBuilder("mcr.microsoft.com/dotnet/sdk:10.0")
            .WithWorkingDirectory("/workspace")
            .WithBindMount(solutionDir, "/workspace")
            .WithCommand("tail", "-f", "/dev/null") // Keep container running
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("dotnet", "--version"))
            .Build();

        await _container.StartAsync();

        // Detect container architecture to determine RID
        var archResult = await _container.ExecAsync(["uname", "-m"]);
        if (archResult.ExitCode != ExitCodes.Success)
        {
            throw new InvalidOperationException("Failed to detect container architecture");
        }

        var arch = archResult.Stdout.Trim();
        _rid = arch switch
        {
            "x86_64" => "linux-x64",
            "aarch64" => "linux-arm64",
            _ => throw new InvalidOperationException($"Unsupported architecture: {arch}")
        };

        var publishDirName = Guid.NewGuid().ToString("N");
        _publishHostPath = Path.Combine(solutionDir, ".fgvm-publish", publishDirName);
        _publishContainerPath = $"/workspace/.fgvm-publish/{publishDirName}";
        Directory.CreateDirectory(_publishHostPath);

        var publishResult =
            await _container.ExecAsync(["dotnet", "publish", "/workspace/Fgvm.Cli/Fgvm.Cli.csproj", "-c", "Debug", "-r", _rid, "-o", _publishContainerPath]);

        if (publishResult.ExitCode != ExitCodes.Success)
        {
            throw new InvalidOperationException(
                $"Failed to publish Fgvm. Exit code: {publishResult.ExitCode}\nStdout: {publishResult.Stdout}\nStderr: {publishResult.Stderr}");
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }

        if (_publishHostPath is not null)
        {
            Directory.CreateDirectory(_publishHostPath);
            Directory.Delete(_publishHostPath, true);
        }
    }
}
