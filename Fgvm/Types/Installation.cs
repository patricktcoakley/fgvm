namespace Fgvm.Types;

/// <summary>
///     Represents information about checksum verification during installation.
/// </summary>
public abstract record ChecksumVerification
{
    /// <summary>Checksum was verified successfully</summary>
    public record Verified : ChecksumVerification;

    /// <summary>Checksum verification was skipped (version too old)</summary>
    public record Skipped : ChecksumVerification;

    /// <summary>Checksum verification failed due to network error</summary>
    public record Failed(NetworkError Error) : ChecksumVerification;
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
    public record NotFound(string Version) : InstallationError;

    public record Failed(string Reason) : InstallationError;

    /// <summary>Checksum mismatch - actual security issue</summary>
    public record ChecksumMismatch(string Expected, string Actual, string FileName) : InstallationError;

    /// <summary>Checksum content couldn't be parsed</summary>
    public record ChecksumParseError(string Content) : InstallationError;
}
