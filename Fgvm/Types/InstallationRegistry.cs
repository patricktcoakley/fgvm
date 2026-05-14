namespace Fgvm.Types;

/// <summary>
///     Represents an installed Godot release tracked by the installation registry.
/// </summary>
public sealed record Installation(
    string Key,
    string ReleaseNameWithRuntime,
    string Target,
    string RelativePath,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastLaunchedAt);

/// <summary>
///     Represents failures while reading, generating, or writing the installation registry.
/// </summary>
public abstract record InstallationRegistryError
{
    public sealed record ReadFailed(FileOperationError Error) : InstallationRegistryError
    {
        public override string ToString() => $"Unable to read installation registry: {Error}";
    }

    public sealed record WriteFailed(FileOperationError Error) : InstallationRegistryError
    {
        public override string ToString() => $"Unable to write installation registry: {Error}";
    }

    public sealed record GenerationFailed(FileOperationError Error) : InstallationRegistryError
    {
        public override string ToString() => $"Unable to generate installation registry: {Error}";
    }

    public sealed record InvalidPath(string Path) : InstallationRegistryError
    {
        public override string ToString() => $"Invalid installation registry path: `{Path}`.";
    }

    public sealed record NotFound(string Key) : InstallationRegistryError
    {
        public override string ToString() => $"Installation not found: `{Key}`.";
    }
}
