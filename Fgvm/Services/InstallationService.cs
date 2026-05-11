using Fgvm.Environment;
using Fgvm.Extensions;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Security.Cryptography;

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

public interface IInstallationService
{
    Task<Result<InstallationOutcome, InstallationError>> InstallReleaseAsync(Release godotRelease,
        IProgress<OperationProgress<InstallationStage>> progress, bool setAsDefault = true,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationOutcome, InstallationError>> InstallByQueryAsync(string[] query,
        IProgress<OperationProgress<InstallationStage>> progress, bool setAsDefault = false,
        CancellationToken cancellationToken = default);

    Task<string[]> FetchReleaseNames(CancellationToken cancellationToken, ReleaseFetchMode fetchMode = ReleaseFetchMode.UseCache);
}

public class InstallationService(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IReleaseCatalog releaseCatalog,
    IPathService pathService,
    ILogger<InstallationService> logger)
    : IInstallationService
{
    /// <summary>
    ///     Tries to install a Godot release.
    /// </summary>
    /// <param name="godotRelease">The release to install</param>
    /// <param name="progress">Progress reporter for installation updates</param>
    /// <param name="setAsDefault">Whether to set this version as the global default</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The installation result if successful, null if the installation failed.</returns>
    public async Task<Result<InstallationOutcome, InstallationError>> InstallReleaseAsync(Release godotRelease,
        IProgress<OperationProgress<InstallationStage>> progress, bool setAsDefault = true,
        CancellationToken cancellationToken = default)
    {
        var extractPath = "";

        try
        {
            var installPathBase = godotRelease.ReleaseNameWithRuntime;

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Initializing, $"Initializing installation of {installPathBase}..."));

            // Check if already installed
            var existingPath = Path.Combine(pathService.RootPath, installPathBase);
            if (Directory.Exists(existingPath))
            {
                logger.LogInformation("Version {InstallPathBase} is already installed", installPathBase);
                return new Result<InstallationOutcome, InstallationError>.Success(
                    new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
            }

            // Show progress immediately before HTTP request
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase}..."));

            const int bufferSize = 32768;
            ReleaseArtifact artifact;
            switch (await releaseCatalog.FindOrHydrateArtifact(godotRelease, cancellationToken))
            {
                case Result<ReleaseArtifact, NetworkError>.Success success:
                    artifact = success.Value;
                    break;
                case Result<ReleaseArtifact, NetworkError>.Failure failure:
                    return new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.Failed($"Release catalog hydration failed for {godotRelease.ReleaseName}: {failure.Error}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            var zipFileName = artifact.FileName;
            var responseResult = await releaseManager.GetZipFile(zipFileName, godotRelease, cancellationToken);

            HttpResponseMessage response;
            switch (responseResult)
            {
                case Result<HttpResponseMessage, NetworkError>.Success downloadSuccess:
                    response = downloadSuccess.Value;
                    break;
                case Result<HttpResponseMessage, NetworkError>.Failure downloadFailure:
                    return new Result<InstallationOutcome, InstallationError>.Failure(
                        new InstallationError.Failed($"Download failed for {zipFileName}: {downloadFailure.Error}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            using var responseOwner = response;

            var contentLength = response.Content.Headers.ContentLength ?? 0;

            await using var memStream = new MemoryStream(checked((int)contentLength));

            // Download the release with dedicated progress context
            cancellationToken.ThrowIfCancellationRequested();

            await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var buffer = new byte[bufferSize];
            int bytesRead;
            var totalDownloaded = 0L;
            var lastProgressUpdate = 0L;
            var startTime = DateTime.UtcNow;

            while ((bytesRead = await networkStream.ReadAsync(buffer, cancellationToken)) > 0)
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
                case Result<ChecksumVerification, InstallationError>.Success checksumSuccess:
                    checksumStatus = checksumSuccess.Value;
                    break;
                case Result<ChecksumVerification, InstallationError>.Failure checksumFailure:
                    return new Result<InstallationOutcome, InstallationError>.Failure(checksumFailure.Error);
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Extracting, "Extracting files..."));
            memStream.Position = 0;
            extractPath = Path.Combine(pathService.RootPath, installPathBase);

            await using var archive = new ZipArchive(memStream, ZipArchiveMode.Read);
            archive.ExtractWithFlatteningSupport(extractPath, true);

            SymlinkError? symlinkWarning = null;
            if (setAsDefault)
            {
                progress.Report(new OperationProgress<InstallationStage>(InstallationStage.SettingDefault, "Setting as default version..."));
                var symlinkTargetPath = Path.Combine(extractPath, godotRelease.ExecName);
                var symlinkResult = hostSystem.CreateOrOverwriteSymbolicLink(symlinkTargetPath);

                switch (symlinkResult)
                {
                    case Result<Unit, SymlinkError>.Success:
                        break;
                    case Result<Unit, SymlinkError>.Failure failure:
                        logger.LogError("Failed to create symlink: {Error}", failure.Error);
                        hostSystem.RemoveSymbolicLinks();
                        symlinkWarning = failure.Error;
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
        catch (Exception e)
        {
            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, true);
                logger.LogError("Removing {ExtractPath} due to error: {Message}", extractPath, e.Message);
            }

            logger.LogError("Error downloading and installing Godot {ReleaseNameWithRuntime}: {Message}", godotRelease.ReleaseNameWithRuntime, e.Message);
            throw;
        }
    }

    /// <summary>
    ///     Tries to find and install a version matching the query.
    /// </summary>
    /// <param name="query">Version query arguments</param>
    /// <param name="progress">Progress reporter for installation updates</param>
    /// <param name="setAsDefault">Whether to set the installed version as the global default</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The installation result if successful, null if installation failed.</returns>
    public async Task<Result<InstallationOutcome, InstallationError>> InstallByQueryAsync(string[] query,
        IProgress<OperationProgress<InstallationStage>> progress, bool setAsDefault = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var releaseNames = await FetchReleaseNames(cancellationToken);
            var godotRelease = releaseManager.TryFindReleaseByQuery(query, releaseNames);

            // Retry with remote fetch if not found
            godotRelease ??= releaseManager.TryFindReleaseByQuery(query, await FetchReleaseNames(cancellationToken, ReleaseFetchMode.ForceRemote));

            return godotRelease == null
                ? new Result<InstallationOutcome, InstallationError>.Failure(
                    new InstallationError.NotFound(string.Join(" ", query)))
                : await InstallReleaseAsync(godotRelease, progress, setAsDefault, cancellationToken);
        }
        catch (Exception e)
        {
            return new Result<InstallationOutcome, InstallationError>.Failure(
                new InstallationError.Failed($"Installation failed for query '{string.Join(" ", query)}': {e.Message}"));
        }
    }

    public async Task<string[]> FetchReleaseNames(CancellationToken cancellationToken, ReleaseFetchMode fetchMode = ReleaseFetchMode.UseCache)
    {
        var releaseIds = await releaseCatalog.ReadReleaseIds(fetchMode, cancellationToken) switch
        {
            Result<string[], NetworkError>.Success success => success.Value,
            Result<string[], NetworkError>.Failure failure => throw new HttpRequestException($"Failed to fetch releases: {failure.Error}"),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

        var sortedReleases = releaseIds
            .Select(name => releaseManager.TryCreateRelease($"{name}-standard"))
            .OfType<Release>()
            .OrderByDescending(r => r)
            .Select(r => r.ReleaseName)
            .ToArray();

        if (sortedReleases.Length == 0)
        {
            logger.LogError("Unable to fetch remote releases");
            return [];
        }

        return sortedReleases;
    }

    private static async Task<string> CalculateChecksum(MemoryStream memStream, CancellationToken cancellationToken)
    {
        using var sha512Hash = SHA512.Create();
        var hashBytes = await sha512Hash.ComputeHashAsync(memStream, cancellationToken);
        var checksum = Convert.ToHexStringLower(hashBytes);
        return checksum;
    }

    private async Task<Result<ChecksumVerification, InstallationError>> VerifyChecksum(
        string zipFileName,
        ReleaseArtifact artifact,
        MemoryStream memStream,
        IProgress<OperationProgress<InstallationStage>> progress,
        CancellationToken cancellationToken)
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
