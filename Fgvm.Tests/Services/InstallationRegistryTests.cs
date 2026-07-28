using System.Text.Json;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RuntimeEnvironment = Fgvm.Godot.RuntimeEnvironment;

namespace Fgvm.Tests.Services;

public sealed class InstallationRegistryTests : IDisposable
{
    private const string LegacyRelease = "4.3-stable-standard";
    private const string NewLayoutRelease = "4.2-stable-standard";
    private const string Target = "linux.x86_64";

    private readonly Mock<IHostSystem> _hostSystem = new();
    private readonly Mock<IReleaseManager> _releaseManager = new();
    private readonly string _rootPath;

    public InstallationRegistryTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "fgvm-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);

        var filesystem = new HostSystem(new SystemInfo(), CreatePathService(), NullLogger<HostSystem>.Instance);
        _hostSystem.Setup(x => x.FileExists(It.IsAny<string>()))
            .Returns((string path) => filesystem.FileExists(path));
        _hostSystem.Setup(x => x.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) => filesystem.DirectoryExists(path));
        _hostSystem.Setup(x => x.CreateDirectory(It.IsAny<string>()))
            .Returns((string path) => filesystem.CreateDirectory(path));
        _hostSystem.Setup(x => x.ReadAllText(It.IsAny<string>()))
            .Returns((string path) => filesystem.ReadAllText(path));
        _hostSystem.Setup(x => x.WriteAllText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string path, string contents) => filesystem.WriteAllText(path, contents));
        _hostSystem.Setup(x => x.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string sourcePath, string destinationPath, bool overwrite) =>
                filesystem.MoveFile(sourcePath, destinationPath, overwrite));
        _hostSystem.Setup(x => x.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string sourcePath, string destinationPath) =>
                filesystem.MoveDirectory(sourcePath, destinationPath));
        _hostSystem.Setup(x => x.DeleteFileIfExists(It.IsAny<string>()))
            .Returns((string path) => filesystem.DeleteFileIfExists(path));
        _hostSystem.Setup(x => x.DeleteDirectoryIfExists(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string path, bool recursive) => filesystem.DeleteDirectoryIfExists(path, recursive));
        _hostSystem.Setup(x => x.EnumerateDirectories(It.IsAny<string>()))
            .Returns((string path) => filesystem.EnumerateDirectories(path));
        _hostSystem.Setup(x => x.GetDirectoryCreatedAtUtc(It.IsAny<string>()))
            .Returns((string path) => filesystem.GetDirectoryCreatedAtUtc(path));
        _hostSystem.Setup(x => x.ResolveLinkTarget(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string path, bool returnFinalTarget) => filesystem.ResolveLinkTarget(path, returnFinalTarget));
        _hostSystem.Setup(x => x.OpenRead(It.IsAny<string>(), It.IsAny<FileShare>()))
            .Returns((string path, FileShare fileShare) => filesystem.OpenRead(path, fileShare));

        _hostSystem.Setup(x => x.ResolveCurrentSymlinks())
            .Returns(new Result<SymlinkInfo, SymlinkError>.Failure(new SymlinkError.NoVersionSet()));

        _hostSystem.Setup(x => x.EnsureShim(It.IsAny<string>()))
            .Returns(new Result<Unit, ShimError>.Success(Unit.Value));

        _hostSystem.Setup(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()))
            .Returns(new Result<Unit, SymlinkError>.Success(Unit.Value));

        _releaseManager.Setup(x => x.CreateRelease(It.IsAny<string>()))
            .Returns((string version) => CreateRelease(version));
    }

    private string InstallationsPath => Path.Combine(_rootPath, "installations.json");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Fact]
    public void ListInstallations_WhenRegistryMissing_GeneratesFromLegacyInstallations()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, LegacyRelease));

        var result = CreateRegistry().ListInstallations();

        var installations = AssertSuccess(result);
        var installation = Assert.Single(installations);
        Assert.Equal($"{LegacyRelease}@{Target}", installation.Key);
        Assert.Equal(LegacyRelease, installation.ReleaseNameWithRuntime);
        Assert.Equal(Target, installation.Target);
        Assert.Equal(LegacyRelease, installation.RelativePath);
        Assert.True(File.Exists(InstallationsPath));
        _hostSystem.Verify(x => x.EnsureShim(It.IsAny<string>()), Times.Never);
        _hostSystem.Verify(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ListInstallations_WhenRegistryIsCorrupt_GeneratesFromFilesystem()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, LegacyRelease));
        File.WriteAllText(InstallationsPath, "{ not-json");

        var result = CreateRegistry().ListInstallations();

        var installation = Assert.Single(AssertSuccess(result));
        Assert.Equal($"{LegacyRelease}@{Target}", installation.Key);
    }

    [Fact]
    public void ListInstallations_WhenRegistryIsCorrupt_InfersDefaultFromCurrentSymlink()
    {
        var legacyPath = Path.Combine(_rootPath, LegacyRelease);
        Directory.CreateDirectory(legacyPath);
        File.WriteAllText(InstallationsPath, "{ not-json");
        _hostSystem.Setup(x => x.ResolveCurrentSymlinks())
            .Returns(new Result<SymlinkInfo, SymlinkError>.Success(new SymlinkInfo(Path.Combine(legacyPath, "Godot"))));

        var result = CreateRegistry().ListInstallations();

        Assert.Single(AssertSuccess(result));
        Assert.Equal($"{LegacyRelease}@{Target}", ReadRegistry().Default);
        _hostSystem.Verify(x => x.EnsureShim(It.IsAny<string>()), Times.Once);
        _hostSystem.Verify(x => x.CreateOrOverwriteShortcut(
                Path.Combine(legacyPath, "Godot_v4.3-stable_linux.x86_64")),
            Times.Once);
    }

    [Fact]
    public void ListInstallations_RegistersLegacyAndTargetAwareInstallations()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, LegacyRelease));
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", NewLayoutRelease, Target));

        var result = CreateRegistry().ListInstallations();

        var installations = AssertSuccess(result);
        Assert.Contains(installations, x => x.Key == $"{LegacyRelease}@{Target}" && x.RelativePath == LegacyRelease);
        Assert.Contains(installations, x => x.Key == $"{NewLayoutRelease}@{Target}" &&
                                            x.RelativePath == $"installations/{NewLayoutRelease}/{Target}");
    }

    [Fact]
    public void ListInstallations_WhenLegacyAndTargetAwareUseSameKey_PrefersTargetAwarePath()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, LegacyRelease));
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, Target));

        var result = CreateRegistry().ListInstallations();

        var installation = Assert.Single(AssertSuccess(result));
        Assert.Equal($"{LegacyRelease}@{Target}", installation.Key);
        Assert.Equal($"installations/{LegacyRelease}/{Target}", installation.RelativePath);
    }

    [Fact]
    public void ListInstallations_TargetAwareSameReleaseWithMultipleTargets_KeepsEachTarget()
    {
        const string armTarget = "linux.arm64";
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, Target));
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, armTarget));

        var result = CreateRegistry().ListInstallations();

        var installations = AssertSuccess(result);
        Assert.Equal(2, installations.Count);
        Assert.Contains(installations, installation =>
            installation.Key == $"{LegacyRelease}@{Target}" &&
            installation.ReleaseNameWithRuntime == LegacyRelease &&
            installation.Target == Target &&
            installation.RelativePath == $"installations/{LegacyRelease}/{Target}");
        Assert.Contains(installations, installation =>
            installation.Key == $"{LegacyRelease}@{armTarget}" &&
            installation.ReleaseNameWithRuntime == LegacyRelease &&
            installation.Target == armTarget &&
            installation.RelativePath == $"installations/{LegacyRelease}/{armTarget}");
    }

    [Fact]
    public void ListInstallations_IgnoresBackupLeftBehindByAnInterruptedCommit()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, Target));
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease,
            $".backup-{Guid.NewGuid():N}-{Target}"));

        var result = CreateRegistry().ListInstallations();

        var installation = Assert.Single(AssertSuccess(result));
        Assert.Equal(Target, installation.Target);
    }

    [Fact]
    public void FindByReleaseName_WhenSameReleaseHasMultipleTargets_UsesReleaseManagerPlatformTarget()
    {
        const string armTarget = "linux.arm64";
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, Target));
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, armTarget));

        var result = CreateRegistry().FindByReleaseName(LegacyRelease);

        var installation = Assert.IsType<Result<Installation, InstallationRegistryError>.Success>(result).Value;
        Assert.Equal($"{LegacyRelease}@{Target}", installation.Key);
        Assert.Equal(Target, installation.Target);
        Assert.Equal($"installations/{LegacyRelease}/{Target}", installation.RelativePath);
    }

    [Fact]
    public void ListInstallations_DropsInvalidRecordsAndClearsInvalidDefault()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, Target));
        WriteRegistry(new InstallationRegistryDocument
        {
            Default = "missing@linux.x86_64",
            Installations =
            {
                [$"{LegacyRelease}@{Target}"] = new InstallationRegistryEntry
                {
                    Path = $"installations/{LegacyRelease}/{Target}",
                    InstalledAt = DateTimeOffset.Parse("2026-05-11T20:15:00Z")
                },
                ["not-a-key"] = new InstallationRegistryEntry
                {
                    Path = $"installations/{LegacyRelease}/{Target}"
                },
                [$"{NewLayoutRelease}@{Target}"] = new InstallationRegistryEntry
                {
                    Path = "../outside"
                },
                ["4.1-stable-standard@linux.x86_64"] = new InstallationRegistryEntry
                {
                    Path = "installations/4.1-stable-standard/linux.x86_64"
                }
            }
        });

        var result = CreateRegistry().ListInstallations();

        var installation = Assert.Single(AssertSuccess(result));
        Assert.Equal($"{LegacyRelease}@{Target}", installation.Key);

        var generated = ReadRegistry();
        Assert.Null(generated.Default);
        Assert.Single(generated.Installations);
        Assert.True(generated.Installations.ContainsKey($"{LegacyRelease}@{Target}"));
    }

    [Fact]
    public void ListInstallations_WhenRegistryMissing_InfersDefaultFromCurrentSymlink()
    {
        var legacyPath = Path.Combine(_rootPath, LegacyRelease);
        Directory.CreateDirectory(legacyPath);
        _hostSystem.Setup(x => x.ResolveCurrentSymlinks())
            .Returns(new Result<SymlinkInfo, SymlinkError>.Success(new SymlinkInfo(Path.Combine(legacyPath, "Godot"))));

        var result = CreateRegistry().ListInstallations();

        Assert.Single(AssertSuccess(result));
        Assert.Equal($"{LegacyRelease}@{Target}", ReadRegistry().Default);
        _hostSystem.Verify(x => x.EnsureShim(It.IsAny<string>()), Times.Once);
        _hostSystem.Verify(x => x.CreateOrOverwriteShortcut(
                Path.Combine(legacyPath, "Godot_v4.3-stable_linux.x86_64")),
            Times.Once);
    }

    [Fact]
    public void ListInstallations_WhenRegistryMissing_InfersDefaultFromRootSymlink()
    {
        var legacyPath = Path.Combine(_rootPath, LegacyRelease);
        var newLayoutPath = Path.Combine(_rootPath, "installations", NewLayoutRelease, Target);
        Directory.CreateDirectory(legacyPath);
        Directory.CreateDirectory(newLayoutPath);

        _hostSystem.Setup(x => x.ResolveCurrentSymlinks())
            .Returns(new Result<SymlinkInfo, SymlinkError>.Success(new SymlinkInfo(Path.Combine(newLayoutPath, "Godot"))));

        var result = CreateRegistry().ListInstallations();

        Assert.Equal(2, AssertSuccess(result).Count);
        Assert.Equal($"{NewLayoutRelease}@{Target}", ReadRegistry().Default);
        _hostSystem.Verify(x => x.EnsureShim(It.IsAny<string>()), Times.Once);
        _hostSystem.Verify(x => x.CreateOrOverwriteShortcut(
                Path.Combine(newLayoutPath, "Godot_v4.2-stable_linux.x86_64")),
            Times.Once);
    }

    [Fact]
    public void ListInstallations_WhenDefaultArtifactRefreshFails_StillGeneratesRegistry()
    {
        var legacyPath = Path.Combine(_rootPath, LegacyRelease);
        Directory.CreateDirectory(legacyPath);
        _hostSystem.Setup(x => x.ResolveCurrentSymlinks())
            .Returns(new Result<SymlinkInfo, SymlinkError>.Success(new SymlinkInfo(Path.Combine(legacyPath, "Godot"))));

        _hostSystem.Setup(x => x.EnsureShim(It.IsAny<string>()))
            .Returns(new Result<Unit, ShimError>.Failure(new ShimError.PathConflict(Path.Combine(_rootPath, "bin", "godot"))));

        _hostSystem.Setup(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()))
            .Returns(new Result<Unit, SymlinkError>.Failure(new SymlinkError.PermissionDenied()));

        var result = CreateRegistry().ListInstallations();

        Assert.Single(AssertSuccess(result));
        Assert.Equal($"{LegacyRelease}@{Target}", ReadRegistry().Default);
    }

    [Fact]
    public void FindByReleaseName_WhenGeneratingMissingRecord_PreservesExistingDefault()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", NewLayoutRelease, Target));
        Directory.CreateDirectory(Path.Combine(_rootPath, "installations", LegacyRelease, Target));
        WriteRegistry(new InstallationRegistryDocument
        {
            Default = $"{NewLayoutRelease}@{Target}",
            Installations =
            {
                [$"{NewLayoutRelease}@{Target}"] = new InstallationRegistryEntry
                {
                    Path = $"installations/{NewLayoutRelease}/{Target}"
                }
            }
        });

        var result = CreateRegistry().FindByReleaseName(LegacyRelease);

        var installation = Assert.IsType<Result<Installation, InstallationRegistryError>.Success>(result).Value;
        Assert.Equal($"{LegacyRelease}@{Target}", installation.Key);
        Assert.Equal($"{NewLayoutRelease}@{Target}", ReadRegistry().Default);
    }

    [Fact]
    public void UpsertInstalled_WritesRegistryAndRemovesTemporaryFile()
    {
        var release = Assert.IsType<Result<Release, ReleaseParseError>.Success>(CreateRelease(LegacyRelease)).Value;
        var registry = CreateRegistry();

        var result = registry.UpsertInstalled(release, $"installations/{LegacyRelease}/{Target}",
            DateTimeOffset.Parse("2026-05-11T20:15:00Z"));

        Assert.IsType<Result<Unit, InstallationRegistryError>.Success>(result);
        Assert.False(File.Exists(InstallationsPath + ".tmp"));
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "installations.json.*.tmp"));
        var document = ReadRegistry();
        Assert.True(document.Installations.ContainsKey($"{LegacyRelease}@{Target}"));
    }

    [Fact]
    public void UpsertInstalled_OnMacOS_RejectsAPathEscapingToACaseVariantSibling()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var release = Assert.IsType<Result<Release, ReleaseParseError>.Success>(CreateRelease(LegacyRelease)).Value;
        var rootDirectoryName = Path.GetFileName(_rootPath);
        var relativePath = Path.Combine("..", "..", "FGVM-REGISTRY-TESTS", rootDirectoryName,
            "installations", LegacyRelease, Target);

        var result = CreateRegistry().UpsertInstalled(release, relativePath);

        var failure = Assert.IsType<Result<Unit, InstallationRegistryError>.Failure>(result);
        Assert.IsType<InstallationRegistryError.InvalidPath>(failure.Error);
    }

    // Removal renames the directory away before touching the registry, so this is the state a crash in between leaves
    [Fact]
    public void ListInstallations_WhenAListedDirectoryIsGone_DropsTheEntry()
    {
        var relativePath = $"installations/{NewLayoutRelease}/{Target}";
        WriteRegistry(new InstallationRegistryDocument
        {
            Default = $"{NewLayoutRelease}@{Target}",
            Installations =
            {
                [$"{NewLayoutRelease}@{Target}"] = new InstallationRegistryEntry { Path = relativePath }
            }
        });

        var installations = AssertSuccess(CreateRegistry().ListInstallations());

        Assert.Empty(installations);
        Assert.Null(ReadRegistry().Default);
    }

    // The reverse does not repair itself, which is why the registry is never written before the directory is moved
    [Fact]
    public void ListInstallations_WhenAPresentDirectoryIsNotListed_DoesNotResurrectIt()
    {
        var relativePath = $"installations/{NewLayoutRelease}/{Target}";
        Directory.CreateDirectory(Path.Combine(_rootPath, relativePath));
        WriteRegistry(new InstallationRegistryDocument());

        var installations = AssertSuccess(CreateRegistry().ListInstallations());

        Assert.Empty(installations);
    }

    [Fact]
    public void Remove_WhileDirectoryIsStagedForRemoval_DoesNotImportTombstoneAsInstallation()
    {
        var relativePath = $"installations/{NewLayoutRelease}/{Target}";
        var installationPath = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(installationPath);
        WriteRegistry(new InstallationRegistryDocument
        {
            Installations =
            {
                [$"{NewLayoutRelease}@{Target}"] = new InstallationRegistryEntry
                {
                    Path = relativePath
                }
            }
        });
        var registry = CreateRegistry();

        var removal = new DirectoryRemoval(_hostSystem.Object, NullLogger<DirectoryRemoval>.Instance);
        var staged = Assert.IsType<Result<IReadOnlyList<string>, FileOperationError>.Success>(
            removal.Stage([installationPath])).Value;
        var result = registry.Remove($"{NewLayoutRelease}@{Target}");

        Assert.IsType<Result<Unit, InstallationRegistryError>.Success>(result);
        Assert.Empty(AssertSuccess(registry.ListInstallations()));

        removal.Discard(staged);

        Assert.False(Directory.Exists(installationPath));
        Assert.DoesNotContain(ReadRegistry().Installations.Keys,
            key => key.Contains(".fgvm-removing-", StringComparison.Ordinal));
    }

    [Fact]
    public void ListInstallations_WhenRegistryWriteHitsExistingFileRoot_ReturnsIoContext()
    {
        var rootFile = Path.Combine(Path.GetTempPath(), "fgvm-registry-tests", Guid.NewGuid().ToString("N"));
        File.WriteAllText(rootFile, "");

        try
        {
            var result = new InstallationRegistry(
                CreatePathService(rootFile),
                _releaseManager.Object,
                _hostSystem.Object,
                NullLogger<InstallationRegistry>.Instance).ListInstallations();

            var failure = Assert.IsType<Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure>(result);
            var error = Assert.IsType<InstallationRegistryError.WriteFailed>(failure.Error);
            Assert.IsType<FileOperationError.IoFailure>(error.Error);
            Assert.Equal(rootFile, error.Error.Path);
            Assert.Contains("I/O failure", error.ToString());
        }
        finally
        {
            File.Delete(rootFile);
        }
    }

    private InstallationRegistry CreateRegistry() =>
        new(CreatePathService(), _releaseManager.Object, _hostSystem.Object, NullLogger<InstallationRegistry>.Instance);

    private IPathService CreatePathService(string? rootPath = null)
    {
        var root = rootPath ?? _rootPath;
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(root);
        pathService.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(root, "releases.json"));
        pathService.SetupGet(x => x.InstallationsPath).Returns(Path.Combine(root, "installations.json"));
        pathService.SetupGet(x => x.InstallationsDirectoryPath).Returns(Path.Combine(root, "installations"));
        pathService.SetupGet(x => x.BinPath).Returns(Path.Combine(root, "bin"));
        pathService.SetupGet(x => x.ShimPath).Returns(Path.Combine(root, "bin", "godot"));
        pathService.SetupGet(x => x.SymlinkPath).Returns(Path.Combine(root, "Godot"));
        pathService.SetupGet(x => x.MacAppSymlinkPath).Returns(Path.Combine(root, "Godot.app"));
        pathService.SetupGet(x => x.LogPath).Returns(Path.Combine(root, "fgvm.log"));
        return pathService.Object;
    }

    private void WriteRegistry(InstallationRegistryDocument document)
    {
        var json = JsonSerializer.Serialize(document);
        File.WriteAllText(InstallationsPath, json);
    }

    private InstallationRegistryDocument ReadRegistry()
    {
        var json = File.ReadAllText(InstallationsPath);
        return Assert.IsType<InstallationRegistryDocument>(
            JsonSerializer.Deserialize<InstallationRegistryDocument>(json));
    }

    private static IReadOnlyList<Installation> AssertSuccess(Result<IReadOnlyList<Installation>, InstallationRegistryError> result) =>
        Assert.IsType<Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success>(result).Value;

    private static Result<Release, ReleaseParseError> CreateRelease(string version)
    {
        var release = Release.TryParse(version);
        if (release is null)
        {
            return new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.InvalidVersion(version));
        }

        var target = release.RuntimeEnvironment == RuntimeEnvironment.Mono ? "mono_linux_x86_64" : Target;
        return new Result<Release, ReleaseParseError>.Success(release with
        {
            OS = OS.Linux,
            PlatformString = target
        });
    }
}
