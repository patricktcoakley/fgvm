using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Godot;

public interface ITuxFamilyClient
{
    Task<Result<string, NetworkError>> GetSha512Async(Release godotRelease, CancellationToken cancellationToken);
    Task<Result<HttpResponseMessage, NetworkError>> GetZipFileAsync(string filename, Release godotRelease, CancellationToken cancellationToken);
}

// Basically exists as a backup for checksums of older versions since GitHub doesn't seem to always have them.
// TuxFamily has had inconsistent connectivity and is seeminlgy down, so we just use the internet archive version.
public class TuxFamilyClient(HttpClient httpClient, ILogger<TuxFamilyClient> logger) : ITuxFamilyClient
{
    // Use a stable archive snapshot; the live endpoint is unreliable.
    private const string BaseUrl = "https://web.archive.org/web/20211106101031if_/https://downloads.tuxfamily.org/godotengine";

    public async Task<Result<string, NetworkError>> GetSha512Async(Release godotRelease, CancellationToken cancellationToken)
    {
        var url = BuildUrl("SHA512-SUMS.txt", godotRelease);

        try
        {
            logger.LogInformation("HTTP GET {Url}", url);
            var response = await httpClient.GetAsync(url, cancellationToken);
            logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Found SHA512 for {Version} at TuxFamily", godotRelease.Version);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return new Result<string, NetworkError>.Success(content);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("{Url} returned {StatusCode}. Body: {Body}", url, response.StatusCode, body);
            return new Result<string, NetworkError>.Failure(
                new NetworkError.RequestFailure(url, (int)response.StatusCode, body));
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to get SHA512 from TuxFamily for {Version}: {Message}", godotRelease.Version, ex.Message);
            return new Result<string, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    public async Task<Result<HttpResponseMessage, NetworkError>> GetZipFileAsync(string filename, Release godotRelease, CancellationToken cancellationToken)
    {
        var url = BuildUrl(filename, godotRelease);

        try
        {
            logger.LogInformation("HTTP GET {Url}", url);
            var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            logger.LogInformation("HTTP GET {Url} completed with status {StatusCode}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Found {File} for {Release} at TuxFamily", filename, godotRelease.ReleaseNameWithRuntime);
                return new Result<HttpResponseMessage, NetworkError>.Success(response);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("{Url} returned {StatusCode}. Body: {Body}", url, response.StatusCode, body);
            return new Result<HttpResponseMessage, NetworkError>.Failure(
                new NetworkError.RequestFailure(url, (int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to get zip file from TuxFamily for {ReleaseNameWithRuntime}: {Message}", godotRelease.ReleaseNameWithRuntime, ex.Message);
            return new Result<HttpResponseMessage, NetworkError>.Failure(
                new NetworkError.ConnectionFailure(ex.Message));
        }
    }

    private static string BuildUrl(string filename, Release godotRelease)
    {
        var basePath = godotRelease.Type is null || godotRelease.Type == "stable"
            ? $"{BaseUrl}/{godotRelease.Version}"
            : $"{BaseUrl}/{godotRelease.Version}/{godotRelease.Type}";

        return godotRelease.RuntimeEnvironment == RuntimeEnvironment.Mono
            ? $"{basePath}/mono/{filename}"
            : $"{basePath}/{filename}";
    }
}
