using System.IO.Compression;
using System.Security.Cryptography;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
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

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var success = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.IsType<TemplateInstallationOutcome.AlreadyInstalled>(success.Value);
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
        var service = CreateService(release, CreateArchiveWithEntries(("templates/version.txt", "not-a-version")), out _);

        var result = await service.InstallAsync(release, new Progress<OperationProgress<TemplateInstallationStage>>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("Invalid version.txt format", failed.Reason);
    }

    [Fact]
    public async Task InstallAsync_FailsWhenVersionTxtIsMissing()
    {
        var release = CreateRelease("4.4-stable-standard");
        var service = CreateService(release, CreateArchiveWithEntries(("templates/notversion.txt", "4.4.stable")), out _);

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
        var service = CreateService(release, CreateArchiveWithEntries(("../version.txt", "4.4.stable")), out _);

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

    [Fact]
    public async Task InstallAsync_StreamsArchiveToDisk_WhenArchiveStreamIsSmall()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable", payloadBytes: 2 * 1024 * 1024);
        var service = CreateService(
            release,
            archive,
            out _,
            streamFactory: bytes => new TestDownloadStream(bytes));
        var progress = new RecordingProgress<TemplateInstallationStage>();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineBytes = GC.GetTotalMemory(true);
        var peakBytes = baselineBytes;
        progress.Reported += report =>
        {
            if (report.Stage == TemplateInstallationStage.Downloading)
            {
                peakBytes = Math.Max(peakBytes, GC.GetTotalMemory(false));
            }
        };

        var result = await service.InstallAsync(release, progress);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        Assert.InRange(peakBytes - baselineBytes, 0, 64L * 1024 * 1024);
    }

    [Fact]
    public async Task InstallAsync_FailsWhenDownloadEndsBeforeAdvertisedContentLength()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable", payloadBytes: 1024);
        var before = Directory.GetDirectories(Path.GetTempPath(), "fgvm-template-*").ToHashSet(StringComparer.Ordinal);
        var service = CreateService(
            release,
            archive,
            out var templatesRoot,
            checksumUnavailable: true,
            contentLength: archive.Length + 1,
            streamFactory: bytes => new TestDownloadStream(bytes));

        var result = await service.InstallAsync(release, new RecordingProgress<TemplateInstallationStage>());

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("Download ended after", failed.Reason);
        Assert.False(Directory.Exists(Path.Combine(templatesRoot, "4.4.stable")));
        var after = Directory.GetDirectories(Path.GetTempPath(), "fgvm-template-*").ToHashSet(StringComparer.Ordinal);
        Assert.Subset(before, after);
        Assert.Subset(after, before);
    }

    [Fact]
    public async Task InstallAsync_ThrottlesProgress_WhenDownloadStreamReturnsTinyChunks()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable", payloadBytes: 3 * 1024 * 1024);
        var service = CreateService(
            release,
            archive,
            out _,
            streamFactory: bytes => new TestDownloadStream(bytes, maxChunkSize: 4 * 1024));
        var progress = new RecordingProgress<TemplateInstallationStage>();

        var result = await service.InstallAsync(release, progress);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        var downloadReports = progress.Reports.Count(report => report.Stage == TemplateInstallationStage.Downloading);
        Assert.InRange(downloadReports, 1, 8);
    }

    [Fact]
    public async Task InstallAsync_CleansTemporaryArchiveDirectory_WhenDownloadFails()
    {
        var release = CreateRelease("4.4-stable-standard");
        var archive = CreateTemplateArchive("4.4.stable", payloadBytes: 2 * 1024 * 1024);
        var before = Directory.GetDirectories(Path.GetTempPath(), "fgvm-template-*").ToHashSet(StringComparer.Ordinal);
        var service = CreateService(
            release,
            archive,
            out var templatesRoot,
            streamFactory: bytes => new TestDownloadStream(bytes, failAfterBytes: 1024));

        var result = await service.InstallAsync(release, new RecordingProgress<TemplateInstallationStage>());

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
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
        long? contentLength = null,
        Func<byte[], Stream>? streamFactory = null,
        bool checksumUnavailable = false
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
        releaseManager.Setup(x => x.GetZipFile(artifact.FileName, release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Result<ZipDownload, NetworkError>.Success(
                new ZipDownload(
                    streamFactory?.Invoke(archive) ?? new MemoryStream(archive),
                    contentLength ?? archive.Length)));

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

        return CreateArchiveWithEntries(entries.ToArray(), payloadBytes);
    }

    private static byte[] CreateTemplateArchiveWithExecutableMode(string templateVersion)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            AddEntry(archive, "templates/version.txt", templateVersion);
            var executable = AddEntry(archive, "templates/linux_release.x86_64", "template");
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

    private static byte[] CreateArchiveWithEntries(params (string Name, string Content)[] entries) =>
        CreateArchiveWithEntries(entries, 0);

    private static byte[] CreateArchiveWithEntries((string Name, string Content)[] entries, int payloadBytes)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                AddEntry(archive, name, content);
            }

            if (payloadBytes > 0)
            {
                AddPayloadEntry(archive, payloadBytes);
            }
        }

        return stream.ToArray();
    }

    private static ZipArchiveEntry AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
        return entry;
    }

    private static void AddPayloadEntry(ZipArchive archive, int payloadBytes)
    {
        var entry = archive.CreateEntry("templates/payload.bin", CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var buffer = new byte[8192];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        var remaining = payloadBytes;
        while (remaining > 0)
        {
            var write = Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, write);
            remaining -= write;
        }
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

    private sealed class TestDownloadStream(byte[] bytes, int maxChunkSize = int.MaxValue, int? failAfterBytes = null) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (failAfterBytes is { } failAfter && _position >= failAfter)
            {
                throw new IOException("Simulated download failure.");
            }

            if (_position >= bytes.Length)
            {
                return 0;
            }

            var allowedByFailure = failAfterBytes is { } failurePoint
                ? Math.Max(0, failurePoint - _position)
                : int.MaxValue;
            var read = Math.Min(Math.Min(Math.Min(buffer.Length, maxChunkSize), allowedByFailure), bytes.Length - _position);
            bytes.AsSpan(_position, read).CopyTo(buffer[..read]);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
