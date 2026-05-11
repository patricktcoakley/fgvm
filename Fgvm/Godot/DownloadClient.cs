using Fgvm.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    ///     Gets the release archive response for a Godot release file.
    /// </summary>
    /// <param name="filename">The archive filename to fetch.</param>
    /// <param name="godotRelease">The release that owns the archive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTTP response, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken);
}

/// <summary>
///     Downloads Godot release metadata and artifacts from the godot-builds GitHub repo.
/// </summary>
public sealed class DownloadClient : IDownloadClient
{
    private const string ReleaseDownloadBaseUrl = "https://github.com/godotengine/godot-builds/releases/download";
    private const string ReleaseIndexUrl = "https://api.github.com/repos/godotengine/godot-builds/contents/releases";
    private const string RawManifestBaseUrl = "https://raw.githubusercontent.com/godotengine/godot-builds/main/releases";

    private readonly Lazy<IConfiguration> _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DownloadClient> _logger;

    public DownloadClient(HttpClient httpClient, Lazy<IConfiguration> configuration, ILogger<DownloadClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;

        ConfigureHttpClient();
    }

    private string? GitHubToken => _configuration.Value["github:token"];

    /// <inheritdoc />
    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("HTTP GET {Url}", ReleaseIndexUrl);
            var response = await _httpClient.GetAsync(ReleaseIndexUrl, cancellationToken);
            _logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", ReleaseIndexUrl, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
                var releases = JsonSerializer.Deserialize(jsonString, DownloadJsonContext.Default.ListReleaseIndexFile) ?? [];
                releases.Reverse();
                return new Result<IEnumerable<string>, NetworkError>.Success(
                    releases.Select<ReleaseIndexFile, string>(release => release.ReleaseName));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("{Url} returned {StatusCode}. Body: {Body}", ReleaseIndexUrl, response.StatusCode, body);
            return new Result<IEnumerable<string>, NetworkError>.Failure(
                new NetworkError.RequestFailure(ReleaseIndexUrl, (int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to list releases: {Message}", ex.Message);
            return new Result<IEnumerable<string>, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<Result<GodotReleaseManifest, NetworkError>> GetReleaseManifest(Release godotRelease, CancellationToken cancellationToken)
    {
        var url = $"{RawManifestBaseUrl}/godot-{godotRelease.ReleaseName}.json";

        try
        {
            _logger.LogInformation("HTTP GET {Url}", url);
            var response = await _httpClient.GetAsync(url, cancellationToken);
            _logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
                var manifest = JsonSerializer.Deserialize(jsonString, DownloadJsonContext.Default.GodotReleaseManifest);
                return manifest is not null
                    ? new Result<GodotReleaseManifest, NetworkError>.Success(manifest)
                    : new Result<GodotReleaseManifest, NetworkError>.Failure(
                        new NetworkError.ConnectionFailure($"Release manifest {godotRelease.ReleaseName} was empty or invalid"));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("{Url} returned {StatusCode}. Body: {Body}", url, response.StatusCode, body);
            return new Result<GodotReleaseManifest, NetworkError>.Failure(
                new NetworkError.RequestFailure(url, (int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get release manifest for {ReleaseName}: {Message}", godotRelease.ReleaseName, ex.Message);
            return new Result<GodotReleaseManifest, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken)
    {
        var url = $"{ReleaseDownloadBaseUrl}/{godotRelease.ReleaseName}/SHA512-SUMS.txt";

        try
        {
            _logger.LogInformation("HTTP GET {Url}", url);
            var response = await _httpClient.GetAsync(url, cancellationToken);
            _logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return new Result<string, NetworkError>.Success(content);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("{Url} returned {StatusCode}. Body: {Body}", url, response.StatusCode, body);
            return new Result<string, NetworkError>.Failure(
                new NetworkError.RequestFailure(url, (int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get SHA512 for {Version}: {Message}", godotRelease.Version, ex.Message);
            return new Result<string, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken)
    {
        var url = $"{ReleaseDownloadBaseUrl}/{godotRelease.ReleaseName}/{filename}";

        try
        {
            _logger.LogInformation("HTTP GET {Url}", url);
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                return new Result<HttpResponseMessage, NetworkError>.Success(response);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("{Url} returned {StatusCode}. Body: {Body}", url, response.StatusCode, body);
            return new Result<HttpResponseMessage, NetworkError>.Failure(
                new NetworkError.RequestFailure(url, (int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get zip file for {ReleaseNameWithRuntime}: {Message}", godotRelease.ReleaseNameWithRuntime, ex.Message);
            return new Result<HttpResponseMessage, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    private void ConfigureHttpClient()
    {
        if (GitHubToken is { } token)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
