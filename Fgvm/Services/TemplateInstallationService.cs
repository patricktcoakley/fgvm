using System.IO.Compression;
using System.Security.Cryptography;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Godot.Download;
using Fgvm.Progress;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Services;

public enum TemplateInstallationStage
{
    Initializing,
    Downloading,
    VerifyingChecksum,
    Extracting,
    Installing
}

public interface ITemplateInstallationService
{
    Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(Release release,
        IProgress<OperationProgress<TemplateInstallationStage>> progress,
        bool force = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    );
}

public sealed class TemplateInstallationService(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IReleaseCatalog releaseCatalog,
    IGodotPathService godotPathService,
    ILogger<TemplateInstallationService> logger
) : ITemplateInstallationService
{
    public async Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(Release release,
        IProgress<OperationProgress<TemplateInstallationStage>> progress,
        bool force = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    )
    {
        var expectedTemplateVersion = TemplateInstallation.ToTemplateVersion(release);
        var destinationPath = godotPathService.GetExportTemplateVersionPath(expectedTemplateVersion);
        var stagingPath = "";
        var tempRoot = Path.Combine(Path.GetTempPath(), $"fgvm-template-{Guid.NewGuid():N}");

        try
        {
            progress.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Initializing,
                $"Initializing export template installation for {release.ReleaseNameWithRuntime}..."));

            if (!force && hostSystem.DirectoryExists(destinationPath) is Result<bool, FileOperationError>.Success { Value: true })
            {
                return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                    new TemplateInstallationOutcome.AlreadyInstalled(expectedTemplateVersion, destinationPath));
            }

            ReleaseArtifact artifact;
            switch (await releaseCatalog.FindOrHydrateExportTemplateArtifact(release, cancellationToken))
            {
                case Result<ReleaseArtifact, NetworkError>.Success(var releaseArtifact):
                    artifact = releaseArtifact;
                    break;
                case Result<ReleaseArtifact, NetworkError>.Failure(var error):
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                        new TemplateInstallationError.Failed(
                            $"Release catalog hydration failed for export templates {release.ReleaseName}: {error}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            progress.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Downloading,
                $"Downloading {artifact.FileName}..."));

            Directory.CreateDirectory(tempRoot);
            var archivePath = Path.Combine(tempRoot, Path.GetFileName(artifact.FileName));

            string archiveChecksum;
            switch (await releaseManager.DownloadZipFileAsync(
                        artifact.FileName, release, archivePath, new DownloadProgressAdapter(progress, verbose), cancellationToken))
            {
                case Result<string, NetworkError>.Success(var checksum):
                    archiveChecksum = checksum;
                    break;
                case Result<string, NetworkError>.Failure(var error):
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                        new TemplateInstallationError.Failed($"Download failed for {artifact.FileName}: {error}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            var checksumResult = VerifyChecksum(artifact, archiveChecksum, progress);
            ChecksumVerification checksumStatus;
            switch (checksumResult)
            {
                case Result<ChecksumVerification, TemplateInstallationError>.Success(var checksumVerification):
                    checksumStatus = checksumVerification;
                    break;
                case Result<ChecksumVerification, TemplateInstallationError>.Failure(var error):
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(error);
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            progress.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Extracting,
                "Extracting export templates..."));

            // Keep staging beside the destination so the commit is a same-volume rename.
            var templatesDirectory = Path.GetDirectoryName(destinationPath)
                                     ?? throw new InvalidOperationException($"Template path has no parent: {destinationPath}");
            Directory.CreateDirectory(templatesDirectory);
            stagingPath = Path.Combine(templatesDirectory, $".fgvm-template-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingPath);

            var templateVersionResult = await ExtractTemplateArchiveAsync(archivePath, stagingPath, cancellationToken);
            string actualTemplateVersion;
            switch (templateVersionResult)
            {
                case Result<string, TemplateInstallationError>.Success(var templateVersion):
                    actualTemplateVersion = templateVersion;
                    break;
                case Result<string, TemplateInstallationError>.Failure(var error):
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(error);
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            if (!string.Equals(expectedTemplateVersion, actualTemplateVersion, StringComparison.OrdinalIgnoreCase))
            {
                return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed(
                        $"Template archive version {actualTemplateVersion} does not match requested {expectedTemplateVersion}."));
            }

            progress.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Installing,
                $"Installing export templates to {destinationPath}..."));

            if (hostSystem.CreateDirectory(godotPathService.ExportTemplatesRootPath) is
                Result<Unit, FileOperationError>.Failure(var createError))
            {
                return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed($"Unable to create export template directory: {createError}"));
            }

            switch (StagedDirectoryCommitter.Commit(stagingPath, destinationPath, force, logger))
            {
                case Result<Unit, DirectoryCommitError>.Success:
                    break;
                case Result<Unit, DirectoryCommitError>.Failure(DirectoryCommitError.DestinationExists):
                    // Another process finished installing these templates while this one was downloading.
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                        new TemplateInstallationOutcome.AlreadyInstalled(expectedTemplateVersion, destinationPath));
                case Result<Unit, DirectoryCommitError>.Failure(var commitError):
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                        new TemplateInstallationError.Failed($"Unable to install export templates: {commitError}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.NewInstallation(actualTemplateVersion, destinationPath, checksumStatus));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException
                                       or NotSupportedException
                                       or ArgumentException
                                       or CryptographicException)
        {
            logger.LogError(ex, "Export template installation failed for {ReleaseName}", release.ReleaseNameWithRuntime);
            return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                new TemplateInstallationError.Failed(
                    $"Export template installation failed for {release.ReleaseNameWithRuntime}: {ex.Message}"));
        }
        finally
        {
            CleanupTempDirectory(stagingPath);
            CleanupTempDirectory(tempRoot);
        }
    }

    private sealed class DownloadProgressAdapter(
        IProgress<OperationProgress<TemplateInstallationStage>> progress,
        bool verbose
    )
        : IProgress<DownloadProgress>
    {
        private readonly DateTime _startTime = DateTime.UtcNow;

        public void Report(DownloadProgress value)
        {
            if (value.SourceUrl is { } sourceUrl)
            {
                if (verbose)
                {
                    progress.Report(new OperationProgress<TemplateInstallationStage>(
                        TemplateInstallationStage.Downloading,
                        $"Downloading from {sourceUrl}...",
                        IsVerboseDetail: true));
                }

                return;
            }

            if (value.TotalBytes is not { } totalBytes || totalBytes <= 0)
            {
                return;
            }

            var downloadedMB = value.BytesDownloaded / 1024.0 / 1024.0;
            var totalMB = totalBytes / 1024.0 / 1024.0;

            var elapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;
            var speedText = "";
            if (elapsedSeconds > 0.5)
            {
                var speedMBps = downloadedMB / elapsedSeconds;
                speedText = speedMBps >= 1.0
                    ? $" • {speedMBps:F1} MB/s"
                    : $" • {speedMBps * 1024:F0} KB/s";
            }

            progress.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Downloading,
                $"Downloading export templates • {downloadedMB:F1}/{totalMB:F1} MB{speedText}"));
        }
    }

    private Result<ChecksumVerification, TemplateInstallationError> VerifyChecksum(ReleaseArtifact artifact,
        string actualHash,
        IProgress<OperationProgress<TemplateInstallationStage>> progress
    )
    {
        if (artifact.Sha512 is not { } expectedHash)
        {
            logger.LogWarning("Checksum unavailable for {FileName}. Installation will continue without verification", artifact.FileName);
            return new Result<ChecksumVerification, TemplateInstallationError>.Success(new ChecksumVerification.Unavailable());
        }

        progress.Report(new OperationProgress<TemplateInstallationStage>(
            TemplateInstallationStage.VerifyingChecksum,
            "Verifying checksum..."));

        return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
            ? new Result<ChecksumVerification, TemplateInstallationError>.Success(new ChecksumVerification.Verified())
            : new Result<ChecksumVerification, TemplateInstallationError>.Failure(
                new TemplateInstallationError.ChecksumMismatch(expectedHash, actualHash, artifact.FileName));
    }

    private static async Task<Result<string, TemplateInstallationError>> ExtractTemplateArchiveAsync(string archivePath,
        string extractPath,
        CancellationToken cancellationToken
    )
    {
        string templateVersion;
        ZipExtractionWorkItem[] entries;

        await using (var archiveStream = ParallelZipExtractor.OpenArchiveStream(archivePath))
        await using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
        {
            ZipArchiveEntry? versionEntry = null;
            var versionEntryPath = "";
            var fileEntries = new List<(int EntryIndex, string NormalizedPath)>(archive.Entries.Count);

            for (var index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                if (IsIgnoredEntry(entry.FullName))
                {
                    continue;
                }

                var normalizedPath = NormalizeEntryPath(entry.FullName);
                if (IsUnsafeEntryPath(normalizedPath))
                {
                    return new Result<string, TemplateInstallationError>.Failure(
                        new TemplateInstallationError.Failed($"Archive entry is outside the target directory: {entry.FullName}"));
                }

                if (versionEntry is null &&
                    GetEntryFileName(normalizedPath).Equals("version.txt", StringComparison.OrdinalIgnoreCase))
                {
                    versionEntry = entry;
                    versionEntryPath = normalizedPath;
                }

                if (!string.IsNullOrEmpty(entry.Name))
                {
                    fileEntries.Add((index, normalizedPath));
                }
            }

            if (versionEntry is null)
            {
                return new Result<string, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed("No version.txt found inside the export templates archive."));
            }

            templateVersion = ReadEntryText(versionEntry).Trim();
            if (TemplateInstallation.TryCreate(templateVersion, extractPath) is null)
            {
                return new Result<string, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed(
                        $"Invalid version.txt format inside the export templates archive: {templateVersion}."));
            }

            var contentsDir = GetBaseDirectory(versionEntryPath);
            entries = fileEntries
                .Select(item => new ZipExtractionWorkItem(
                    item.EntryIndex,
                    GetRelativeTemplatePath(item.NormalizedPath, contentsDir),
                    IsDirectory: false,
                    ApplyUnixPermissions: true))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
                .ToArray();
        }

        await ParallelZipExtractor.ExtractAsync(
            archivePath,
            extractPath,
            entries,
            overwrite: true,
            cancellationToken);

        return new Result<string, TemplateInstallationError>.Success(templateVersion);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizeEntryPath(string entryPath) =>
        entryPath.Replace('\\', '/');

    private static bool IsIgnoredEntry(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        return normalized.StartsWith("__MACOSX/", StringComparison.Ordinal) ||
               normalized.Equals("__MACOSX", StringComparison.Ordinal);
    }

    private static bool IsUnsafeEntryPath(string entryPath)
    {
        if (entryPath.StartsWith("/", StringComparison.Ordinal) ||
            entryPath.Contains(":/", StringComparison.Ordinal) ||
            entryPath.Split('/').Any(segment => segment == ".."))
        {
            return true;
        }

        return false;
    }

    private static string GetBaseDirectory(string entryPath)
    {
        var index = entryPath.LastIndexOf('/');
        return index < 0 ? "" : entryPath[..index].Trim('/');
    }

    private static string GetEntryFileName(string entryPath)
    {
        var index = entryPath.LastIndexOf('/');
        return index < 0 ? entryPath : entryPath[(index + 1)..];
    }

    private static string GetRelativeTemplatePath(string entryPath, string contentsDir)
    {
        if (string.IsNullOrEmpty(contentsDir))
        {
            return entryPath;
        }

        return entryPath.StartsWith($"{contentsDir}/", StringComparison.Ordinal)
            ? entryPath[(contentsDir.Length + 1)..]
            : "";
    }

    /// <summary>
    ///     Removes the temporary archive and extraction directory created for the install attempt.
    /// </summary>
    /// <param name="tempRoot">The temporary directory to remove.</param>
    private void CleanupTempDirectory(string tempRoot)
    {
        if (string.IsNullOrWhiteSpace(tempRoot) || !Directory.Exists(tempRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(tempRoot, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to clean up temporary template install directory {TempRoot}", tempRoot);
        }
    }
}
