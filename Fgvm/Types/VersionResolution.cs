namespace Fgvm.Types;

/// <summary>
///     Represents the successful outcome of version resolution.
/// </summary>
public abstract record VersionResolutionOutcome
{
    public record Found(string ExecutablePath, string WorkingDirectory, string VersionName, bool IsProjectVersion, string? InstallationKey = null)
        : VersionResolutionOutcome;

    public record InteractiveRequired(IReadOnlyList<string> AvailableVersions) : VersionResolutionOutcome;
}

/// <summary>
///     Represents the possible errors that can occur during version resolution.
/// </summary>
public abstract record VersionResolutionError
{
    public record NotFound(string Version) : VersionResolutionError;

    public record Failed(string Reason) : VersionResolutionError;

    public record InvalidVersion(string Version) : VersionResolutionError;
}
