using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.IO.Enumeration;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Fgvm.Environment;

/// <summary>
///     Provides host OS and filesystem operations used by fgvm.
/// </summary>
public interface IHostSystem
{
    /// <summary>
    ///     Gets the current host OS and architecture.
    /// </summary>
    SystemInfo SystemInfo { get; }

    /// <summary>
    ///     Creates or replaces fgvm's Godot symlink for the supplied target.
    /// </summary>
    /// <param name="symlinkTargetPath">The executable or app bundle path to link to.</param>
    /// <returns>Success, or a symlink error describing why the link could not be created.</returns>
    Result<Unit, SymlinkError> CreateOrOverwriteSymbolicLink(string symlinkTargetPath);

    /// <summary>
    ///     Removes fgvm-managed Godot symlinks.
    /// </summary>
    /// <returns>Success, or a symlink error describing why removal failed.</returns>
    Result<Unit, SymlinkError> RemoveSymbolicLinks();

    /// <summary>
    ///     Resolves the currently configured fgvm symlinks.
    /// </summary>
    /// <returns>Current symlink information, or a symlink error.</returns>
    Result<SymlinkInfo, SymlinkError> ResolveCurrentSymlinks();

    /// <summary>
    ///     Lists locally installed Godot versions.
    /// </summary>
    /// <returns>Installed release names, or a filesystem error.</returns>
    Result<IReadOnlyList<string>, FileSystemError> ListInstallations();

    /// <summary>
    ///     Checks whether fgvm can create symlinks on the current host.
    /// </summary>
    /// <returns>Success, or a symlink support error.</returns>
    Result<Unit, SymlinkError> AreSymlinksSupported();
}

/// <summary>
///     A way to organize host OS file operations.
/// </summary>
public sealed class HostSystem(SystemInfo systemInfo, IPathService pathService, ILogger<HostSystem> logger) : IHostSystem
{
    /// <inheritdoc />
    public SystemInfo SystemInfo { get; } = systemInfo;

    /// <inheritdoc />
    public Result<Unit, SymlinkError> AreSymlinksSupported()
    {
        Result<Unit, SymlinkError> result = SystemInfo.CurrentOS switch
        {
            OS.Windows => IsWindowsDeveloperModeEnabled()
                ? new Result<Unit, SymlinkError>.Success(Unit.Value)
                : new Result<Unit, SymlinkError>.Failure(new SymlinkError.DeveloperModeRequired()),
            OS.Linux or OS.MacOS => new Result<Unit, SymlinkError>.Success(Unit.Value),
            _ => new Result<Unit, SymlinkError>.Failure(new SymlinkError.UnsupportedOS(SystemInfo.CurrentOS.ToString()))
        };

        return result;
    }

