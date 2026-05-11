using Fgvm.Types;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace Fgvm.Godot;

public interface IDownloadClient
{
    Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken);
    Task<Result<GodotReleaseManifest, NetworkError>> GetReleaseManifest(Release godotRelease, CancellationToken cancellationToken);
    Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken);
    Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken);
}

/// <summary>
///     A download client that coordinates between GitHub and TuxFamily sources.
///     Uses GitHub as primary and TuxFamily as backup for improved reliability.
/// </summary>
public class DownloadClient(IGitHubClient gitHubClient, ITuxFamilyClient tuxFamilyClient, ILogger<DownloadClient> logger) : IDownloadClient
{
    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken) =>
        await gitHubClient.ListReleasesAsync(cancellationToken);

    public async Task<Result<GodotReleaseManifest, NetworkError>> GetReleaseManifest(Release godotRelease, CancellationToken cancellationToken) =>
        await gitHubClient.GetReleaseManifestAsync(godotRelease.ReleaseName, cancellationToken);

    public async Task<Result<string, NetworkError>> GetSha512(Release godotRelease, CancellationToken cancellationToken)
    {
        var errors = new List<NetworkError>();

        // Try GitHub first
        var gitHubResult = await gitHubClient.GetSha512Async(godotRelease, cancellationToken);
        switch (gitHubResult)
        {
            case Result<string, NetworkError>.Success gitHubSuccess:
                logger.LogInformation("Found SHA512 for {Version} at GitHub", godotRelease.Version);
                return gitHubSuccess;
            case Result<string, NetworkError>.Failure gitHubFailure:
                logger.LogError("GitHub SHA512 failed for {Version}", godotRelease.Version);
                errors.Add(gitHubFailure.Error);
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        // Fallback to TuxFamily
        var tuxFamilyResult = await tuxFamilyClient.GetSha512Async(godotRelease, cancellationToken);
        switch (tuxFamilyResult)
        {
            case Result<string, NetworkError>.Success tuxFamilySuccess:
                logger.LogInformation("Found SHA512 for {Version} at TuxFamily", godotRelease.Version);
                return tuxFamilySuccess;
            case Result<string, NetworkError>.Failure tuxFamilyFailure:
                logger.LogError("TuxFamily SHA512 failed for {Version}", godotRelease.Version);
                errors.Add(tuxFamilyFailure.Error);
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        logger.LogError("SHA512-SUMS.txt unavailable from all sources for {Version}", godotRelease.Version);
        return new Result<string, NetworkError>.Failure(
            new NetworkError.AllSourcesFailed("SHA512-SUMS.txt", errors));
    }

    public async Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release godotRelease, CancellationToken cancellationToken)
    {
        var errors = new List<NetworkError>();

        // Try GitHub first
        var gitHubResult = await gitHubClient.GetZipFileAsync(filename, godotRelease, cancellationToken);
        switch (gitHubResult)
        {
            case Result<HttpResponseMessage, NetworkError>.Success gitHubSuccess:
                logger.LogInformation("Found {ReleaseNameWithRuntime} at GitHub", godotRelease.ReleaseNameWithRuntime);
                return gitHubSuccess;
            case Result<HttpResponseMessage, NetworkError>.Failure gitHubFailure:
                logger.LogError("GitHub zip file failed for {ReleaseNameWithRuntime}", godotRelease.ReleaseNameWithRuntime);
                errors.Add(gitHubFailure.Error);
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        // Fallback to TuxFamily
        var tuxFamilyResult = await tuxFamilyClient.GetZipFileAsync(filename, godotRelease, cancellationToken);
        switch (tuxFamilyResult)
        {
            case Result<HttpResponseMessage, NetworkError>.Success tuxFamilySuccess:
                logger.LogInformation("Found {ReleaseNameWithRuntime} at TuxFamily", godotRelease.ReleaseNameWithRuntime);
                return tuxFamilySuccess;
            case Result<HttpResponseMessage, NetworkError>.Failure tuxFamilyFailure:
                logger.LogError("TuxFamily zip file failed for {ReleaseNameWithRuntime}", godotRelease.ReleaseNameWithRuntime);
                errors.Add(tuxFamilyFailure.Error);
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        logger.LogError("{Filename} was missing from all sources for {ReleaseNameWithRuntime}", filename, godotRelease.ReleaseNameWithRuntime);
        return new Result<HttpResponseMessage, NetworkError>.Failure(
            new NetworkError.AllSourcesFailed(filename, errors));
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
[JsonSerializable(typeof(GodotReleaseManifest))]
internal partial class GithubReleaseAssetContext : JsonSerializerContext;
