using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Fgvm.Types;

namespace Fgvm.Cli.Services;

public enum GodotLaunchMode
{
    Attached,
    Detached
}

public sealed record GodotLaunchTarget(
    string InstallationKey,
    string VersionName,
    string ExecutablePath,
    string WorkingDirectory
)
{
    public static GodotLaunchTarget FromResolution(VersionResolutionOutcome.Found resolution,
        string? executableOverride = null
    )
    {
        var installationKey = resolution.InstallationKey
                              ?? throw new InvalidOperationException("A resolved Godot installation must have an installation key.");
        var executablePath = executableOverride ?? resolution.ExecutablePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution.WorkingDirectory);

        return new GodotLaunchTarget(
            installationKey,
            resolution.VersionName,
            executablePath,
            resolution.WorkingDirectory);
    }
}

public sealed record GodotLaunchRequest(
    GodotLaunchTarget Target,
    IReadOnlyList<string> Arguments,
    GodotLaunchMode Mode
);

public abstract record GodotLaunchOutput
{
    public sealed record StandardOutput(string Line) : GodotLaunchOutput;

    public sealed record StandardError(string Line) : GodotLaunchOutput;
}

public abstract record GodotLaunchOutcome
{
    public sealed record Detached(int ProcessId) : GodotLaunchOutcome;

    public sealed record Exited(int ExitCode) : GodotLaunchOutcome;
}

public abstract record GodotLaunchError
{
    public sealed record StartFailed(string ExecutablePath, string Reason) : GodotLaunchError;

    public sealed record ProcessFailed(string ExecutablePath, string Reason) : GodotLaunchError;
}

public interface IGodotLauncher
{
    Task<Result<GodotLaunchOutcome, GodotLaunchError>> LaunchAsync(GodotLaunchRequest request,
        Action<GodotLaunchOutput>? onOutput = null,
        CancellationToken cancellationToken = default
    );
}

public sealed class GodotLauncher : IGodotLauncher
{
    public async Task<Result<GodotLaunchOutcome, GodotLaunchError>> LaunchAsync(GodotLaunchRequest request,
        Action<GodotLaunchOutput>? onOutput = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(request);

        if (request.Mode == GodotLaunchMode.Detached)
        {
            try
            {
                var processId = Process.StartAndForget(startInfo);
                return new Result<GodotLaunchOutcome, GodotLaunchError>.Success(
                    new GodotLaunchOutcome.Detached(processId));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                    new GodotLaunchError.StartFailed(request.Target.ExecutablePath, ex.Message));
            }
        }

        using var process = new Process();
        process.StartInfo = startInfo;

        try
        {
            if (!process.Start())
            {
                return new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                    new GodotLaunchError.StartFailed(request.Target.ExecutablePath, "The process did not start."));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                new GodotLaunchError.StartFailed(request.Target.ExecutablePath, ex.Message));
        }

        try
        {
            await foreach (var line in process.ReadAllLinesAsync(cancellationToken))
            {
                onOutput?.Invoke(line.StandardError
                    ? new GodotLaunchOutput.StandardError(line.Content)
                    : new GodotLaunchOutput.StandardOutput(line.Content));
            }

            var exitStatus = await process.WaitForExitStatusAsync(cancellationToken);
            return new Result<GodotLaunchOutcome, GodotLaunchError>.Success(
                new GodotLaunchOutcome.Exited(exitStatus.ExitCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            TryKill(process);
            return new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                new GodotLaunchError.ProcessFailed(request.Target.ExecutablePath, ex.Message));
        }
    }

    internal static ProcessStartInfo CreateStartInfo(GodotLaunchRequest request)
    {
        var attached = request.Mode == GodotLaunchMode.Attached;
        return new ProcessStartInfo(request.Target.ExecutablePath, request.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = attached,
            RedirectStandardOutput = attached,
            RedirectStandardError = attached,
            StartDetached = !attached,
            // Detached processes must not retain the launcher's redirected pipes.
            InheritedHandles = attached ? null : [],
            WorkingDirectory = request.Target.WorkingDirectory
        };
    }

    internal static IReadOnlyList<string> ParseArguments(string arguments)
    {
        var parsed = new List<string>();

        for (var index = 0; index < arguments.Length; index++)
        {
            while (index < arguments.Length && arguments[index] is ' ' or '\t')
            {
                index++;
            }

            if (index == arguments.Length)
            {
                break;
            }

            parsed.Add(ParseNextArgument(arguments, ref index));
        }

        return parsed;
    }

    private static string ParseNextArgument(string arguments, ref int index)
    {
        var argument = new StringBuilder();
        var inQuotes = false;

        while (index < arguments.Length)
        {
            var backslashCount = 0;
            while (index < arguments.Length && arguments[index] == '\\')
            {
                index++;
                backslashCount++;
            }

            if (backslashCount > 0)
            {
                if (index >= arguments.Length || arguments[index] != '"')
                {
                    argument.Append('\\', backslashCount);
                }
                else
                {
                    argument.Append('\\', backslashCount / 2);
                    if (backslashCount % 2 != 0)
                    {
                        argument.Append('"');
                        index++;
                    }
                }

                continue;
            }

            var character = arguments[index];
            if (character == '"')
            {
                if (inQuotes && index < arguments.Length - 1 && arguments[index + 1] == '"')
                {
                    argument.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                index++;
                continue;
            }

            if (character is ' ' or '\t' && !inQuotes)
            {
                break;
            }

            argument.Append(character);
            index++;
        }

        return argument.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process may exit before cleanup reaches it.
        }
    }
}
