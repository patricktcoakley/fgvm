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
        string? sha512 = null
    )
    {
        templatesRoot = Path.Combine(_rootPath, "export_templates");
        var templatesRootPath = templatesRoot;
        var artifact = new ReleaseArtifact(ReleaseCatalog.GetExportTemplateFileName(release), sha512 ?? Sha512(archive));

        var catalog = new Mock<IReleaseCatalog>();
        catalog.Setup(x => x.FindOrHydrateExportTemplateArtifact(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<ReleaseArtifact, NetworkError>.Success(artifact));

        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.GetZipFile(artifact.FileName, release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Result<ZipDownload, NetworkError>.Success(
                new ZipDownload(new MemoryStream(archive), archive.Length)));

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

    private static byte[] CreateTemplateArchive(string templateVersion, bool includeZipSlip = false)
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

        return CreateArchiveWithEntries(entries.ToArray());
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

    private static byte[] CreateArchiveWithEntries(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                AddEntry(archive, name, content);
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

    private static string Sha512(byte[] bytes)
    {
        using var sha512 = SHA512.Create();
        return Convert.ToHexStringLower(sha512.ComputeHash(bytes));
    }
}
