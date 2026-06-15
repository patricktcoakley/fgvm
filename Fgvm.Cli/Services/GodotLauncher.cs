using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
    string Arguments,
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
    private const int StandardErrorDescriptor = 2;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const int FileDescriptorCloseOnExec = 1;
    private const int GetFileDescriptorFlags = 1;
    private const int SetFileDescriptorFlags = 2;
    private const uint HandleFlagInherit = 1;
    private static readonly object DetachedStartLock = new();

    public async Task<Result<GodotLaunchOutcome, GodotLaunchError>> LaunchAsync(GodotLaunchRequest request,
        Action<GodotLaunchOutput>? onOutput = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Mode == GodotLaunchMode.Detached &&
            !OperatingSystem.IsWindows() &&
            Access(request.Target.ExecutablePath, 1) != 0)
        {
            return new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                new GodotLaunchError.StartFailed(
                    request.Target.ExecutablePath,
                    new Win32Exception(Marshal.GetLastPInvokeError()).Message));
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };

        try
        {
            if (!StartProcess(process, request.Mode))
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

        if (request.Mode == GodotLaunchMode.Detached)
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.Close();
            }

            return new Result<GodotLaunchOutcome, GodotLaunchError>.Success(
                new GodotLaunchOutcome.Detached(process.Id));
        }

        var standardOutput = ForwardLines(
            process.StandardOutput,
            line => onOutput?.Invoke(new GodotLaunchOutput.StandardOutput(line)),
            cancellationToken);
        var standardError = ForwardLines(
            process.StandardError,
            line => onOutput?.Invoke(new GodotLaunchOutput.StandardError(line)),
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutput, standardError);
            return new Result<GodotLaunchOutcome, GodotLaunchError>.Success(
                new GodotLaunchOutcome.Exited(process.ExitCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveOutputTasks(standardOutput, standardError);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            TryKill(process);
            return new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                new GodotLaunchError.ProcessFailed(request.Target.ExecutablePath, ex.Message));
        }
    }

    private static ProcessStartInfo CreateStartInfo(GodotLaunchRequest request)
    {
        var attached = request.Mode == GodotLaunchMode.Attached;
        if (!attached && !OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                WorkingDirectory = request.Target.WorkingDirectory
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
            startInfo.ArgumentList.Add("fgvm-godot-launch");
            startInfo.ArgumentList.Add(request.Target.ExecutablePath);

            foreach (var argument in ParseArguments(request.Arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        return new ProcessStartInfo
        {
            Arguments = request.Arguments,
            FileName = request.Target.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = attached,
            RedirectStandardInput = !attached && OperatingSystem.IsWindows(),
            RedirectStandardOutput = attached,
            RedirectStandardError = attached,
            WorkingDirectory = request.Target.WorkingDirectory
        };
    }

    private static bool StartProcess(Process process, GodotLaunchMode mode)
    {
        if (mode == GodotLaunchMode.Attached)
        {
            return process.Start();
        }

        lock (DetachedStartLock)
        {
            if (OperatingSystem.IsWindows())
            {
                using var nullDevice = File.OpenHandle(
                    "NUL",
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                return StartWithWindowsNullHandles(process, nullDevice);
            }

            var inheritedDescriptors = SetCloseOnExecForOpenDescriptors();
            try
            {
                return process.Start();
            }
            finally
            {
                RestoreFileDescriptorFlags(inheritedDescriptors);
            }
        }
    }

    private static bool StartWithWindowsNullHandles(Process process, SafeFileHandle nullDevice)
    {
        var originalInput = GetStdHandle(StandardInputHandle);
        var originalOutput = GetStdHandle(StandardOutputHandle);
        var originalError = GetStdHandle(StandardErrorHandle);
        var nullHandle = nullDevice.DangerousGetHandle();
        var originalHandleFlags = ClearHandleInheritance(originalInput, originalOutput, originalError);

        try
        {
            SetHandleInformationOrThrow(nullHandle, HandleFlagInherit, HandleFlagInherit);
            SetStdHandleOrThrow(StandardInputHandle, nullHandle);
            SetStdHandleOrThrow(StandardOutputHandle, nullHandle);
            SetStdHandleOrThrow(StandardErrorHandle, nullHandle);
            return process.Start();
        }
        finally
        {
            SetStdHandle(StandardInputHandle, originalInput);
            SetStdHandle(StandardOutputHandle, originalOutput);
            SetStdHandle(StandardErrorHandle, originalError);
            RestoreHandleInheritance(originalHandleFlags);
        }
    }

    private static List<(IntPtr Handle, uint Flags)> ClearHandleInheritance(params IntPtr[] handles)
    {
        var originalFlags = new List<(IntPtr Handle, uint Flags)>();
        var visited = new HashSet<IntPtr>();

        try
        {
            foreach (var handle in handles)
            {
                if (handle == IntPtr.Zero ||
                    handle == new IntPtr(-1) ||
                    !visited.Add(handle) ||
                    !GetHandleInformation(handle, out var flags))
                {
                    continue;
                }

                SetHandleInformationOrThrow(handle, HandleFlagInherit, 0);
                originalFlags.Add((handle, flags));
            }
        }
        catch
        {
            RestoreHandleInheritance(originalFlags);
            throw;
        }

        return originalFlags;
    }

    private static void RestoreHandleInheritance(IEnumerable<(IntPtr Handle, uint Flags)> handles)
    {
        foreach (var (handle, flags) in handles)
        {
            SetHandleInformation(handle, HandleFlagInherit, flags & HandleFlagInherit);
        }
    }

    private static List<(int Descriptor, int Flags)> SetCloseOnExecForOpenDescriptors()
    {
        var descriptorDirectory = OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd";
        var changedDescriptors = new List<(int Descriptor, int Flags)>();

        foreach (var path in Directory.EnumerateFileSystemEntries(descriptorDirectory).ToArray())
        {
            if (!int.TryParse(Path.GetFileName(path), out var descriptor) || descriptor <= StandardErrorDescriptor)
            {
                continue;
            }

            var flags = Fcntl(descriptor, GetFileDescriptorFlags, 0);
            if (flags < 0 || (flags & FileDescriptorCloseOnExec) != 0)
            {
                continue;
            }

            if (Fcntl(descriptor, SetFileDescriptorFlags, flags | FileDescriptorCloseOnExec) < 0)
            {
                continue;
            }

            changedDescriptors.Add((descriptor, flags));
        }

        return changedDescriptors;
    }

    private static void RestoreFileDescriptorFlags(IEnumerable<(int Descriptor, int Flags)> descriptors)
    {
        foreach (var (descriptor, flags) in descriptors)
        {
            Fcntl(descriptor, SetFileDescriptorFlags, flags);
        }
    }

    private static IReadOnlyList<string> ParseArguments(string arguments)
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
        var argument = new System.Text.StringBuilder();
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

    private static void SetStdHandleOrThrow(int standardHandle, IntPtr handle)
    {
        if (!SetStdHandle(standardHandle, handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void SetHandleInformationOrThrow(IntPtr handle, uint mask, uint flags)
    {
        if (!SetHandleInformation(handle, mask, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static async Task ForwardLines(StreamReader reader,
        Action<string> forward,
        CancellationToken cancellationToken
    )
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            forward(line);
        }
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
            // Best-effort cleanup after cancellation or process-management failure.
        }
    }

    private static async Task ObserveOutputTasks(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The streams are expected to close while terminating the process.
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string path, int mode);

    [DllImport("kernel32.dll", EntryPoint = "GetStdHandle", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", EntryPoint = "SetStdHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "GetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [DllImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
#pragma warning restore SYSLIB1054
}