    /// <inheritdoc />
    public Result<Unit, SymlinkError> CreateOrOverwriteSymbolicLink(string symlinkTargetPath)
    {
        if (AreSymlinksSupported() is Result<Unit, SymlinkError>.Failure supportFailure)
        {
            return supportFailure;
        }

        if (RemoveSymbolicLinks() is Result<Unit, SymlinkError>.Failure removeFailure)
        {
            return removeFailure;
        }

        switch (SystemInfo.CurrentOS)
        {
            // We link to both the .app and the Godot command-line binary on macOS.
            case OS.MacOS:
                try
                {
                    Directory.CreateSymbolicLink(pathService.MacAppSymlinkPath, symlinkTargetPath);
                    File.CreateSymbolicLink(pathService.SymlinkPath, Path.Combine(symlinkTargetPath, "Contents/MacOS/Godot"));
                }
                catch (UnauthorizedAccessException)
                {
                    return new Result<Unit, SymlinkError>.Failure(new SymlinkError.PermissionDenied());
                }

                break;
            case OS.Windows:
                try
                {
                    File.CreateSymbolicLink(pathService.SymlinkPath, symlinkTargetPath);
                }
                // Special case where we can assume that the user has not enabled Developer Mode.
                // We don't necessarily want to fail because symlinks aren't required.
                // TODO: Consider adding an option to ignore/disable symlinks for people who don't care
                catch (Exception e) when (e.Message.StartsWith("A required privilege is not held by the client"))
                {
                    logger.LogWarning(
                        "Windows requires Developer Mode enabled to create symlinks. See: https://learn.microsoft.com/en-us/windows/apps/get-started/enable-your-device-for-development.");

                    return new Result<Unit, SymlinkError>.Failure(new SymlinkError.DeveloperModeRequired());
                }
                catch (UnauthorizedAccessException)
                {
                    return new Result<Unit, SymlinkError>.Failure(new SymlinkError.PermissionDenied());
                }

                break;

            case OS.Linux:
                try
                {
                    File.CreateSymbolicLink(pathService.SymlinkPath, symlinkTargetPath);
                }
                catch (UnauthorizedAccessException)
                {
                    return new Result<Unit, SymlinkError>.Failure(new SymlinkError.PermissionDenied());
                }

                break;

            // TODO: Untested but possibly works with Linux builds?
            case OS.FreeBSD:
            case OS.Unknown:
            default:
                logger.LogError("Unsupported OS: {OS}", SystemInfo.CurrentOS);
                return new Result<Unit, SymlinkError>.Failure(
                    new SymlinkError.UnsupportedOS(SystemInfo.CurrentOS.ToString()));
        }

        if (SystemInfo.CurrentOS == OS.MacOS && !IsSymbolicLinkValid(pathService.MacAppSymlinkPath))
        {
            return new Result<Unit, SymlinkError>.Failure(
                new SymlinkError.InvalidSymlink(pathService.MacAppSymlinkPath, "Symlink created but appears invalid"));
        }

        if (!IsSymbolicLinkValid(pathService.SymlinkPath))
        {
            return new Result<Unit, SymlinkError>.Failure(
                new SymlinkError.InvalidSymlink(pathService.SymlinkPath, "Symlink created but appears invalid"));
        }

        return new Result<Unit, SymlinkError>.Success(Unit.Value);
    }

