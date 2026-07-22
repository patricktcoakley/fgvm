using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fgvm.Godot.Download;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Godot;

public interface IDownloadClient
{
    /// <summary>
    ///     Lists available remote Godot release identifiers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Remote release identifiers, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken);

    /// <summary>
    ///     Gets the manifest for a specific Godot release.
    /// </summary>
    /// <param name="godotRelease">The release whose manifest should be fetched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The release manifest, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<GodotReleaseManifest, NetworkError>> GetReleaseManifest(Release godotRelease, CancellationToken cancellationToken);

    /// <summary>
    ///     Gets SHA512 checksum metadata for a Godot release.
    /// </summary>
    /// <param name="godotRelease">The release whose checksum metadata should be fetched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Checksum metadata, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken);

    /// <summary>
    ///     Downloads a release zip file directly to <paramref name="destinationPath" />, using parallel ranged
    ///     requests when the source supports them.
    /// </summary>
    /// <param name="filename">The zip filename to fetch.</param>
    /// <param name="godotRelease">The release that owns the zip.</param>
    /// <param name="destinationPath">The file path to write the downloaded archive to.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lowercase hex SHA-512 checksum of the downloaded file, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<string, NetworkError>> DownloadZipFileAsync(string filename,
        Release godotRelease,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken
    );
}

/// <summary>
///     Downloads Godot release metadata and artifacts from official Godot sources.
/// </summary>
public sealed class DownloadClient(HttpClient httpClient, ILogger<DownloadClient> logger) : IDownloadClient
{
    private readonly DownloadSource _gitHubBuildsManifest = new("https://raw.githubusercontent.com/godotengine/godot-builds/main/releases");
    private readonly DownloadSource _gitHubBuildsRelease = new("https://github.com/godotengine/godot-builds/releases/download");

    private readonly DownloadSource _gitHubBuildsReleaseIndex =
        new("https://api.github.com/repos/godotengine/godot-builds/contents/releases");

    private readonly DownloadSource _gitHubRelease = new("https://github.com/godotengine/godot/releases/download");
    private readonly DownloadSource _godotDownloadApi = new("https://downloads.godotengine.org/");

