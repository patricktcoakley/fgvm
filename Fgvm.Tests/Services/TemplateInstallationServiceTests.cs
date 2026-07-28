using System.IO.Compression;
using System.Security.Cryptography;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Godot.Download;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Tests.TestSupport;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Services;

public sealed class TemplateInstallationServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "fgvm-template-install-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_ExtractsTemplateArchiveToGodotTemplateDirectory()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable");
        var service = CreateService(release, archive, out var templatesRoot);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var success = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        var installation = Assert.IsType<TemplateInstallationOutcome.NewInstallation>(success.Value);
        Assert.Equal("4.4.stable", installation.TemplateVersion);
        Assert.True(File.Exists(Path.Combine(templatesRoot, "4.4.stable", "linux_release.x86_64")));
        Assert.Equal("template", await File.ReadAllTextAsync(Path.Combine(templatesRoot, "4.4.stable", "linux_release.x86_64")));
    }

    [Fact]
    public async Task InstallAsync_ReturnsAlreadyInstalledWithoutDownload_WhenDirectoryExistsAndForceIsFalse()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchive("4.4.stable"), out var templatesRoot);
        Directory.CreateDirectory(Path.Combine(templatesRoot, "4.4.stable"));
        var progress = new RecordingProgress<TemplateInstallationStage>();

        var result = await service.InstallAsync(release, progress);

        var success = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.IsType<TemplateInstallationOutcome.AlreadyInstalled>(success.Value);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task InstallAsync_ReturnsAlreadyInstalled_WhenDestinationAppearsDuringInstall()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchive("4.4.stable"), out var templatesRoot);
        var destination = Path.Combine(templatesRoot, "4.4.stable");
        var progress = new RecordingProgress<TemplateInstallationStage>();
        // Simulate a concurrent install finishing between the up-front existence check and the commit.
        progress.Reported += report =>
        {
            if (report.Stage.Equals(TemplateInstallationStage.Extracting) && !Directory.Exists(destination))
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "existing.txt"), "existing");
            }
        };

        var result = await service.InstallAsync(release, progress);

        var success = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.IsType<TemplateInstallationOutcome.AlreadyInstalled>(success.Value);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "existing.txt")));
        Assert.Empty(Directory.GetDirectories(templatesRoot, ".fgvm-template-staging-*"));
        Assert.Empty(Directory.GetDirectories(templatesRoot, "*.backup-*"));
    }

    [Fact]
    public async Task InstallAsync_ReturnsFailed_WhenCommitFailsBecauseDestinationIsBlockedByAFile()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchive("4.4.stable"), out var templatesRoot);
        var destination = Path.Combine(templatesRoot, "4.4.stable");
        Directory.CreateDirectory(templatesRoot);
        await File.WriteAllTextAsync(destination, "blocker");

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("Unable to install export templates", failed.Reason);
        Assert.Equal("blocker", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetDirectories(templatesRoot, ".fgvm-template-staging-*"));
        Assert.Empty(Directory.GetDirectories(templatesRoot, "*.backup-*"));
    }

    [Fact]
    public async Task InstallAsync_ReplacesExistingTemplateDirectory_WhenForceIsTrue()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchive("4.4.stable"), out var templatesRoot);
        var destination = Path.Combine(templatesRoot, "4.4.stable");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "old.txt"), "old");

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>(), force: true);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.False(File.Exists(Path.Combine(destination, "old.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "linux_release.x86_64")));
    }

    [Fact]
    public async Task InstallAsync_FailsWhenChecksumDoesNotMatch()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable");
        var service = CreateService(release, archive, out _, sha512: new string('0', 128));

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        Assert.IsType<TemplateInstallationError.ChecksumMismatch>(failure.Error);
    }

    [Fact]
    public async Task InstallAsync_FailsWhenArchiveVersionDoesNotMatchRelease()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchive("4.5.stable"), out _);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("does not match", failed.Reason);
    }

    [Fact]
    public async Task InstallAsync_FailsWhenVersionTxtIsMalformed()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, ZipArchiveTestBuilder.CreateArchive(("templates/version.txt", "not-a-version")), out _);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("Invalid version.txt format", failed.Reason);
    }

    [Fact]
    public async Task InstallAsync_FailsWhenVersionTxtIsMissing()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, ZipArchiveTestBuilder.CreateArchive(("templates/notversion.txt", "4.4.stable")), out _);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("No version.txt", failed.Reason);
    }

    [Fact]
    public async Task InstallAsync_RejectsZipSlipEntries()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchive("4.4.stable", includeZipSlip: true), out _);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("outside the target directory", failed.Reason);
    }

    [Fact]
    public async Task InstallAsync_RejectsZipSlipVersionEntry()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, ZipArchiveTestBuilder.CreateArchive(("../version.txt", "4.4.stable")), out _);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("outside the target directory", failed.Reason);
    }

    [Fact]
    public async Task InstallAsync_PreservesUnixExecutableBits_WhenAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateTemplateArchiveWithExecutableMode("4.4.stable"), out var templatesRoot);
        var templatePath = Path.Combine(templatesRoot, "4.4.stable", "linux_release.x86_64");

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.True(File.GetUnixFileMode(templatePath).HasFlag(UnixFileMode.UserExecute));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InstallAsync_ReportsSourceDetailsOnlyWhenVerbose(bool verbose, bool expectsSourceDetail)
    {
        const string sourceUrl = "https://downloads.example.test/templates.zip";
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(
            release,
            CreateTemplateArchive("4.4.stable"),
            out _,
            sourceUrl: sourceUrl);
        var progress = new RecordingProgress<TemplateInstallationStage>();

        var result = await service.InstallAsync(release, progress, verbose: verbose);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.Equal(expectsSourceDetail, progress.Reports.Any(report =>
            report.IsVerboseDetail && report.Message == $"Downloading from {sourceUrl}..."));
        Assert.Contains(progress.Reports, report =>
            !report.IsVerboseDetail &&
            report.Stage == TemplateInstallationStage.Downloading &&
            report.Message.Contains(" MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_CleansTemporaryArchiveDirectory_WhenDownloadFails()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable", payloadBytes: 2 * 1024 * 1024);
        var before = Directory.GetDirectories(Path.GetTempPath(), "fgvm-template-*").ToHashSet(StringComparer.Ordinal);
        var service = CreateService(release, archive, out var templatesRoot, downloadFails: true);

        var result = await service.InstallAsync(release, new RecordingProgress<TemplateInstallationStage>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("Download failed", failed.Reason);
        Assert.False(Directory.Exists(Path.Combine(templatesRoot, "4.4.stable")));
        var after = Directory.GetDirectories(Path.GetTempPath(), "fgvm-template-*").ToHashSet(StringComparer.Ordinal);
        Assert.Subset(before, after);
        Assert.Subset(after, before);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private TemplateInstallationService CreateService(Release release,
        byte[] archive,
        out string templatesRoot,
        string? sha512 = null,
        bool checksumUnavailable = false,
        bool downloadFails = false,
        string? sourceUrl = null
    )
    {
        templatesRoot = Path.Combine(_rootPath, "export_templates");
        var templatesRootPath = templatesRoot;
        var artifact = new ReleaseArtifact(
            ReleaseCatalog.GetExportTemplateFileName(release),
            checksumUnavailable ? null : sha512 ?? Sha512(archive));

        var catalog = new Mock<IReleaseCatalog>();
        catalog.Setup(x => x.FindOrHydrateExportTemplateArtifact(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<ReleaseArtifact, NetworkError>.Success(artifact));

        var releaseManager = new Mock<IReleaseManager>();
        if (downloadFails)
        {
            releaseManager.Setup(x => x.DownloadZipFileAsync(
                    artifact.FileName, release, It.IsAny<string>(), It.IsAny<IProgress<DownloadProgress>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Result<string, NetworkError>.Failure(
                    new NetworkError.ConnectionFailure("Simulated download failure.")));
        }
        else
        {
            // Model the download contract; transport behavior is covered by downloader tests.
            releaseManager.Setup(x => x.DownloadZipFileAsync(
                    artifact.FileName, release, It.IsAny<string>(), It.IsAny<IProgress<DownloadProgress>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (string _, Release _, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (sourceUrl is not null)
                    {
                        progress?.Report(new DownloadProgress(0, null, sourceUrl));
                    }

                    await File.WriteAllBytesAsync(destinationPath, archive, ct);
                    progress?.Report(new DownloadProgress(archive.Length, archive.Length));
                    return new Result<string, NetworkError>.Success(Sha512(archive));
                });
        }

        var godotPathService = new Mock<IGodotPathService>();
        godotPathService.SetupGet(x => x.ExportTemplatesRootPath).Returns(templatesRootPath);
        godotPathService.Setup(x => x.GetExportTemplateVersionPath(It.IsAny<string>()))
            .Returns((string version) => Path.Combine(templatesRootPath, version));

        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(_rootPath);
        var hostSystem = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);

        return new TemplateInstallationService(
            hostSystem,
            releaseManager.Object,
            catalog.Object,
            godotPathService.Object,
            NullLogger<TemplateInstallationService>.Instance);
    }

    private static Release CreateRelease(string releaseNameWithRuntime)
    {
        var release = Release.TryParse(releaseNameWithRuntime);
        Assert.NotNull(release);
        return release;
    }

    private static byte[] CreateTemplateArchive(string templateVersion, bool includeZipSlip = false, int payloadBytes = 0)
    {
        var entries = new List<(string Name, string Content)>
        {
            ("templates/version.txt", templateVersion),
            ("templates/linux_release.x86_64", "template")
        };

        if (includeZipSlip)
        {
            entries.Add(("templates/../escape.txt", "escape"));
        }

        return ZipArchiveTestBuilder.CreateArchive(entries.ToArray(), "templates/payload.bin", payloadBytes);
    }

    private static byte[] CreateTemplateArchiveWithExecutableMode(string templateVersion)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            ZipArchiveTestBuilder.AddEntry(archive, "templates/version.txt", templateVersion);
            var executable = ZipArchiveTestBuilder.AddEntry(archive, "templates/linux_release.x86_64", "template");
            executable.ExternalAttributes =
                (int)(UnixFileMode.UserRead |
                      UnixFileMode.UserWrite |
                      UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead |
                      UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead |
                      UnixFileMode.OtherExecute) << 16;
        }

        return stream.ToArray();
    }

    private static string Sha512(byte[] bytes)
    {
        using var sha512 = SHA512.Create();
        return Convert.ToHexStringLower(sha512.ComputeHash(bytes));
    }

    private sealed class RecordingProgress<TStage> : IProgress<OperationProgress<TStage>> where TStage : Enum
    {
        public event Action<OperationProgress<TStage>>? Reported;
        public List<OperationProgress<TStage>> Reports { get; } = [];

        public void Report(OperationProgress<TStage> value)
        {
            Reports.Add(value);
            Reported?.Invoke(value);
        }
    }
}
