using System.Collections.Concurrent;
using Fgvm.Cli.Services;
using Fgvm.Types;

namespace Fgvm.Tests.Services;

public sealed class GodotLauncherTests
{
    [Fact]
    public void GodotLaunchTarget_FromResolution_AppliesExecutableOverride()
    {
        var resolution = new VersionResolutionOutcome.Found(
            "/install/Godot",
            "/install",
            "4.6-stable-standard",
            false,
            "4.6-stable-standard@linux.x86_64");

        var target = GodotLaunchTarget.FromResolution(resolution, "/override/MockGodot");

        Assert.Equal("/override/MockGodot", target.ExecutablePath);
        Assert.Equal(resolution.WorkingDirectory, target.WorkingDirectory);
        Assert.Equal(resolution.InstallationKey, target.InstallationKey);
        Assert.Equal(resolution.VersionName, target.VersionName);
    }

    [Fact]
    public void GodotLaunchTarget_FromResolution_RequiresInstallationKey()
    {
        var resolution = new VersionResolutionOutcome.Found(
            "/install/Godot",
            "/install",
            "4.6-stable-standard",
            false);

        Assert.Throws<InvalidOperationException>(() => GodotLaunchTarget.FromResolution(resolution));
    }

    [Fact]
    public async Task LaunchAsync_MissingExecutable_ReturnsStartFailure()
    {
        var launcher = new GodotLauncher();
        var request = CreateRequest(
            Path.Combine(Path.GetTempPath(), $"missing-godot-{Guid.NewGuid():N}"),
            "",
            GodotLaunchMode.Attached);

        var result = await launcher.LaunchAsync(request);

        var failure = Assert.IsType<Result<GodotLaunchOutcome, GodotLaunchError>.Failure>(result);
        var startFailed = Assert.IsType<GodotLaunchError.StartFailed>(failure.Error);
        Assert.Equal(request.Target.ExecutablePath, startFailed.ExecutablePath);
    }

    [Fact]
    public async Task LaunchAsync_Attached_ForwardsOutputAndReturnsExitCode()
    {
        var launcher = new GodotLauncher();
        var output = new ConcurrentQueue<GodotLaunchOutput>();
        var (executable, arguments) = ShellCommand(
            "printf 'hello\\n'; printf 'oops\\n' >&2; exit 7",
            "echo hello & echo oops 1>&2 & exit /b 7");
        var request = CreateRequest(executable, arguments, GodotLaunchMode.Attached);

        var result = await launcher.LaunchAsync(request, output.Enqueue);

        var success = Assert.IsType<Result<GodotLaunchOutcome, GodotLaunchError>.Success>(result);
        var exited = Assert.IsType<GodotLaunchOutcome.Exited>(success.Value);
        Assert.Equal(7, exited.ExitCode);
        Assert.Contains(output, item => item is GodotLaunchOutput.StandardOutput("hello"));
        Assert.Contains(output, item => item is GodotLaunchOutput.StandardError("oops"));
    }

    [Fact]
    public async Task LaunchAsync_Detached_ReturnsProcessId()
    {
        var launcher = new GodotLauncher();
        var (executable, arguments) = ShellCommand("exit 0", "exit /b 0");
        var request = CreateRequest(executable, arguments, GodotLaunchMode.Detached);

        var result = await launcher.LaunchAsync(request);

        var success = Assert.IsType<Result<GodotLaunchOutcome, GodotLaunchError>.Success>(result);
        var detached = Assert.IsType<GodotLaunchOutcome.Detached>(success.Value);
        Assert.True(detached.ProcessId > 0);
    }

    [Fact]
    public async Task LaunchAsync_AttachedCancellation_ThrowsOperationCanceledException()
    {
        var launcher = new GodotLauncher();
        var (executable, arguments) = ShellCommand("sleep 30", "ping -n 30 127.0.0.1 > nul");
        var request = CreateRequest(executable, arguments, GodotLaunchMode.Attached);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.LaunchAsync(request, cancellationToken: cancellation.Token));
    }

    private static GodotLaunchRequest CreateRequest(string executable, string arguments, GodotLaunchMode mode) =>
        new(
            new GodotLaunchTarget(
                "4.6-stable-standard@test",
                "4.6-stable-standard",
                executable,
                Path.GetTempPath()),
            arguments,
            mode);

    private static (string Executable, string Arguments) ShellCommand(string unixCommand, string windowsCommand)
    {
        if (OperatingSystem.IsWindows())
        {
            return (System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"{windowsCommand}\"");
        }

        return ("/bin/sh", $"-c \"{unixCommand}\"");
    }
}
