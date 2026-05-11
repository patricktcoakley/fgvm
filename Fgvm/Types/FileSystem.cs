namespace Fgvm.Types;

/// <summary>
///     Information about the current symlink configuration.
/// </summary>
public readonly record struct SymlinkInfo(string SymlinkPath, string? MacAppSymlinkPath = null);

/// <summary>
///     Represents the possible errors that can occur with symlink operations.
/// </summary>
public abstract record SymlinkError
{
    public record NoVersionSet : SymlinkError;

    public record DeveloperModeRequired : SymlinkError;

    public record UnsupportedOS(string OS) : SymlinkError;

    public record PermissionDenied : SymlinkError;

    public record InvalidSymlink(string SymlinkPath, string Target) : SymlinkError;

    public record RemoveFailed(string Path) : SymlinkError;
}

/// <summary>
///     Represents failures while reading installation filesystem state.
/// </summary>
public abstract record FileSystemError
{
    public sealed record DirectoryNotFound(string Directory) : FileSystemError;

    public sealed record PermissionDenied(string Path) : FileSystemError;

    public sealed record InvalidPath(string Path) : FileSystemError;

    public sealed record EnumerationFailed(string Path) : FileSystemError;
}
