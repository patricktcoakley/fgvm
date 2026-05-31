using System.IO.Compression;
using System.Security.Cryptography;
using Fgvm.Environment;
using Fgvm.Extensions;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Services;

/// <summary>
///     Represents the different stages of a Godot installation process
/// </summary>
public enum InstallationStage
{
    /// <summary>Preparing for installation</summary>
    Initializing,

    /// <summary>Downloading the release archive</summary>
    Downloading,

    /// <summary>Verifying the downloaded file's checksum</summary>
    VerifyingChecksum,

    /// <summary>Extracting files from the archive</summary>
    Extracting,

    /// <summary>Setting the installed version as default</summary>
    SettingDefault
}

/// <summary>
///     Installs Godot releases and fetches release names for installation flows.
/// </summary>
public interface IInstallationService
{
    /// <summary>
    ///     Installs a specific Godot release.
    /// </summary>
    /// <param name="godotRelease">The release to install.</param>
    /// <param name="progress">Progress reporter for installation updates.</param>
    /// <param name="setAsDefault">Whether to set this version as the global default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation outcome, or an installation error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when installation is canceled.</exception>
    Task<Result<InstallationOutcome, InstallationError>> InstallReleaseAsync(Release godotRelease,
        IProgress<OperationProgress<InstallationStage>> progress,
        bool setAsDefault = true,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Resolves a query and installs the matching Godot release.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="progress">Progress reporter for installation updates.</param>
    /// <param name="setAsDefault">Whether to set the installed version as the global default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation outcome, or an installation error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when installation is canceled.</exception>
    Task<Result<InstallationOutcome, InstallationError>> InstallByQueryAsync(string[] query,
        IProgress<OperationProgress<InstallationStage>> progress,
        bool setAsDefault = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Fetches available release names from the release catalog.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="fetchMode">Whether to use the local cache or force a remote refresh.</param>
    /// <returns>Available release names, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when release lookup is canceled.</exception>
    Task<Result<string[], NetworkError>> FetchReleaseNames(CancellationToken cancellationToken,
        ReleaseFetchMode fetchMode = ReleaseFetchMode.UseCache
    );
}

public class InstallationService(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IReleaseCatalog releaseCatalog,
    IPathService pathService,
    IInstallationRegistry installationRegistry,
    ILogger<InstallationService> logger
)
    : IInstallationService
{
    /// <inheritdoc />
    public async Task<Result<InstallationOutcome, InstallationError>> InstallReleaseAsync(Release godotRelease,
        IProgress<OperationProgress<InstallationStage>> progress,
        bool setAsDefault = true,
        CancellationToken cancellationToken = default
    )
    {
        var extractPath = "";

        try
        {
            var installPathBase = godotRelease.ReleaseNameWithRuntime;
            var relativeInstallPath = InstallationRegistry.CreateRelativeInstallPath(godotRelease);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Initializing,
                $"Initializing installation of {installPathBase}..."));

            // Check if already installed
            switch (installationRegistry.FindByReleaseName(installPathBase))
            {
                case Result<Installation, InstallationRegistryError>.Success:
                    logger.LogInformation("Version {InstallPathBase} is already installed", installPathBase);
                    return new Result<InstallationOutcome, InstallationError>.Success(
                        new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
                case Result<Installation, InstallationRegistryError>.Failure(InstallationRegistryError.NotFound):
                    break;
                case Result<Installation, InstallationRegistryError>.Failure:
                    return new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.Failed($"Unable to read installation registry for {installPathBase}."));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            // Show progress immediately before HTTP request
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase}..."));

            const int bufferSize = 32768;
            ReleaseArtifact artifact;
            switch (await releaseCatalog.FindOrHydrateArtifact(godotRelease, cancellationToken))
            {
                case Result<ReleaseArtifact, NetworkError>.Success(var releaseArtifact):
                    artifact = releaseArtifact;
                    break;
                case Result<ReleaseArtifact, NetworkError>.Failure(var error):
                    return new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.Failed($"Release catalog hydration failed for {godotRelease.ReleaseName}: {error}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            var zipFileName = artifact.FileName;
            var zipResult = await releaseManager.GetZipFile(zipFileName, godotRelease, cancellationToken);

            ZipDownload download;
            switch (zipResult)
            {
                case Result<ZipDownload, NetworkError>.Success(var downloadZip):
                    download = downloadZip;
                    break;
                case Result<ZipDownload, NetworkError>.Failure(var downloadError):
                    return new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.Failed($"Download failed for {zipFileName}: {downloadError}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            await using var downloadOwner = download;

            var contentLength = download.ContentLength ?? (download.Stream.CanSeek ? download.Stream.Length : 0);

            await using var memStream = contentLength > 0 && contentLength <= int.MaxValue
                ? new MemoryStream(checked((int)contentLength))
                : new MemoryStream();

            // Download the release with dedicated progress context
            cancellationToken.ThrowIfCancellationRequested();

            var buffer = new byte[bufferSize];
            int bytesRead;
            var totalDownloaded = 0L;
            var lastProgressUpdate = 0L;
            var startTime = DateTime.UtcNow;

            while ((bytesRead = await download.Stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await memStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalDownloaded += bytesRead;

                // Update progress every 1MB or when finished
                if (contentLength > 0 && (totalDownloaded - lastProgressUpdate >= 1024 * 1024 || totalDownloaded == contentLength))
                {
                    var downloadedMB = totalDownloaded / 1024.0 / 1024.0;
                    var totalMB = contentLength / 1024.0 / 1024.0;

                    // Calculate download speed
                    var elapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
                    var speedText = "";
                    if (elapsedSeconds > 0.5)
                    {
                        var speedMBps = downloadedMB / elapsedSeconds;
                        speedText = speedMBps >= 1.0
                            ? $" • {speedMBps:F1} MB/s"
                            : $" • {speedMBps * 1024:F0} KB/s";
                    }

                    progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading,
                        $"Downloading {installPathBase} • {downloadedMB:F1}/{totalMB:F1} MB{speedText}"));

                    lastProgressUpdate = totalDownloaded;
                }
            }

            var checksumResult = await VerifyChecksum(zipFileName, artifact, memStream, progress, cancellationToken);
            ChecksumVerification checksumStatus;
            switch (checksumResult)
            {
                case Result<ChecksumVerification, InstallationError>.Success(var checksumVerification):
                    checksumStatus = checksumVerification;
                    break;
                case Result<ChecksumVerification, InstallationError>.Failure(var error):
                    return new Result<InstallationOutcome, InstallationError>.Failure(error);
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Extracting, "Extracting files..."));
            memStream.Position = 0;
            extractPath = Path.Combine(pathService.RootPath, relativeInstallPath);

            await using var archive = new ZipArchive(memStream, ZipArchiveMode.Read);
            archive.ExtractWithFlatteningSupport(extractPath, true);

            if (installationRegistry.UpsertInstalled(godotRelease, relativeInstallPath) is
                Result<Unit, InstallationRegistryError>.Failure(var upsertError))
            {
                logger.LogError("Failed to update installation registry after installing {ReleaseNameWithRuntime}: {Error}",
                    godotRelease.ReleaseNameWithRuntime, upsertError);

                return new Result<InstallationOutcome, InstallationError>.Failure(
                    new InstallationError.Failed($"Unable to update installation registry for {godotRelease.ReleaseNameWithRuntime}."));
            }

            SymlinkError? symlinkWarning = null;
            if (setAsDefault)
            {
                progress.Report(
                    new OperationProgress<InstallationStage>(InstallationStage.SettingDefault, "Setting as default version..."));
                var installationKey = InstallationRegistry.CreateKey(godotRelease.ReleaseNameWithRuntime, godotRelease.PlatformString);
                if (installationRegistry.SetDefault(installationKey) is Result<Unit, InstallationRegistryError>.Failure(var defaultError))
                {
                    logger.LogError("Failed to set default installation {InstallationKey}: {Error}", installationKey, defaultError);
                    return new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.Failed($"Unable to set default installation for {godotRelease.ReleaseNameWithRuntime}."));
                }

                var fgvmExecutablePath =
                    Path.GetFullPath(System.Environment.ProcessPath ?? System.Environment.GetCommandLineArgs().First());
                if (hostSystem.EnsureShim(fgvmExecutablePath) is Result<Unit, ShimError>.Failure(var shimError))
                {
                    logger.LogWarning("Failed to update shim: {Error}", shimError);
                }

                var symlinkTargetPath = Path.Combine(extractPath, godotRelease.ExecName);
                switch (hostSystem.CreateOrOverwriteShortcut(symlinkTargetPath))
                {
                    case Result<Unit, SymlinkError>.Success:
                        break;
                    case Result<Unit, SymlinkError>.Failure(var symlinkError):
                        logger.LogWarning("Failed to refresh Godot symlink: {Error}", symlinkError);
                        symlinkWarning = symlinkError;
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }
            }

            logger.LogInformation("Successfully installed {ReleaseNameWithRuntime}", godotRelease.ReleaseNameWithRuntime);
            return new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation(godotRelease.ReleaseNameWithRuntime, checksumStatus, symlinkWarning));
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled installation");
            throw;
        }
        catch (Exception e) when (e is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or NotSupportedException
                                      or ArgumentException
                                      or CryptographicException)
        {
            if (!string.IsNullOrWhiteSpace(extractPath) &&
                hostSystem.DeleteDirectoryIfExists(extractPath, true) is Result<Unit, FileOperationError>.Failure(var cleanupError))
            {
                logger.LogWarning("Failed to remove {ExtractPath} after installation error: {Error}", extractPath, cleanupError);
            }
            else if (!string.IsNullOrWhiteSpace(extractPath))
            {
                logger.LogError("Removing {ExtractPath} due to error: {Message}", extractPath, e.Message);
            }

            logger.LogError("Error downloading and installing Godot {ReleaseNameWithRuntime}: {Message}",
                godotRelease.ReleaseNameWithRuntime, e.Message);
            return new Result<InstallationOutcome, InstallationError>.Failure(
                new InstallationError.Failed($"Installation failed for {godotRelease.ReleaseNameWithRuntime}."));
        }
    }

