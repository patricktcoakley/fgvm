namespace Fgvm.Types;

/// <summary>
///     Represents a successful compatible-version acquisition.
/// </summary>
public abstract record CompatibleVersionOutcome
{
    public sealed record Found(string Version) : CompatibleVersionOutcome;

    public sealed record Installed(string Version) : CompatibleVersionOutcome;

    public sealed record Declined : CompatibleVersionOutcome;
}

/// <summary>
///     Represents failures while finding or installing a compatible version.
/// </summary>
public abstract record CompatibleVersionError
{
    public sealed record RegistryFailed(InstallationRegistryError Error) : CompatibleVersionError;

    public sealed record InstallationFailed(InstallationError Error) : CompatibleVersionError;

    public sealed record ResolutionFailed(
        string ProjectVersion,
        string InstalledVersion,
        CompatibilityError Error
    ) : CompatibleVersionError;

    public sealed record Unexpected(string Reason) : CompatibleVersionError;
}