    /// <inheritdoc />
    public Result<Unit, SymlinkError> RemoveSymbolicLinks()
    {
        if (SystemInfo.CurrentOS is OS.FreeBSD or OS.Unknown)
        {
            return new Result<Unit, SymlinkError>.Failure(
                new SymlinkError.UnsupportedOS(SystemInfo.CurrentOS.ToString()));
        }

        try
        {
            // ATTN: On macOS the behavior of #.Exists can be unreliable due to the differences in how symbolic links are handled on the filesystem.
            // This is possibly related to the fact that the .app is a symlink to a directory and not a file, so it is technically "neither."
            // Therefore, we need to check if it has the ReparsePoint attribute to see if it "truly" exists.
            var macAppSymlinkFileInfo = new FileInfo(pathService.MacAppSymlinkPath);
            if ((macAppSymlinkFileInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                macAppSymlinkFileInfo.Delete();
            }

            if (File.Exists(pathService.SymlinkPath))
            {
                File.Delete(pathService.SymlinkPath);
            }

            return new Result<Unit, SymlinkError>.Success(Unit.Value);
        }
        catch (UnauthorizedAccessException)
        {
            return new Result<Unit, SymlinkError>.Failure(new SymlinkError.PermissionDenied());
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return new Result<Unit, SymlinkError>.Failure(new SymlinkError.RemoveFailed(pathService.BinPath));
        }
    }

    /// <inheritdoc />
    public Result<SymlinkInfo, SymlinkError> ResolveCurrentSymlinks()
    {
        var file = new FileInfo(pathService.SymlinkPath);
        if (file.LinkTarget is null)
        {
            logger.LogInformation("Ran `which` without version set");
            return new Result<SymlinkInfo, SymlinkError>.Failure(
                new SymlinkError.NoVersionSet());
        }

        if (!IsSymbolicLinkValid(pathService.SymlinkPath))
        {
            return new Result<SymlinkInfo, SymlinkError>.Failure(
                new SymlinkError.InvalidSymlink(pathService.SymlinkPath, file.LinkTarget));
        }

        logger.LogInformation("{SymlinkPath} is currently set to: {LinkTarget}", pathService.SymlinkPath, file.LinkTarget);

        if (SystemInfo.CurrentOS != OS.MacOS)
        {
            return new Result<SymlinkInfo, SymlinkError>.Success(
                new SymlinkInfo(file.LinkTarget));
        }

        // Only macOS has two symlinks
        string? macAppSymlinkPath = null;
        var appFile = new FileInfo(pathService.MacAppSymlinkPath);
        if (appFile.LinkTarget is not null)
        {
            if (IsSymbolicLinkValid(pathService.MacAppSymlinkPath))
            {
                logger.LogInformation("{MacAppSymlinkPath} is currently set to: {LinkTarget}", pathService.MacAppSymlinkPath, appFile.LinkTarget);
                macAppSymlinkPath = appFile.LinkTarget;
            }
            else
            {
                logger.LogWarning("Mac App symlink {MacAppSymlinkPath} exists but is invalid", pathService.MacAppSymlinkPath);
            }
        }
        else
        {
            logger.LogWarning("Mac App symlink {MacAppSymlinkPath} is not set", pathService.MacAppSymlinkPath);
        }

        return new Result<SymlinkInfo, SymlinkError>.Success(
            new SymlinkInfo(file.LinkTarget, macAppSymlinkPath));
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<string>, FileSystemError> ListInstallations()
    {
        try
        {
            var installed = new FileSystemEnumerable<string>(
                pathService.RootPath,
                (ref entry) => entry.FileName.ToString())
            {
                ShouldIncludePredicate = (ref entry) =>
                    entry is { IsDirectory: true, FileName: not "bin", IsHidden: false }
            };

            var releases = installed
                .Select(Release.TryParse)
                .OfType<Release>()
                .OrderByDescending(release => release)
                .Select(release => release.ReleaseNameWithRuntime)
                .ToArray();

            return new Result<IReadOnlyList<string>, FileSystemError>.Success(releases);
        }
        catch (UnauthorizedAccessException)
        {
            return new Result<IReadOnlyList<string>, FileSystemError>.Failure(new FileSystemError.PermissionDenied(pathService.RootPath));
        }
        catch (DirectoryNotFoundException)
        {
            return new Result<IReadOnlyList<string>, FileSystemError>.Failure(new FileSystemError.DirectoryNotFound(pathService.RootPath));
        }
        catch (Exception ex) when (ex is IOException)
        {
            return new Result<IReadOnlyList<string>, FileSystemError>.Failure(new FileSystemError.EnumerationFailed(pathService.RootPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new Result<IReadOnlyList<string>, FileSystemError>.Failure(new FileSystemError.InvalidPath(pathService.RootPath));
        }
    }


    private static bool IsSymbolicLinkValid(string symlinkTargetPath)
    {
        var symlinkFileInfo = new FileInfo(symlinkTargetPath);

        // check if considered a symlink
        if ((symlinkFileInfo.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
        {
            return false;
        }

        var linkTarget = symlinkFileInfo.ResolveLinkTarget(true);

        return linkTarget is not null &&
               (File.Exists(linkTarget.FullName) || Directory.Exists(linkTarget.FullName));
    }

    // Stub Developer Mode check by existing early on non-Windows and only checking when necessary
    private bool IsWindowsDeveloperModeEnabled() =>
        !OperatingSystem.IsWindows() || IsDeveloperModeEnabled() || IsWindowsElevated();

    /// <summary>
    ///     Determines whether Windows Developer Mode is enabled by querying the system registry. This method assumes it is
    ///     only called on Windows platforms that support registry access.
    /// </summary>
    /// <returns>
    ///     True if Windows Developer Mode is enabled; otherwise, false. Defaults to false if the registry query fails.
    /// </returns>
    [SupportedOSPlatform("windows")]
    private bool IsDeveloperModeEnabled()
    {
        try
        {
            const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue("AllowDevelopmentWithoutDevLicense") is 1;
        }
        catch
        {
            logger.LogWarning("Windows Developer Mode not enabled.");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private bool IsWindowsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            logger.LogWarning("Command not run with elevated privileges on Windows.");
            return false;
        }
    }
}
