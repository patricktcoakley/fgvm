using Fgvm.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Fgvm.Godot;

public interface IGitHubClient
{
    Task<Result<IEnumerable<string>, NetworkError>> ListReleasesAsync(CancellationToken cancellationToken);
    Task<Result<string, NetworkError>> GetSha512Async(Release godotRelease, CancellationToken cancellationToken);
    Task<HttpResponseMessage> GetZipFileAsync(string filename, Release godotRelease, CancellationToken cancellationToken);
}

public class GitHubClient : IGitHubClient
{
    private const string BaseUrl = "https://github.com/godotengine/godot-builds/releases/download";
    private const string ApiUrl = "https://api.github.com/repos/godotengine/godot-builds/contents/releases";
    private readonly Lazy<IConfiguration> _configuration;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubClient> _logger;

    public GitHubClient(HttpClient httpClient, Lazy<IConfiguration> configuration, ILogger<GitHubClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;

        // Configure the HttpClient for GitHub API
        ConfigureHttpClient();
    }

    private string? GitHubToken => _configuration.Value["github:token"];

    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("HTTP GET {Url}", ApiUrl);
            var response = await _httpClient.GetAsync(ApiUrl, cancellationToken);
            _logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", ApiUrl, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
                var releases = JsonSerializer.Deserialize(jsonString, GithubReleaseAssetContext.Default.ListGitHubReleaseAsset) ?? [];
                releases.Reverse();
                return new Result<IEnumerable<string>, NetworkError>.Success(
                    releases.Select<GitHubReleaseAsset, string>(r => r.ReleaseName));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("{ApiUrl} returned {StatusCode}. Body: {Body}", ApiUrl, response.StatusCode, body);
            return new Result<IEnumerable<string>, NetworkError>.Failure(
                new NetworkError.RequestFailure(ApiUrl, (int)response.StatusCode, body));
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to list releases from GitHub: {Message}", ex.Message);
            return new Result<IEnumerable<string>, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    public async Task<Result<string, NetworkError>> GetSha512Async(Release godotRelease, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/{godotRelease.ReleaseName}/SHA512-SUMS.txt";

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
        catch (Exception ex)
        {
            _logger.LogError("Failed to get SHA512 from GitHub for {Version}: {Message}", godotRelease.Version, ex.Message);
            return new Result<string, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    // TODO: Replace with Task<Result<HttpResponseMessage, NetworkError>> GetZipFileAsync(string filename, Release godotRelease, CancellationToken cancellationToken)
    public async Task<HttpResponseMessage> GetZipFileAsync(string filename, Release godotRelease, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/{godotRelease.ReleaseName}/{filename}";

        try
        {
            _logger.LogInformation("HTTP GET {Url}", url);
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("{Url} returned {StatusCode}. Body: {Body}", url, response.StatusCode, body);
            throw new HttpRequestException($"GitHub zip file request failed: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get zip file from GitHub for {ReleaseNameWithRuntime}: {Message}", godotRelease.ReleaseNameWithRuntime, ex.Message);
            throw;
        }
    }

    private void ConfigureHttpClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("fgvm", null));

        if (GitHubToken != null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken);
        }
    }
}
