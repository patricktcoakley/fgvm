namespace Fgvm.Types;

/// <summary>
///     Represents information about checksum verification during installation.
/// </summary>
public abstract record ChecksumVerification
{
    /// <summary>Checksum was verified successfully</summary>
    public record Verified : ChecksumVerification;

    /// <summary>Checksum metadata was unavailable for the selected artifact</summary>
    public record Unavailable : ChecksumVerification;
}

/// <summary>
///     Represents the successful outcome of an installation operation.
/// </summary>
public abstract record InstallationOutcome
{
    public record NewInstallation(string ReleaseNameWithRuntime, ChecksumVerification ChecksumStatus, SymlinkError? SymlinkWarning = null)
        : InstallationOutcome;

    public record AlreadyInstalled(string ReleaseNameWithRuntime) : InstallationOutcome;
}

/// <summary>
///     Represents the possible errors that can occur during installation.
/// </summary>
public abstract record InstallationError
{
    public record InvalidQuery(string Message) : InstallationError;

    public record NotFound(string Version) : InstallationError;

    public record Failed(string Reason) : InstallationError;

    public record ChecksumMismatch(string Expected, string Actual, string FileName) : InstallationError;
}
