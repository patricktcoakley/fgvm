namespace Fgvm.Types;

/// <summary>
///     Represents a project lookup that can succeed without finding a project value.
/// </summary>
public abstract record ProjectLookup<T>
{
    public sealed record Found(T Value) : ProjectLookup<T>;

    public sealed record Missing : ProjectLookup<T>;
}

/// <summary>
///     Represents project file discovery, parsing, and write failures.
/// </summary>
public abstract record ProjectError
{
    public sealed record InvalidPath(string Path) : ProjectError;

    public sealed record DirectoryNotFound(string Directory) : ProjectError;

    public sealed record PermissionDenied(string Path) : ProjectError;

    public sealed record ReadFailed(string Path) : ProjectError;

    public sealed record WriteFailed(string Path) : ProjectError;

    public sealed record InvalidVersion(string Version) : ProjectError;

    public sealed record InvalidProjectFile(string Path) : ProjectError;
}
