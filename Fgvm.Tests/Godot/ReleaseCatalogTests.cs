using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace Fgvm.Tests.Godot;

public sealed class ReleaseCatalogTests : IDisposable
{
    private readonly Mock<IDownloadClient> _downloadClient = new();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "fgvm-release-catalog-tests", Guid.NewGuid().ToString("N"));
    private readonly string _releasesPath;
    private readonly ReleaseCatalog _catalog;

    public ReleaseCatalogTests()
    {
        _releasesPath = Path.Combine(_rootPath, "releases.json");

        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(_rootPath);
        pathService.SetupGet(x => x.ReleasesPath).Returns(_releasesPath);

        _catalog = new ReleaseCatalog(_downloadClient.Object, pathService.Object, NullLogger<ReleaseCatalog>.Instance);
    }

    [Fact]
    public async Task ReadReleaseIds_UsesFreshReleasesJson()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            ["4.4"] = new ReleaseCatalogVersion
            {
                ["stable"] = []
            },
            ["4.3"] = new ReleaseCatalogVersion
            {
                ["stable"] = []
            }
        });

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable", "4.3-stable"], success.Value);
        _downloadClient.Verify(x => x.ListReleases(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadReleaseIds_MissingCache_FetchesRemoteAndWritesReleasesJson()
    {
        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable", "4.5-dev1"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable", "4.5-dev1"], success.Value);

        var manifest = await ReadManifest();
        Assert.Contains("4.4", manifest.Keys);
        Assert.Contains("4.5", manifest.Keys);
        Assert.Contains("stable", manifest["4.4"].Keys);
        Assert.Contains("dev1", manifest["4.5"].Keys);
        Assert.True(File.Exists(_releasesPath));
    }

    [Fact]
    public async Task ReadReleaseIds_DeduplicatesRuntimeSpecificRemoteIds()
    {
        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable-standard", "4.4-stable-mono"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable"], success.Value);

        var manifest = await ReadManifest();
        Assert.Single(manifest);
        Assert.Single(manifest["4.4"]);
        Assert.Contains("stable", manifest["4.4"].Keys);
    }

    [Fact]
    public async Task ReadReleaseIds_ForceRemote_IgnoresFreshCache()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            ["4.3"] = new ReleaseCatalogVersion
            {
                ["stable"] = []
            }
        });

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.ForceRemote, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable"], success.Value);
        _downloadClient.Verify(x => x.ListReleases(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadReleaseIds_InvalidCache_FetchesRemote()
    {
        Directory.CreateDirectory(_rootPath);
        await File.WriteAllTextAsync(_releasesPath, "{ nope", CancellationToken.None);

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable"], success.Value);
    }

    [Fact]
    public async Task ReadReleaseIds_EmptyCache_FetchesRemote()
    {
        await WriteManifest([]);

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable"], success.Value);
    }

    [Fact]
    public async Task ReadReleaseIds_RemoteFailureWithoutCache_ReturnsFailure()
    {
        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Failure(new NetworkError.ConnectionFailure("offline")));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var failure = Assert.IsType<Result<string[], NetworkError>.Failure>(result);
        var error = Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
        Assert.Equal("offline", error.Message);
    }

    [Fact]
    public async Task ReadReleaseIds_RefreshPreservesExistingArtifactsForMatchingRelease()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            ["4.4"] = new ReleaseCatalogVersion
            {
                ["stable"] = new ReleaseCatalogRelease
                {
                    ["macos.universal"] = new ReleaseCatalogTarget
                    {
                        ["standard"] = new()
                        {
                            FileName = "Godot_v4.4-stable_macos.universal.zip",
                            Sha512 = "abc"
                        },
                        ["mono"] = new()
                        {
                            FileName = "Godot_v4.4-stable_mono_macos.universal.zip",
                            Sha512 = "def"
                        }
                    }
                }
            },
            ["4.3"] = new ReleaseCatalogVersion
            {
                ["stable"] = []
            }
        });

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.5-stable", "4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.ForceRemote, CancellationToken.None);

        Assert.IsType<Result<string[], NetworkError>.Success>(result);

        var manifest = await ReadManifest();
        Assert.Contains("4.5", manifest.Keys);
        Assert.Contains("4.4", manifest.Keys);
        Assert.DoesNotContain("4.3", manifest.Keys);
        Assert.Contains("stable", manifest["4.5"].Keys);
        Assert.Contains("stable", manifest["4.4"].Keys);
        Assert.Equal("abc", manifest["4.4"]["stable"]["macos.universal"]["standard"].Sha512);
        Assert.Equal("def", manifest["4.4"]["stable"]["macos.universal"]["mono"].Sha512);
    }

    [Fact]
    public async Task FindArtifact_ReturnsMatchingReleaseTargetAndRuntime()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            ["4.4"] = new ReleaseCatalogVersion
            {
                ["stable"] = new ReleaseCatalogRelease
                {
                    ["macos.universal"] = new ReleaseCatalogTarget
                    {
                        ["standard"] = new()
                        {
                            FileName = "Godot_v4.4-stable_macos.universal.zip",
                            Sha512 = "abc"
                        }
                    }
                }
            }
        });

        var release = Release.TryParse("4.4-stable-standard")! with
        {
            PlatformString = "macos.universal"
        };

        var artifact = await _catalog.FindArtifact(release, CancellationToken.None);

        Assert.NotNull(artifact);
        Assert.Equal("Godot_v4.4-stable_macos.universal.zip", artifact.FileName);
        Assert.Equal("abc", artifact.Sha512);
    }

    [Fact]
    public async Task FindArtifact_ReturnsNullWhenRuntimeIsMissing()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            ["4.4"] = new ReleaseCatalogVersion
            {
                ["stable"] = new ReleaseCatalogRelease
                {
                    ["macos.universal"] = new ReleaseCatalogTarget
                    {
                        ["standard"] = new()
                        {
                            FileName = "Godot_v4.4-stable_macos.universal.zip",
                            Sha512 = "abc"
                        }
                    }
                }
            }
        });

        var release = Release.TryParse("4.4-stable-mono")! with
        {
            PlatformString = "mono_macos.universal"
        };

        var artifact = await _catalog.FindArtifact(release, CancellationToken.None);

        Assert.Null(artifact);
    }

    [Fact]
    public async Task RecordArtifact_NormalizesMonoTargetAndWritesRuntimeArtifact()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            ["4.4"] = new ReleaseCatalogVersion
            {
                ["stable"] = []
            }
        });

        var release = Release.TryParse("4.4-stable-mono")! with
        {
            PlatformString = "mono_macos.universal"
        };

        await _catalog.RecordArtifact(release, "Godot_v4.4-stable_mono_macos.universal.zip", "def", CancellationToken.None);

        var manifest = await ReadManifest();
        var artifact = manifest["4.4"]["stable"]["macos.universal"]["mono"];
        Assert.Equal("Godot_v4.4-stable_mono_macos.universal.zip", artifact.FileName);
        Assert.Equal("def", artifact.Sha512);
    }

    [Fact]
    public async Task RecordArtifact_NormalizesWindowsExecutableTarget()
    {
        var release = Release.TryParse("4.4-stable-standard")! with
        {
            PlatformString = "win64.exe"
        };

        await _catalog.RecordArtifact(release, "Godot_v4.4-stable_win64.exe.zip", "abc", CancellationToken.None);

        var manifest = await ReadManifest();
        var artifact = manifest["4.4"]["stable"]["win64"]["standard"];
        Assert.Equal("Godot_v4.4-stable_win64.exe.zip", artifact.FileName);
        Assert.Equal("abc", artifact.Sha512);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private async Task WriteManifest(ReleaseCatalogManifest manifest)
    {
        Directory.CreateDirectory(_rootPath);
        await using var stream = File.Create(_releasesPath);
        await JsonSerializer.SerializeAsync(stream, manifest, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest, CancellationToken.None);
    }

    private async Task<ReleaseCatalogManifest> ReadManifest()
    {
        await using var stream = File.OpenRead(_releasesPath);
        return await JsonSerializer.DeserializeAsync(stream, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest, CancellationToken.None)
               ?? [];
    }
}
