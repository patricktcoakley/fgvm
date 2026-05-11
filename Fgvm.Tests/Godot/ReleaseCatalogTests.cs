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
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                },
                ["4.3"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
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
        Assert.NotNull(manifest.LastUpdated);
        Assert.Contains("4.4", manifest.Releases.Keys);
        Assert.Contains("4.5", manifest.Releases.Keys);
        Assert.Contains("stable", manifest.Releases["4.4"].Keys);
        Assert.Contains("dev1", manifest.Releases["4.5"].Keys);
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
        Assert.Single(manifest.Releases);
        Assert.Single(manifest.Releases["4.4"]);
        Assert.Contains("stable", manifest.Releases["4.4"].Keys);
    }

    [Fact]
    public async Task ReadReleaseIds_ForceRemote_IgnoresFreshCache()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.3"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
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

        var manifest = await ReadManifest();
        Assert.NotNull(manifest.LastUpdated);
        Assert.Contains("4.4", manifest.Releases.Keys);
    }

    [Fact]
    public async Task ReadReleaseIds_StaleLastUpdated_FetchesRemote()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow.AddDays(-2),
            Releases =
            {
                ["4.3"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
            }
        });

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var success = Assert.IsType<Result<string[], NetworkError>.Success>(result);
        Assert.Equal(["4.4-stable"], success.Value);

        var manifest = await ReadManifest();
        Assert.DoesNotContain("4.3", manifest.Releases.Keys);
        Assert.Contains("4.4", manifest.Releases.Keys);
    }

    [Fact]
    public async Task ReadReleaseIds_EmptyCache_FetchesRemote()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow
        });

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
    public async Task ReadReleaseIds_WriteFailure_ReturnsFailure()
    {
        Directory.CreateDirectory(_releasesPath);

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.UseCache, CancellationToken.None);

        var failure = Assert.IsType<Result<string[], NetworkError>.Failure>(result);
        var error = Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
        Assert.Contains("Failed to write release catalog", error.Message);
    }

    [Fact]
    public async Task ReadReleaseIds_RefreshPreservesExistingArtifactsForMatchingRelease()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease
                    {
                        Targets = new ReleaseCatalogTargets
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
                    }
                },
                ["4.3"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
            }
        });

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.5-stable", "4.4-stable"]));

        var result = await _catalog.ReadReleaseIds(ReleaseFetchMode.ForceRemote, CancellationToken.None);

        Assert.IsType<Result<string[], NetworkError>.Success>(result);

        var manifest = await ReadManifest();
        Assert.Contains("4.5", manifest.Releases.Keys);
        Assert.Contains("4.4", manifest.Releases.Keys);
        Assert.DoesNotContain("4.3", manifest.Releases.Keys);
        Assert.Contains("stable", manifest.Releases["4.5"].Keys);
        Assert.Contains("stable", manifest.Releases["4.4"].Keys);
        Assert.Equal("abc", manifest.Releases["4.4"]["stable"].Targets["macos.universal"]["standard"].Sha512);
        Assert.Equal("def", manifest.Releases["4.4"]["stable"].Targets["macos.universal"]["mono"].Sha512);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_ReturnsCachedReleaseTargetAndRuntime()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease
                    {
                        Targets = new ReleaseCatalogTargets
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
                }
            }
        });

        var release = Release.TryParse("4.4-stable-standard")! with
        {
            PlatformString = "macos.universal"
        };

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        var success = Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        var artifact = success.Value;
        Assert.Equal("Godot_v4.4-stable_macos.universal.zip", artifact.FileName);
        Assert.Equal("abc", artifact.Sha512);
        _downloadClient.Verify(x => x.GetReleaseManifest(It.IsAny<Release>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_HydratesWhenCachedRuntimeIsMissing()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease
                    {
                        Targets = new ReleaseCatalogTargets
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
                }
            }
        });

        var release = Release.TryParse("4.4-stable-mono")! with
        {
            PlatformString = "mono_macos.universal"
        };

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "4.4-stable",
                Version = "4.4",
                Status = "stable",
                Files =
                [
                    new()
                    {
                        FileName = "Godot_v4.4-stable_mono_macos.universal.zip",
                        Checksum = "def"
                    }
                ]
            }));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        var success = Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        Assert.Equal("Godot_v4.4-stable_mono_macos.universal.zip", success.Value.FileName);
        Assert.Equal("def", success.Value.Sha512);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_IngestsManifestFilesAndNormalizesMonoTarget()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
            }
        });

        var release = Release.TryParse("4.4-stable-mono")! with
        {
            PlatformString = "mono_macos.universal"
        };

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "4.4-stable",
                Version = "4.4",
                Status = "stable",
                ReleaseDate = 123,
                GitReference = "abc123",
                Files =
                [
                    new()
                    {
                        FileName = "Godot_v4.4-stable_macos.universal.zip",
                        Checksum = "standard"
                    },
                    new()
                    {
                        FileName = "Godot_v4.4-stable_mono_macos.universal.zip",
                        Checksum = "mono"
                    },
                    new()
                    {
                        FileName = "Godot_v4.4-stable_export_templates.tpz",
                        Checksum = "templates"
                    }
                ]
            }));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        var success = Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        Assert.NotNull(success.Value);
        Assert.Equal("mono", success.Value.Sha512);
        var manifest = await ReadManifest();
        var catalogRelease = manifest.Releases["4.4"]["stable"];
        var artifact = catalogRelease.Targets["macos.universal"]["mono"];
        Assert.Equal("Godot_v4.4-stable_mono_macos.universal.zip", artifact.FileName);
        Assert.Equal("mono", artifact.Sha512);
        Assert.Equal(123, catalogRelease.ReleaseDate);
        Assert.Equal("abc123", catalogRelease.GitReference);
        Assert.Equal("templates", catalogRelease.Files["Godot_v4.4-stable_export_templates.tpz"].Sha512);
        _downloadClient.Verify(x => x.GetSha512(It.IsAny<Release>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_FallsBackToSha512SumsWhenManifestFilesAreEmpty()
    {
        var hostHash = new string('a', 128);
        var otherHash = new string('b', 128);
        var release = Release.TryParse("3.2-stable-standard")! with
        {
            PlatformString = "osx.64"
        };

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["3.2-stable"]));

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "3.2-stable",
                Version = "3.2",
                Status = "stable",
                Files = []
            }));

        _downloadClient.Setup(x => x.GetSha512(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string, NetworkError>.Success(
                $"{hostHash}  Godot_v3.2-stable_osx.64.zip\n{otherHash}  Godot_v3.2-stable_x11.64.zip"));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        var success = Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        Assert.NotNull(success.Value);
        Assert.Equal("Godot_v3.2-stable_osx.64.zip", success.Value.FileName);
        Assert.Equal(hostHash, success.Value.Sha512);

        var manifest = await ReadManifest();
        Assert.Equal(otherHash, manifest.Releases["3.2"]["stable"].Targets["x11.64"]["standard"].Sha512);
    }

    [Fact]
    public void ParseSha512SumsContent_IgnoresNonSha512Lines()
    {
        var validHash = new string('a', 128);
        var artifacts = ReleaseCatalog.ParseSha512SumsContent(
            $"""
            <!DOCTYPE html>
            not-a-sha Godot_v3.2-stable_osx.64.zip
            {validHash}  Godot_v3.2-stable_x11.64.zip
            """);

        var artifact = Assert.Single(artifacts);
        Assert.Equal("Godot_v3.2-stable_x11.64.zip", artifact.FileName);
        Assert.Equal(validHash, artifact.Sha512);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_ReturnsInferredArtifactWhenEmptyManifestChecksumFallbackFails()
    {
        var release = Release.TryParse("3.2.1-stable-standard")! with
        {
            PlatformString = "osx.64"
        };

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["3.2.1-stable"]));

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "3.2.1-stable",
                Version = "3.2.1",
                Status = "stable",
                Files = []
            }));

        _downloadClient.Setup(x => x.GetSha512(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string, NetworkError>.Failure(new NetworkError.ConnectionFailure("not found")));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        var success = Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        Assert.Equal("Godot_v3.2.1-stable_osx.64.zip", success.Value.FileName);
        Assert.Null(success.Value.Sha512);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_IngestsManifestFilesAndNormalizesWindowsExecutableTarget()
    {
        var release = Release.TryParse("4.4-stable-standard")! with
        {
            PlatformString = "win64.exe"
        };

        _downloadClient.Setup(x => x.ListReleases(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.4-stable"]));

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "4.4-stable",
                Version = "4.4",
                Status = "stable",
                Files =
                [
                    new()
                    {
                        FileName = "Godot_v4.4-stable_win64.exe.zip",
                        Checksum = "abc"
                    }
                ]
            }));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        var manifest = await ReadManifest();
        var artifact = manifest.Releases["4.4"]["stable"].Targets["win64"]["standard"];
        Assert.Equal("Godot_v4.4-stable_win64.exe.zip", artifact.FileName);
        Assert.Equal("abc", artifact.Sha512);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_PopulatedManifestMissingTarget_ReturnsFailure()
    {
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = DateTimeOffset.UtcNow,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
            }
        });

        var release = Release.TryParse("4.4-stable-standard")! with
        {
            PlatformString = "linux.x86_64"
        };

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "4.4-stable",
                Version = "4.4",
                Status = "stable",
                Files =
                [
                    new()
                    {
                        FileName = "Godot_v4.4-stable_macos.universal.zip",
                        Checksum = "abc"
                    }
                ]
            }));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        var failure = Assert.IsType<Result<ReleaseArtifact, NetworkError>.Failure>(result);
        var error = Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
        Assert.Contains("No artifact found", error.Message);
    }

    [Fact]
    public async Task FindOrHydrateArtifact_DoesNotUpdateLastUpdatedWhenHydratingArtifactMetadata()
    {
        var lastUpdated = DateTimeOffset.UtcNow.AddHours(-3);
        await WriteManifest(new ReleaseCatalogManifest
        {
            LastUpdated = lastUpdated,
            Releases =
            {
                ["4.4"] = new ReleaseCatalogVersion
                {
                    ["stable"] = new ReleaseCatalogRelease()
                }
            }
        });

        var release = Release.TryParse("4.4-stable-standard")! with
        {
            PlatformString = "macos.universal"
        };

        _downloadClient.Setup(x => x.GetReleaseManifest(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
            {
                Name = "4.4-stable",
                Version = "4.4",
                Status = "stable",
                Files =
                [
                    new()
                    {
                        FileName = "Godot_v4.4-stable_macos.universal.zip",
                        Checksum = "abc"
                    }
                ]
            }));

        var result = await _catalog.FindOrHydrateArtifact(release, CancellationToken.None);

        Assert.IsType<Result<ReleaseArtifact, NetworkError>.Success>(result);
        var manifest = await ReadManifest();
        Assert.Equal(lastUpdated, manifest.LastUpdated);
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
        var manifest = await JsonSerializer.DeserializeAsync(stream, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest, CancellationToken.None);
        Assert.NotNull(manifest);
        return manifest;
    }
}
