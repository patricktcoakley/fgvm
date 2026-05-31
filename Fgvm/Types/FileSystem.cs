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

    public record UnsupportedOS(string OS) : SymlinkError;

    public record PermissionDenied : SymlinkError;

    public record InvalidSymlink(string SymlinkPath, string Target) : SymlinkError;

    public record RemoveFailed(string Path) : SymlinkError;
}

/// <summary>
///     Represents failures while creating fgvm shim artifacts.
/// </summary>
public abstract record ShimError
{
    public sealed record PathConflict(string Path) : ShimError
    {
        public override string ToString() => $"Shim path already exists and is not managed by fgvm: `{Path}`.";
    }

    public sealed record WriteFailed(FileOperationError Error) : ShimError
    {
        public override string ToString() => $"Unable to write shim: {Error}";
    }
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

/// <summary>
///     Represents common filesystem operation failures with the affected path preserved.
/// </summary>
public abstract record FileOperationError(string Path)
{
    public sealed record PermissionDenied(string Path) : FileOperationError(Path)
    {
        public override string ToString() => $"Permission denied for `{Path}`.";
    }

    public sealed record NotFound(string Path) : FileOperationError(Path)
    {
        public override string ToString() => $"Path not found: `{Path}`.";
    }

    public sealed record InvalidPath(string Path) : FileOperationError(Path)
    {
        public override string ToString() => $"Invalid path: `{Path}`.";
    }

    public sealed record UnsupportedPath(string Path) : FileOperationError(Path)
    {
        public override string ToString() => $"Unsupported path: `{Path}`.";
    }

    public sealed record IoFailure(string Path) : FileOperationError(Path)
    {
        public override string ToString() => $"I/O failure for `{Path}`.";
    }
}
