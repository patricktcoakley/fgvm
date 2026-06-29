using System.IO.Compression;
using System.Security.Cryptography;
using Fgvm.Environment;
using Fgvm.Godot;
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
        CancellationToken cancellationToken = default
    )
    {
        var expectedTemplateVersion = TemplateInstallation.ToTemplateVersion(release);
        var destinationPath = godotPathService.GetExportTemplateVersionPath(expectedTemplateVersion);
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

            ZipDownload download;
            switch (await releaseManager.GetZipFile(artifact.FileName, release, cancellationToken))
            {
                case Result<ZipDownload, NetworkError>.Success(var zipDownload):
                    download = zipDownload;
                    break;
                case Result<ZipDownload, NetworkError>.Failure(var error):
                    return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                        new TemplateInstallationError.Failed($"Download failed for {artifact.FileName}: {error}"));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            await using var downloadOwner = download;
            await using var memStream = await ReadDownload(download, progress, cancellationToken);

            var checksumResult = await VerifyChecksum(artifact, memStream, progress, cancellationToken);
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

            Directory.CreateDirectory(tempRoot);
            var extractPath = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractPath);

            memStream.Position = 0;
            await using var archive = new ZipArchive(memStream, ZipArchiveMode.Read);
            var templateVersionResult = ExtractTemplateArchive(archive, extractPath);
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

            if (force &&
                hostSystem.DeleteDirectoryIfExists(destinationPath, true) is Result<Unit, FileOperationError>.Failure(var deleteError))
            {
                return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed($"Unable to replace existing export templates: {deleteError}"));
            }

            Directory.Move(extractPath, destinationPath);

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
            CleanupTempDirectory(tempRoot);
        }
    }

    private static async Task<MemoryStream> ReadDownload(ZipDownload download,
        IProgress<OperationProgress<TemplateInstallationStage>> progress,
        CancellationToken cancellationToken
    )
    {
        var contentLength = download.ContentLength ?? (download.Stream.CanSeek ? download.Stream.Length : 0);
        var memStream = contentLength > 0 && contentLength <= int.MaxValue
            ? new MemoryStream(checked((int)contentLength))
            : new MemoryStream();

        const int bufferSize = 32768;
        var buffer = new byte[bufferSize];
        var totalDownloaded = 0L;
        int bytesRead;
        while ((bytesRead = await download.Stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await memStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalDownloaded += bytesRead;

            if (contentLength > 0)
            {
                progress.Report(new OperationProgress<TemplateInstallationStage>(
                    TemplateInstallationStage.Downloading,
                    $"Downloading export templates • {totalDownloaded / 1024.0 / 1024.0:F1}/{contentLength / 1024.0 / 1024.0:F1} MB"));
            }
        }

        memStream.Position = 0;
        return memStream;
    }

    private async Task<Result<ChecksumVerification, TemplateInstallationError>> VerifyChecksum(ReleaseArtifact artifact,
        MemoryStream memStream,
        IProgress<OperationProgress<TemplateInstallationStage>> progress,
        CancellationToken cancellationToken
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

        memStream.Position = 0;
        using var sha512Hash = SHA512.Create();
        var hashBytes = await sha512Hash.ComputeHashAsync(memStream, cancellationToken);
        var actualHash = Convert.ToHexStringLower(hashBytes);
        memStream.Position = 0;

        return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
            ? new Result<ChecksumVerification, TemplateInstallationError>.Success(new ChecksumVerification.Verified())
            : new Result<ChecksumVerification, TemplateInstallationError>.Failure(
                new TemplateInstallationError.ChecksumMismatch(expectedHash, actualHash, artifact.FileName));
    }

    private static Result<string, TemplateInstallationError> ExtractTemplateArchive(ZipArchive archive, string extractPath)
    {
        foreach (var entry in archive.Entries.Where(entry => !IsIgnoredEntry(entry.FullName)))
        {
            var normalizedPath = NormalizeEntryPath(entry.FullName);
            if (IsUnsafeEntryPath(normalizedPath))
            {
                return new Result<string, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed($"Archive entry is outside the target directory: {entry.FullName}"));
            }
        }

        var versionEntry = archive.Entries.FirstOrDefault(entry =>
            !IsIgnoredEntry(entry.FullName) &&
            GetEntryFileName(NormalizeEntryPath(entry.FullName)).Equals("version.txt", StringComparison.OrdinalIgnoreCase));

        if (versionEntry is null)
        {
            return new Result<string, TemplateInstallationError>.Failure(
                new TemplateInstallationError.Failed("No version.txt found inside the export templates archive."));
        }

        var templateVersion = ReadEntryText(versionEntry).Trim();
        if (TemplateInstallation.TryCreate(templateVersion, extractPath) is null)
        {
            return new Result<string, TemplateInstallationError>.Failure(
                new TemplateInstallationError.Failed($"Invalid version.txt format inside the export templates archive: {templateVersion}."));
        }

        var contentsDir = GetBaseDirectory(NormalizeEntryPath(versionEntry.FullName));
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || IsIgnoredEntry(entry.FullName))
            {
                continue;
            }

            var relativePath = GetRelativeTemplatePath(NormalizeEntryPath(entry.FullName), contentsDir);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = GetSafeDestinationPath(extractPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, true);
            PreserveUnixPermissions(entry, destinationPath);
        }

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

    private static string GetSafeDestinationPath(string extractPath, string relativePath)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(extractPath, relativePath));
        var rootPath = Path.GetFullPath(extractPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                        Path.DirectorySeparatorChar);

        if (!destinationPath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry is outside the target directory: {relativePath}");
        }

        return destinationPath;
    }

    private static void PreserveUnixPermissions(ZipArchiveEntry entry, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var unixMode = (entry.ExternalAttributes >> 16) & 0x01FF;
        if (unixMode == 0)
        {
            return;
        }

        File.SetUnixFileMode(destinationPath, (UnixFileMode)unixMode);
    }

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