    /// <inheritdoc />
    public async Task<Result<InstallationOutcome, InstallationError>> InstallByQueryAsync(string[] query,
        IProgress<OperationProgress<InstallationStage>> progress,
        bool setAsDefault = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var resolveResult = await ResolveReleaseFromCatalog(query, ReleaseFetchMode.UseCache, cancellationToken);
            if (resolveResult is Result<Release?, InstallationError>.Success { Value: null })
            {
                resolveResult = await ResolveReleaseFromCatalog(query, ReleaseFetchMode.ForceRemote, cancellationToken);
            }

            return resolveResult switch
            {
                Result<Release?, InstallationError>.Success(var release) when release is not null =>
                    await InstallReleaseAsync(release, progress, setAsDefault, cancellationToken),
                Result<Release?, InstallationError>.Success =>
                    new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.NotFound(string.Join(" ", query))),
                Result<Release?, InstallationError>.Failure(var error) =>
                    new Result<InstallationOutcome, InstallationError>.Failure(error),
                _ => throw new InvalidOperationException("Unexpected Result type")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or NotSupportedException
                                      or ArgumentException
                                      or CryptographicException)
        {
            logger.LogError("Installation failed for query {Query}: {Message}", string.Join(" ", query), e.Message);
            return new Result<InstallationOutcome, InstallationError>.Failure(
                new InstallationError.Failed($"Installation failed for query '{string.Join(" ", query)}'."));
        }
    }

    /// <inheritdoc />
    public async Task<Result<string[], NetworkError>> FetchReleaseNames(CancellationToken cancellationToken,
        ReleaseFetchMode fetchMode = ReleaseFetchMode.UseCache
    )
    {
        var releaseIdsResult = await releaseCatalog.ReadReleaseIds(fetchMode, cancellationToken);

        switch (releaseIdsResult)
        {
            case Result<string[], NetworkError>.Failure(var error):
                return error is NetworkError.ManifestRefreshFailure(var cachedReleaseIds)
                    ? new Result<string[], NetworkError>.Failure(
                        new NetworkError.ManifestRefreshFailure(SortReleaseNames(cachedReleaseIds)))
                    : new Result<string[], NetworkError>.Failure(error);

            case Result<string[], NetworkError>.Success(var releaseIds):
                var sortedReleases = SortReleaseNames(releaseIds);

                if (sortedReleases.Length == 0)
                {
                    logger.LogError("Unable to fetch remote releases");
                    return new Result<string[], NetworkError>.Success([]);
                }

                return new Result<string[], NetworkError>.Success(sortedReleases);

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    private string[] SortReleaseNames(IEnumerable<string> releaseIds) =>
        releaseIds
            .Select(name => releaseManager.CreateRelease($"{name}-standard"))
            .Select(result => result switch
            {
                Result<Release, ReleaseParseError>.Success(var release) => release,
                Result<Release, ReleaseParseError>.Failure => null,
                _ => throw new InvalidOperationException("Unexpected Result type")
            })
            .OfType<Release>()
            .OrderByDescending(release => release)
            .Select(r => r.ReleaseName)
            .ToArray();

    private async Task<Result<Release?, InstallationError>> ResolveReleaseFromCatalog(string[] query,
        ReleaseFetchMode fetchMode,
        CancellationToken cancellationToken
    )
    {
        var releaseNamesResult = await FetchReleaseNames(cancellationToken, fetchMode);
        return releaseNamesResult switch
        {
            Result<string[], NetworkError>.Success(var releaseNames) =>
                ResolveReleaseNames(query, releaseNames),
            Result<string[], NetworkError>.Failure(NetworkError.ManifestRefreshFailure(var releaseNames)) =>
                ResolveReleaseNames(query, releaseNames.ToArray()),
            Result<string[], NetworkError>.Failure(var error) =>
                new Result<Release?, InstallationError>.Failure(
                    new InstallationError.Failed($"Failed to fetch releases: {error}")),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private Result<Release?, InstallationError> ResolveReleaseNames(string[] query, string[] releaseNames) =>
        releaseManager.ResolveReleaseQuery(query, releaseNames) switch
        {
            Result<Release, QueryError>.Success(var release) =>
                new Result<Release?, InstallationError>.Success(release),
            Result<Release, QueryError>.Failure(QueryError.NotFound) =>
                new Result<Release?, InstallationError>.Success(null),
            Result<Release, QueryError>.Failure(var error) =>
                new Result<Release?, InstallationError>.Failure(MapQueryError(error, query)),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private static InstallationError MapQueryError(QueryError error, string[] query) => error switch
    {
        QueryError.EmptyQuery => new InstallationError.InvalidQuery("Version query is required."),
        QueryError.InvalidQuery invalid => new InstallationError.InvalidQuery(invalid.Message),
        QueryError.NotFound notFound => new InstallationError.NotFound(notFound.Query),
        _ => new InstallationError.NotFound(string.Join(" ", query))
    };

    private static async Task<string> CalculateChecksum(MemoryStream memStream, CancellationToken cancellationToken)
    {
        using var sha512Hash = SHA512.Create();
        var hashBytes = await sha512Hash.ComputeHashAsync(memStream, cancellationToken);
        var checksum = Convert.ToHexStringLower(hashBytes);
        return checksum;
    }

    private async Task<Result<ChecksumVerification, InstallationError>> VerifyChecksum(string zipFileName,
        ReleaseArtifact artifact,
        MemoryStream memStream,
        IProgress<OperationProgress<InstallationStage>> progress,
        CancellationToken cancellationToken
    )
    {
        if (artifact.Sha512 is not { } expectedHash)
        {
            logger.LogWarning("Checksum unavailable for {FileName}. Installation will continue without verification", zipFileName);
            return new Result<ChecksumVerification, InstallationError>.Success(new ChecksumVerification.Unavailable());
        }

        progress.Report(new OperationProgress<InstallationStage>(InstallationStage.VerifyingChecksum, "Verifying checksum..."));
        memStream.Position = 0;
        var calculatedChecksum = await CalculateChecksum(memStream, cancellationToken);

        if (!calculatedChecksum.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Checksum mismatch for {FileName}. Expected: {Expected}, Actual: {Actual}",
                zipFileName, expectedHash, calculatedChecksum);

            return new Result<ChecksumVerification, InstallationError>.Failure(
                new InstallationError.ChecksumMismatch(expectedHash, calculatedChecksum, zipFileName));
        }

        logger.LogInformation("Checksum verified successfully for {FileName}", zipFileName);
        return new Result<ChecksumVerification, InstallationError>.Success(new ChecksumVerification.Verified());
    }
}
