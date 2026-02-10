using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace Fgvm.Godot;

public interface IDownloadClient
{
    Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken);
    Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken);
    Task<HttpResponseMessage> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken);
}

/// <summary>
///     A download client that coordinates between GitHub and TuxFamily sources.
///     Uses GitHub as primary and TuxFamily as backup for improved reliability.
/// </summary>
public class DownloadClient(IGitHubClient gitHubClient, ITuxFamilyClient tuxFamilyClient, IPathService pathService, ILogger<DownloadClient> logger) : IDownloadClient
{
    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken)
    {
        var result = await gitHubClient.ListReleasesAsync(cancellationToken);

        // Write cache on successful fetch
        if (result is Result<IEnumerable<string>, NetworkError>.Success success)
        {
            var releaseArray = success.Value.ToArray();
            try
            {
                await File.WriteAllLinesAsync(pathService.ReleasesPath, releaseArray, cancellationToken);
                logger.LogInformation("Updated releases cache at {ReleasesPath} with {Count} releases", pathService.ReleasesPath, releaseArray.Length);
            }
            catch (Exception cacheEx)
            {
                logger.LogWarning("Failed to write releases cache, continuing anyway: {Message}", cacheEx.Message);
            }
        }

        return result;
    }

    public async Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken)
    {
        var errors = new List<NetworkError>();

        // Try GitHub first
        var gitHubResult = await gitHubClient.GetSha512Async(godotRelease, cancellationToken);
        if (gitHubResult is Result<string, NetworkError>.Success gitHubSuccess)
        {
            logger.LogInformation("Found SHA512 for {Version} at GitHub", godotRelease.Version);
            return gitHubSuccess;
        }

        if (gitHubResult is Result<string, NetworkError>.Failure gitHubFailure)
        {
            logger.LogError("GitHub SHA512 failed for {Version}", godotRelease.Version);
            errors.Add(gitHubFailure.Error);
        }

        // Fallback to TuxFamily
        var tuxFamilyResult = await tuxFamilyClient.GetSha512Async(godotRelease, cancellationToken);
        if (tuxFamilyResult is Result<string, NetworkError>.Success tuxFamilySuccess)
        {
            logger.LogInformation("Found SHA512 for {Version} at TuxFamily", godotRelease.Version);
            return tuxFamilySuccess;
        }

        if (tuxFamilyResult is Result<string, NetworkError>.Failure tuxFamilyFailure)
        {
            logger.LogError("TuxFamily SHA512 failed for {Version}", godotRelease.Version);
            errors.Add(tuxFamilyFailure.Error);
        }

        logger.LogError("SHA512-SUMS.txt unavailable from all sources for {Version}", godotRelease.Version);
        return new Result<string, NetworkError>.Failure(
            new NetworkError.AllSourcesFailed("SHA512-SUMS.txt", errors));
    }

    // TODO: Replace with Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken)
    public async Task<HttpResponseMessage> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken)
    {
        // Try GitHub first
        try
        {
            var response = await gitHubClient.GetZipFileAsync(filename, godotRelease, cancellationToken);
            logger.LogInformation("Found {ReleaseNameWithRuntime} at GitHub", godotRelease.ReleaseNameWithRuntime);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError("GitHub zip file failed for {ReleaseNameWithRuntime}: {Message}", godotRelease.ReleaseNameWithRuntime, ex.Message);
        }

        // Fallback to TuxFamily
        try
        {
            var response = await tuxFamilyClient.GetZipFileAsync(filename, godotRelease, cancellationToken);
            logger.LogInformation("Found {ReleaseNameWithRuntime} at TuxFamily", godotRelease.ReleaseNameWithRuntime);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError("TuxFamily zip file failed for {ReleaseNameWithRuntime}: {Message}", godotRelease.ReleaseNameWithRuntime, ex.Message);
        }

        logger.LogError("{Filename} was missing from all sources for {ReleaseNameWithRuntime}", filename, godotRelease.ReleaseNameWithRuntime);
        throw new HttpRequestException($"Wasn't able to download {filename} from any sources.");
    }
}

/// <summary>
///     Just here to grab the release name from the list of builds, nothing more.
/// </summary>
public class GitHubReleaseAsset
{
    // trim `godot-` and `.json` to extract just the version and release type
    private const string Prefix = "godot-";
    private const string Suffix = ".json";
    public required string Name { get; set; }
    public string ReleaseName => Name[Prefix.Length..^Suffix.Length];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubReleaseAsset))]
[JsonSerializable(typeof(List<GitHubReleaseAsset>))]
internal partial class GithubReleaseAssetContext : JsonSerializerContext;
