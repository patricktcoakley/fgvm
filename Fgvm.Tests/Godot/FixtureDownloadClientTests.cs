using System.IO.Compression;
using System.Security.Cryptography;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fgvm.Tests.Godot;

public sealed class FixtureDownloadClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-fixture-client-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task FixtureClient_ReturnsReleaseMetadataAndZip()
    {
        var zipPath = await CreateZip("Godot_v4.6.2-stable_linux.x86_64.zip");
        var checksum = await CalculateSha512(zipPath);
        var manifestPath = await WriteManifest(zipPath, checksum);
        var client = new FixtureDownloadClient(manifestPath, NullLogger<FixtureDownloadClient>.Instance);
        var release = Release.TryParse("4.6.2-stable")!;

        var releases = await client.ListReleases(CancellationToken.None);
        var releaseSuccess = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(releases);
        Assert.Contains("4.6.2-stable", releaseSuccess.Value);

        var manifest = await client.GetReleaseManifest(release, CancellationToken.None);
        var manifestSuccess = Assert.IsType<Result<GodotReleaseManifest, NetworkError>.Success>(manifest);
        Assert.Equal("Godot_v4.6.2-stable_linux.x86_64.zip", manifestSuccess.Value.Files.Single().FileName);
        Assert.Equal(checksum, manifestSuccess.Value.Files.Single().Checksum);

        var sha512 = await client.GetSha512(release, CancellationToken.None);
        var sha512Success = Assert.IsType<Result<string, NetworkError>.Success>(sha512);
        Assert.Contains(checksum, sha512Success.Value);

        var zip = await client.GetZipFile("Godot_v4.6.2-stable_linux.x86_64.zip", release, CancellationToken.None);
        var zipSuccess = Assert.IsType<Result<ZipDownload, NetworkError>.Success>(zip);
        await using var download = zipSuccess.Value;
        Assert.True(download.ContentLength > 0);
    }

    [Fact]
    public async Task FixtureClient_MissingArtifact_ReturnsFailure()
    {
        var zipPath = await CreateZip("Godot_v4.6.2-stable_linux.x86_64.zip");
        var checksum = await CalculateSha512(zipPath);
        var manifestPath = await WriteManifest(zipPath, checksum);
        var client = new FixtureDownloadClient(manifestPath, NullLogger<FixtureDownloadClient>.Instance);
        var release = Release.TryParse("4.6.2-stable")!;

        var zip = await client.GetZipFile("missing.zip", release, CancellationToken.None);

        Assert.IsType<Result<ZipDownload, NetworkError>.Failure>(zip);
    }

    private async Task<string> CreateZip(string filename)
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, filename);
        await using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("Godot_v4.6.2-stable_linux.x86_64");
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream);
        await writer.WriteAsync("mock executable");
        return zipPath;
    }

    private async Task<string> WriteManifest(string zipPath, string checksum)
    {
        var manifestPath = Path.Combine(_root, "manifest.json");
        var zipName = Path.GetFileName(zipPath);
        await File.WriteAllTextAsync(manifestPath, $$"""
                                                     {
                                                       "mockVersion": "4.6.2.stable.standard.mock",
                                                       "platform": "linux-x64",
                                                       "releases": [
                                                         {
                                                           "name": "4.6.2-stable",
                                                           "version": "4.6.2",
                                                           "status": "stable",
                                                           "gitReference": "mock"
                                                         }
                                                       ],
                                                       "artifacts": [
                                                         {
                                                           "releaseName": "4.6.2-stable",
                                                           "runtime": "standard",
                                                           "target": "linux.x86_64",
                                                           "fileName": "{{zipName}}",
                                                           "zipPath": "{{zipName}}",
                                                           "sha512": "{{checksum}}"
                                                         }
                                                       ]
                                                     }
                                                     """);
        return manifestPath;
    }

    private static async Task<string> CalculateSha512(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}