    /// <inheritdoc />
    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken)
    {
        var result = await GetStringFromSources(
            GetReleaseIndexSources(),
            "Failed to list releases",
            cancellationToken);

        switch (result)
        {
            case Result<string, NetworkError>.Failure(var error):
                return new Result<IEnumerable<string>, NetworkError>.Failure(error);

            case Result<string, NetworkError>.Success(var jsonString):
                try
                {
                    var releases = JsonSerializer.Deserialize(jsonString, DownloadJsonContext.Default.ListReleaseIndexFile) ?? [];
                    releases.Reverse();
                    return new Result<IEnumerable<string>, NetworkError>.Success(
                        releases.Select<ReleaseIndexFile, string>(release => release.ReleaseName));
                }
                catch (Exception ex)
                {
                    logger.LogError("Failed to list releases: {Message}", ex.Message);
                    return new Result<IEnumerable<string>, NetworkError>.Failure(
                        new NetworkError.ConnectionFailure(ex.Message));
                }

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public async Task<Result<GodotReleaseManifest, NetworkError>> GetReleaseManifest(Release godotRelease,
        CancellationToken cancellationToken
    )
    {
        var result = await GetStringFromSources(
            GetManifestSources(godotRelease),
            $"Failed to get release manifest for {godotRelease.ReleaseName}",
            cancellationToken);

        switch (result)
        {
            case Result<string, NetworkError>.Failure(var error):
                return new Result<GodotReleaseManifest, NetworkError>.Failure(error);

            case Result<string, NetworkError>.Success(var jsonString):
                try
                {
                    var manifest = JsonSerializer.Deserialize(jsonString, DownloadJsonContext.Default.GodotReleaseManifest);
                    return manifest is not null
                        ? new Result<GodotReleaseManifest, NetworkError>.Success(manifest)
                        : new Result<GodotReleaseManifest, NetworkError>.Failure(
                            new NetworkError.ConnectionFailure($"Release manifest {godotRelease.ReleaseName} was empty or invalid"));
                }
                catch (Exception ex)
                {
                    logger.LogError("Failed to get release manifest for {ReleaseName}: {Message}", godotRelease.ReleaseName, ex.Message);
                    return new Result<GodotReleaseManifest, NetworkError>.Failure(
                        new NetworkError.ConnectionFailure(ex.Message));
                }

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken)
        => await GetStringFromSources(
            GetChecksumSources(godotRelease),
            $"Failed to get SHA512 for {godotRelease.ReleaseName}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<string, NetworkError>> DownloadZipFileAsync(string filename,
        Release godotRelease,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken
    )
        => await DownloadZipToFileFromSources(
            GetZipSources(filename, godotRelease),
            destinationPath,
            progress,
            $"Failed to get zip file for {godotRelease.ReleaseNameWithRuntime}",
            cancellationToken);

    private async Task<Result<string, NetworkError>> GetStringFromSources(IReadOnlyList<DownloadSource> sources,
        string failureMessage,
        CancellationToken cancellationToken
    )
    {
        NetworkError? lastError = null;

        foreach (var source in sources)
        {
            try
            {
                using var request = source.CreateRequest();
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return new Result<string, NetworkError>.Success(await response.Content.ReadAsStringAsync(cancellationToken));
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogDebug("HTTP GET {Url} returned {StatusCode}. Body: {Body}", source.Url, response.StatusCode, body);
                lastError = new NetworkError.RequestFailure(source.Url, (int)response.StatusCode, body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "HTTP GET {Url} failed", source.Url);
                lastError = new NetworkError.ConnectionFailure(ex.Message);
            }
        }

        return new Result<string, NetworkError>.Failure(
            lastError ?? new NetworkError.ConnectionFailure(failureMessage));
    }

    private async Task<Result<string, NetworkError>> DownloadZipToFileFromSources(IReadOnlyList<DownloadSource> sources,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        string failureMessage,
        CancellationToken cancellationToken
    )
    {
        NetworkError? lastError = null;

        foreach (var source in sources)
        {
            try
            {
                logger.LogInformation("Downloading from {Url}", source.Url);
                switch (await ParallelRangedDownloader.DownloadAsync(
                            httpClient, source.Url, range => source.CreateRequest(range), destinationPath, progress, cancellationToken))
                {
                    case Result<Unit, NetworkError>.Success:
                        await using (var fileStream = File.OpenRead(destinationPath))
                        {
                            var hash = await SHA512.HashDataAsync(fileStream, cancellationToken);
                            return new Result<string, NetworkError>.Success(Convert.ToHexStringLower(hash));
                        }
                    case Result<Unit, NetworkError>.Failure(var downloadError):
                        logger.LogDebug("Ranged download from {Url} failed: {Error}", source.Url, downloadError);
                        lastError = downloadError;
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ranged download from {Url} failed", source.Url);
                lastError = new NetworkError.ConnectionFailure(ex.Message);
            }
        }

        return new Result<string, NetworkError>.Failure(
            lastError ?? new NetworkError.ConnectionFailure(failureMessage));
    }

    private DownloadSource[] GetReleaseIndexSources() =>
    [
        _gitHubBuildsReleaseIndex
    ];

    private DownloadSource[] GetManifestSources(Release godotRelease) =>
    [
        _gitHubBuildsManifest.WithPath($"godot-{godotRelease.ReleaseName}.json")
    ];

    private DownloadSource[] GetChecksumSources(Release godotRelease) =>
    [
        // Some older releases expose SHA512-SUMS.txt from godotengine/godot instead of godotengine/godot-builds.
        _gitHubBuildsRelease.WithPath($"{godotRelease.ReleaseName}/SHA512-SUMS.txt"),
        _gitHubRelease.WithPath($"{godotRelease.ReleaseName}/SHA512-SUMS.txt")
    ];

    private DownloadSource[] GetZipSources(string filename, Release godotRelease) =>
    [
        GodotDownloadApiArchive(filename, godotRelease),
        _gitHubBuildsRelease.WithPath($"{godotRelease.ReleaseName}/{filename}")
    ];

    private DownloadSource GodotDownloadApiArchive(string filename, Release godotRelease)
    {
        var slug = GetDownloadSlug(filename, godotRelease);
        var platform = filename.EndsWith(".tpz", StringComparison.OrdinalIgnoreCase)
            ? RemoveArchiveExtension(slug)
            : godotRelease.PlatformString ?? RemoveArchiveExtension(slug);
        var query = $"version={Escape(godotRelease.Version)}" +
                    $"&flavor={Escape(godotRelease.Type?.ToString() ?? string.Empty)}" +
                    $"&slug={Escape(slug)}" +
                    $"&platform={Escape(platform)}";

        return _godotDownloadApi.WithQuery(query);
    }

    private static string GetDownloadSlug(string filename, Release godotRelease)
    {
        var prefix = $"Godot_v{godotRelease.ReleaseName}_";
        return filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? filename[prefix.Length..]
            : filename;
    }

    private static string RemoveArchiveExtension(string slug) =>
        slug.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? slug[..^4]
            : slug.EndsWith(".tpz", StringComparison.OrdinalIgnoreCase)
                ? slug[..^4]
                : slug;

    private static string Escape(string value) =>
        Uri.EscapeDataString(value);

    private sealed record DownloadSource(string Url)
    {
        public DownloadSource WithPath(string relativePath) => new(Url: $"{Url.TrimEnd('/')}/{relativePath.TrimStart('/')}");

        public DownloadSource WithQuery(string query) => new(Url: $"{Url}?{query}");

        public HttpRequestMessage CreateRequest() => CreateRequest(range: null);

        public HttpRequestMessage CreateRequest(RangeHeaderValue? range)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Url);
            if (range is not null)
            {
                request.Headers.Range = range;
            }

            return request;
        }
    }
}

/// <summary>
///     Entry from the godot-builds release index.
/// </summary>
public sealed class ReleaseIndexFile
{
    private const string Prefix = "godot-";
    private const string Suffix = ".json";

    public required string Name { get; set; }

    public string ReleaseName => Name[Prefix.Length..^Suffix.Length];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ReleaseIndexFile))]
[JsonSerializable(typeof(List<ReleaseIndexFile>))]
[JsonSerializable(typeof(GodotReleaseManifest))]
internal partial class DownloadJsonContext : JsonSerializerContext;
