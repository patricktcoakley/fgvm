using System.Collections.Concurrent;
using System.Diagnostics;
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

    [Theory]
    [InlineData(GodotLaunchMode.Attached)]
    [InlineData(GodotLaunchMode.Detached)]
    public async Task LaunchAsync_MissingExecutable_ReturnsStartFailure(GodotLaunchMode mode)
    {
        var launcher = new GodotLauncher();
        var request = CreateRequest(
            Path.Combine(Path.GetTempPath(), $"missing-godot-{Guid.NewGuid():N}"),
            "",
            mode);

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
        Assert.Equal("hello", Assert.Single(output.OfType<GodotLaunchOutput.StandardOutput>()).Line.Trim());
        Assert.Equal("oops", Assert.Single(output.OfType<GodotLaunchOutput.StandardError>()).Line.Trim());
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task LaunchAsync_Detached_ReturnsProcessId()
    {
        var launcher = new GodotLauncher();
        var (executable, arguments) = ShellCommand("sleep 1", "ping -n 3 127.0.0.1 > nul");
        var request = CreateRequest(executable, arguments, GodotLaunchMode.Detached);

        var result = await launcher.LaunchAsync(request);

        var success = Assert.IsType<Result<GodotLaunchOutcome, GodotLaunchError>.Success>(result);
        var detached = Assert.IsType<GodotLaunchOutcome.Detached>(success.Value);
        Assert.True(detached.ProcessId > 0);
    }

    [Fact]
    public async Task LaunchAsync_DetachedImmediateExit_ReturnsProcessId()
    {
        var launcher = new GodotLauncher();
        var (executable, arguments) = ShellCommand("exit 0", "exit /b 0");
        var request = CreateRequest(executable, arguments, GodotLaunchMode.Detached);

        var result = await launcher.LaunchAsync(request);

        var success = Assert.IsType<Result<GodotLaunchOutcome, GodotLaunchError>.Success>(result);
        var detached = Assert.IsType<GodotLaunchOutcome.Detached>(success.Value);
        Assert.True(detached.ProcessId > 0);
    }

    [Theory]
    [InlineData(GodotLaunchMode.Attached)]
    [InlineData(GodotLaunchMode.Detached)]
    public void CreateStartInfo_UsesExpectedProcessSettings(GodotLaunchMode mode)
    {
        var arguments = new[] { "plain", "two words", "quote\"inside" };
        var request = new GodotLaunchRequest(
            new GodotLaunchTarget(
                "4.6-stable-standard@test",
                "4.6-stable-standard",
                Path.Combine(Path.GetTempPath(), "Mock Godot"),
                Path.Combine(Path.GetTempPath(), "working directory")),
            arguments,
            mode);

        var startInfo = GodotLauncher.CreateStartInfo(request);

        Assert.Equal(request.Target.ExecutablePath, startInfo.FileName);
        Assert.Equal(request.Target.WorkingDirectory, startInfo.WorkingDirectory);
        Assert.Equal(arguments, startInfo.ArgumentList);
        Assert.Equal(mode == GodotLaunchMode.Detached, startInfo.StartDetached);
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Equal(mode == GodotLaunchMode.Attached, startInfo.RedirectStandardOutput);
        Assert.Equal(mode == GodotLaunchMode.Attached, startInfo.RedirectStandardError);
        if (mode == GodotLaunchMode.Detached)
        {
            Assert.Empty(startInfo.InheritedHandles!);
        }
        else
        {
            Assert.Null(startInfo.InheritedHandles);
        }
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

    [Fact]
    public async Task LaunchAsync_AttachedSignalExit_ReturnsShellConventionExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = new GodotLauncher();
        var request = CreateRequest("/bin/sh", "-c \"kill -TERM $$\"", GodotLaunchMode.Attached);

        var result = await launcher.LaunchAsync(request);

        var success = Assert.IsType<Result<GodotLaunchOutcome, GodotLaunchError>.Success>(result);
        var exited = Assert.IsType<GodotLaunchOutcome.Exited>(success.Value);
        Assert.Equal(128 + 15, exited.ExitCode);
    }

    [Fact]
    public async Task LaunchAsync_AttachedCancellation_KillsProcessTree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = new GodotLauncher();
        var childProcessId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            "/bin/sh",
            "-c \"sleep 30 & child=$!; printf '%s\\n' $child; wait\"",
            GodotLaunchMode.Attached);
        using var cancellation = new CancellationTokenSource();

        var launch = launcher.LaunchAsync(request, output =>
        {
            if (output is GodotLaunchOutput.StandardOutput(var line) && int.TryParse(line, out var processId))
            {
                childProcessId.TrySetResult(processId);
            }
        }, cancellation.Token);

        try
        {
            var child = await childProcessId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
            await AssertProcessExited(child);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    private static async Task AssertProcessExited(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Child process {processId} was still running after launcher cancellation.");
    }

    private static GodotLaunchRequest CreateRequest(string executable, string arguments, GodotLaunchMode mode) =>
        new(
            new GodotLaunchTarget(
                "4.6-stable-standard@test",
                "4.6-stable-standard",
                executable,
                Path.GetTempPath()),
            GodotLauncher.ParseArguments(arguments),
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
