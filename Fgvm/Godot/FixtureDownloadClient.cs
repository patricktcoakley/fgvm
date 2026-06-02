using System.Text.Json;
using System.Text.Json.Serialization;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Godot;

/// <summary>
///     Test-only download client backed by a generated local fixture manifest.
/// </summary>
public sealed class FixtureDownloadClient : IDownloadClient
{
    private readonly ILogger<FixtureDownloadClient> _logger;
    private readonly Lazy<Result<FixtureManifest, NetworkError>> _manifest;
    private readonly string _manifestPath;

    public FixtureDownloadClient(string manifestPath, ILogger<FixtureDownloadClient> logger)
    {
        _manifestPath = manifestPath;
        _logger = logger;
        _manifest = new Lazy<Result<FixtureManifest, NetworkError>>(LoadManifest);
    }

    public Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken)
        => WithManifest(manifest =>
            new Result<IEnumerable<string>, NetworkError>.Success(manifest.Releases.Select(release => release.Name)), cancellationToken);

    public Task<Result<GodotReleaseManifest, NetworkError>> GetReleaseManifest(Release godotRelease, CancellationToken cancellationToken)
        => WithManifest(manifest => CreateReleaseManifest(manifest, godotRelease), cancellationToken);

    public Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken)
        => WithManifest(manifest => CreateSha512Sums(manifest, godotRelease), cancellationToken);

    public Task<Result<ZipDownload, NetworkError>> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken)
        => WithManifest(manifest => OpenZip(manifest, filename, godotRelease), cancellationToken);

    private Task<Result<T, NetworkError>> WithManifest<T>(Func<FixtureManifest, Result<T, NetworkError>> getResult,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadManifest() switch
        {
            Result<FixtureManifest, NetworkError>.Success(var manifest) => getResult(manifest),
            Result<FixtureManifest, NetworkError>.Failure(var error) =>
                new Result<T, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        });
    }

    private Result<FixtureManifest, NetworkError> ReadManifest() => _manifest.Value;

    private Result<FixtureManifest, NetworkError> LoadManifest()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_manifestPath))
            {
                return new Result<FixtureManifest, NetworkError>.Failure(
                    new NetworkError.ConnectionFailure("FGVM_INTEGRATION_FIXTURE_MANIFEST is not set."));
            }

            var fullPath = Path.GetFullPath(_manifestPath);
            if (!File.Exists(fullPath))
            {
                return new Result<FixtureManifest, NetworkError>.Failure(
                    new NetworkError.ConnectionFailure($"Fixture manifest was not found: {fullPath}"));
            }

            using var stream = File.OpenRead(fullPath);
            var manifest = JsonSerializer.Deserialize(stream, FixtureJsonContext.Default.FixtureManifest);
            if (manifest is null)
            {
                return new Result<FixtureManifest, NetworkError>.Failure(
                    new NetworkError.ConnectionFailure($"Fixture manifest was empty or invalid: {fullPath}"));
            }

            var basePath = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            foreach (var artifact in manifest.Artifacts)
            {
                artifact.ZipPath = Path.GetFullPath(artifact.ZipPath, basePath);
            }

            _logger.LogInformation("Loaded fixture manifest {ManifestPath}", fullPath);
            return new Result<FixtureManifest, NetworkError>.Success(manifest);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            return new Result<FixtureManifest, NetworkError>.Failure(
                new NetworkError.ConnectionFailure($"Failed to load fixture manifest: {ex.Message}"));
        }
    }

    private static Result<GodotReleaseManifest, NetworkError> CreateReleaseManifest(FixtureManifest manifest, Release release)
    {
        var fixtureRelease =
            manifest.Releases.FirstOrDefault(x => string.Equals(x.Name, release.ReleaseName, StringComparison.OrdinalIgnoreCase));
        if (fixtureRelease is null)
        {
            return new Result<GodotReleaseManifest, NetworkError>.Failure(
                new NetworkError.RequestFailure($"fixture://manifest/godot-{release.ReleaseName}.json", 404,
                    "Release not found in fixture manifest."));
        }

        var files = manifest.Artifacts
            .Where(artifact => string.Equals(artifact.ReleaseName, release.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .Select(artifact => new GodotReleaseManifestFile
            {
                FileName = artifact.FileName,
                Checksum = artifact.Sha512
            })
            .ToList();

        return new Result<GodotReleaseManifest, NetworkError>.Success(new GodotReleaseManifest
        {
            Name = fixtureRelease.Name,
            Version = fixtureRelease.Version,
            Status = fixtureRelease.Status,
            ReleaseDate = fixtureRelease.ReleaseDate,
            GitReference = fixtureRelease.GitReference,
            Files = files
        });
    }

    private static Result<string, NetworkError> CreateSha512Sums(FixtureManifest manifest, Release release)
    {
        var artifacts = manifest.Artifacts
            .Where(artifact => string.Equals(artifact.ReleaseName, release.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (artifacts.Length == 0)
        {
            return new Result<string, NetworkError>.Failure(
                new NetworkError.RequestFailure($"fixture://checksums/{release.ReleaseName}/SHA512-SUMS.txt", 404,
                    "Release checksums not found in fixture manifest."));
        }

        var content = string.Join(System.Environment.NewLine, artifacts.Select(artifact => $"{artifact.Sha512}  {artifact.FileName}"));
        return new Result<string, NetworkError>.Success(content + System.Environment.NewLine);
    }

    private static Result<ZipDownload, NetworkError> OpenZip(FixtureManifest manifest, string filename, Release release)
    {
        var artifact = manifest.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.ReleaseName, release.ReleaseName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(artifact.FileName, filename, StringComparison.Ordinal));

        if (artifact is null)
        {
            return new Result<ZipDownload, NetworkError>.Failure(
                new NetworkError.RequestFailure($"fixture://zips/{release.ReleaseName}/{filename}", 404,
                    "Artifact not found in fixture manifest."));
        }

        try
        {
            var stream = File.OpenRead(artifact.ZipPath);
            return new Result<ZipDownload, NetworkError>.Success(new ZipDownload(stream, stream.Length, stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new Result<ZipDownload, NetworkError>.Failure(
                new NetworkError.ConnectionFailure($"Failed to open fixture zip {artifact.ZipPath}: {ex.Message}"));
        }
    }
}

public sealed class FixtureManifest
{
    [JsonPropertyName("mockVersion")]
    public required string MockVersion { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("releases")]
    public List<FixtureRelease> Releases { get; init; } = [];

    [JsonPropertyName("artifacts")]
    public List<FixtureArtifact> Artifacts { get; init; } = [];
}

public sealed class FixtureRelease
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("releaseDate")]
    public long? ReleaseDate { get; init; }

    [JsonPropertyName("gitReference")]
    public string? GitReference { get; init; }
}

public sealed class FixtureArtifact
{
    [JsonPropertyName("releaseName")]
    public required string ReleaseName { get; init; }

    [JsonPropertyName("runtime")]
    public required string Runtime { get; init; }

    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("zipPath")]
    public required string ZipPath { get; set; }

    [JsonPropertyName("sha512")]
    public required string Sha512 { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FixtureManifest))]
internal partial class FixtureJsonContext : JsonSerializerContext;
