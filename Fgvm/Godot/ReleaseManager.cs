using Fgvm.Environment;
using Fgvm.Types;

namespace Fgvm.Godot;

public interface IReleaseManager
{
    Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken);

    Task<Result<IEnumerable<string>, NetworkError>> SearchRemoteReleases(
        string[] query,
        ReleaseFetchMode fetchMode,
        CancellationToken cancellationToken);

    Task<Result<string, NetworkError>> GetSha512(Release release, CancellationToken cancellationToken);
    Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release release, CancellationToken cancellationToken);

    Release? TryFindReleaseByQuery(string[] query, string[] releaseNames);
    Result<Release, QueryError> ResolveReleaseQuery(string[] query, string[] releaseIds);
    IEnumerable<string> FilterReleasesByQuery(string[] query, string[] releaseNames, bool chronological = false);
    string? FindCompatibleVersion(string projectVersion, bool isDotNet, IEnumerable<string> installedVersions);
    Result<string, CompatibilityError> FindCompatibleVersionResult(string projectVersion, bool isDotNet, IEnumerable<string> installedReleaseIds);

    Release? TryCreateRelease(string versionString);
}

public sealed class ReleaseManager(
    IHostSystem hostSystem,
    PlatformStringProvider platformStringProvider,
    IDownloadClient downloadClient,
    IReleaseCatalog releaseCatalog) : IReleaseManager
{
    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken)
    {
        var result = await releaseCatalog.ReadReleaseIds(ReleaseFetchMode.UseCache, cancellationToken);
        return result switch
        {
            Result<string[], NetworkError>.Success(var releases) =>
                new Result<IEnumerable<string>, NetworkError>.Success(releases),
            Result<string[], NetworkError>.Failure(var error) =>
                new Result<IEnumerable<string>, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    public async Task<Result<IEnumerable<string>, NetworkError>> SearchRemoteReleases(
        string[] query,
        ReleaseFetchMode fetchMode,
        CancellationToken cancellationToken)
    {
        var result = await releaseCatalog.ReadReleaseIds(fetchMode, cancellationToken);
        return result switch
        {
            Result<string[], NetworkError>.Success(var releases) =>
                new Result<IEnumerable<string>, NetworkError>.Success(
                    FilterReleasesByQuery(query, releases, true)),
            Result<string[], NetworkError>.Failure(var error) =>
                new Result<IEnumerable<string>, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    public async Task<Result<string, NetworkError>> GetSha512(Release release, CancellationToken cancellationToken) =>
        await downloadClient.GetSha512(release, cancellationToken);

    public async Task<Result<HttpResponseMessage, NetworkError>> GetZipFile(string filename, Release release, CancellationToken cancellationToken) =>
        await downloadClient.GetZipFile(filename, release, cancellationToken);

    public Release? TryFindReleaseByQuery(string[] query, string[] releaseNames)
    {
        var result = ResolveReleaseQuery(query, releaseNames);
        return result switch
        {
            Result<Release, QueryError>.Success(var release) => release,
            Result<Release, QueryError>.Failure(QueryError.EmptyQuery) => throw new ArgumentException("Empty query"),
            Result<Release, QueryError>.Failure(QueryError.InvalidQuery(var message)) => throw new ArgumentException(message),
            Result<Release, QueryError>.Failure(QueryError.NotFound) => null,
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    public Result<Release, QueryError> ResolveReleaseQuery(string[] query, string[] releaseIds)
    {
        try
        {
            return query.Length == 0
                ? new Result<Release, QueryError>.Failure(new QueryError.EmptyQuery())
                : FindReleaseByQuery(query, releaseIds) is { } release
                    ? new Result<Release, QueryError>.Success(release)
                    : new Result<Release, QueryError>.Failure(new QueryError.NotFound(string.Join(" ", query)));
        }
        catch (ArgumentException ex)
        {
            return new Result<Release, QueryError>.Failure(new QueryError.InvalidQuery(ex.Message));
        }
    }

    public IEnumerable<string> FilterReleasesByQuery(string[] query, string[] releaseNames, bool chronological = false)
    {
        // Extract runtime environment filter (mono/standard)
        var runtimeFilter = query.Length > 0
            ? query.FirstOrDefault(x => x.Equals("mono", StringComparison.OrdinalIgnoreCase) ||
                                        x.Equals("standard", StringComparison.OrdinalIgnoreCase), string.Empty)
            : string.Empty;

        // Extract release type filter (stable/rc/beta/alpha/dev)
        var releaseType = query.Length > 0
            ? query.FirstOrDefault(x => ReleaseType.Prefixes
                .Any(prefix => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), string.Empty)
            : string.Empty;

        // Extract version filter (anything that's not runtime or release type)
        var possibleVersion = query.Length > 0
            ? query.Where(x => !x.Equals(releaseType, StringComparison.OrdinalIgnoreCase) &&
                               !x.Equals(runtimeFilter, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(string.Empty)
            : string.Empty;

        var filtered = releaseNames
            .Where(x => string.IsNullOrEmpty(possibleVersion) || x.StartsWith(possibleVersion, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrEmpty(releaseType) || x.Contains(releaseType, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrEmpty(runtimeFilter) || x.Contains(runtimeFilter, StringComparison.OrdinalIgnoreCase))
            .Select(name => new { OriginalName = name, Release = TryCreateRelease(name) ?? TryCreateRelease($"{name}-standard") })
            .Where(x => x.Release != null)
            .Select(x => new { x.OriginalName, Release = x.Release! });

        // Sort chronologically for display (version-first) or by stability for selection (stability-first)
        var sorted = chronological
            ? filtered
                .OrderByDescending(x => x.Release.Major)
                .ThenByDescending(x => x.Release.Minor)
                .ThenByDescending(x => x.Release.Type) // Stability within same minor version
                .ThenByDescending(x => x.Release.Patch) // Then patch number
            : filtered.OrderByDescending(x => x.Release);

        return sorted.Select(x => x.OriginalName);
    }

    public Release? TryCreateRelease(string versionString)
    {
        var release = Release.TryParse(versionString);
        if (release == null)
        {
            return null;
        }

        var platformStringResult = platformStringProvider.GetPlatformString(release);

        return platformStringResult switch
        {
            Result<string, PlatformError>.Success(var platformString) => release with
            {
                OS = hostSystem.SystemInfo.CurrentOS,
                PlatformString = platformString
            },
            _ => null
        };
    }

    public string? FindCompatibleVersion(string projectVersion, bool isDotNet, IEnumerable<string> installedVersions)
    {
        var result = FindCompatibleVersionResult(projectVersion, isDotNet, installedVersions);
        return result switch
        {
            Result<string, CompatibilityError>.Success(var version) => version,
            Result<string, CompatibilityError>.Failure => null,
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    public Result<string, CompatibilityError> FindCompatibleVersionResult(string projectVersion, bool isDotNet, IEnumerable<string> installedReleaseIds)
    {
        var versions = installedReleaseIds.ToList();

        if (versions.Count == 0)
        {
            return new Result<string, CompatibilityError>.Failure(new CompatibilityError.NoInstalledVersions());
        }

        var preferredRuntime = isDotNet ? "mono" : "standard";

        // First, try exact match
        var exactMatch = versions.FirstOrDefault(v => v == projectVersion);
        if (exactMatch != null)
        {
            return new Result<string, CompatibilityError>.Success(exactMatch);
        }

        // Parse all compatible releases and find the best match
        var compatibleReleases = versions
            .Select(TryCreateRelease)
            .Where(release => release != null)
            .Cast<Release>()
            .Where(release =>
            {
                // Check if this release matches our criteria
                var versionString = $"{release.Major}.{release.Minor}";
                bool isVersionMatch;

                if (projectVersion.Contains('.'))
                {
                    // Project version is like "4.3" - match exact major.minor
                    isVersionMatch = versionString == projectVersion;
                }
                else
                {
                    // Project version is like "4" - match major only
                    isVersionMatch = release.Major.ToString() == projectVersion;
                }

                return isVersionMatch &&
                       release.RuntimeEnvironment.ToString().Equals(preferredRuntime, StringComparison.CurrentCultureIgnoreCase);
            })
            .ToArray();

        if (compatibleReleases.Length == 0)
        {
            return new Result<string, CompatibilityError>.Failure(new CompatibilityError.NotFound(projectVersion, isDotNet));
        }

        // Use OrderByDescending with the built-in Release.CompareTo for preference ordering
        // Now that ReleaseType.CompareTo prefers higher RC numbers, this gives us:
        // 1. Higher version numbers first (4.3 > 4.2)
        // 2. More stable releases first (stable > rc2 > rc1 > dev)
        var bestRelease = compatibleReleases
            .OrderByDescending(r => r)
            .First();

        return new Result<string, CompatibilityError>.Success(bestRelease.ReleaseNameWithRuntime);
    }

    private Release? FindReleaseByQuery(string[] query, string[] releaseNames)
    {
        return query switch
        {
            // We handle empty query at the call site
            [] => throw new ArgumentException("Empty query"),
            // Handle latest stable standard
            ["latest"] => TryFilterLatest("stable", "standard", releaseNames),
            // Handle latest stable by with runtime
            ["latest", var releaseType and ("mono" or "standard")] => TryFilterLatest("stable", releaseType, releaseNames),
            // Handle latest standard with release type
            ["latest", var releaseType] when ReleaseType.Prefixes.Contains(releaseType, StringComparer.OrdinalIgnoreCase)
                => TryFilterLatest(releaseType, "standard", releaseNames),
            // Handle latest with release type and runtime
            ["latest", var releaseType, var runtime] when ReleaseType.Prefixes.Contains(releaseType, StringComparer.OrdinalIgnoreCase) &&
                                                          runtime is "mono" or "standard"
                => TryFilterLatest(releaseType, runtime, releaseNames),
            // Explicit version, i.e. `4.2-stable(-mono)`
            _ => TryFilterRelease(query, releaseNames)
        };
    }


    private Release? TryFilterRelease(string[] query, string[] releaseNames)
    {
        // Split on single arguments to for exact version queries like `4.2-stable-mono` or `4.3-beta2`
        if (query.Length == 1)
        {
            query = query[0].Split('-', StringSplitOptions.RemoveEmptyEntries);
        }

        var invalidArgs = ArgumentValidator.GetInvalidArguments(query);
        if (invalidArgs.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid arguments: {string.Join(", ", invalidArgs)}. Valid arguments are version numbers (e.g. `4`, `4.2`), release types ( {string.Join(", ", ReleaseType.Prefixes.Select(p => $"`{p}`"))}), and runtime environments (`mono`, `standard`).");
        }

        var runtime = query
            .FirstOrDefault(x => x is "mono" or "standard")?.ToLowerInvariant() == "mono"
            ? RuntimeEnvironment.Mono
            : RuntimeEnvironment.Standard;

        // Default to `stable` when release type isn't provided
        var releaseType = query
                              .FirstOrDefault(x => ReleaseType.Prefixes
                                  .Any(prefix => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                          ?? "";

        // Get the possible version query by filtering out the release type and runtime
        var possibleVersion = query
            .Except([runtime.Name(), releaseType], StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault("");

        if (possibleVersion.Length == 1 && releaseType.Length == 0)
        {
            releaseType = "stable";
        }

        // Try to find the first release that matches the criteria
        return releaseNames
            .Where(x => x.StartsWith(possibleVersion))
            .Where(x => string.IsNullOrEmpty(releaseType) || x.Contains(releaseType))
            .Select(releaseName => TryCreateRelease($"{releaseName}-{runtime.Name()}"))
            .OfType<Release>()
            .OrderByDescending(release => release)
            .FirstOrDefault();
    }


    private Release? TryFilterLatest(string type, string runtime, string[] releaseNames)
    {
        var version = releaseNames
            .OrderByDescending(x => x.Contains(type, StringComparison.InvariantCultureIgnoreCase))
            .FirstOrDefault();

        return TryCreateRelease($"{version}-{runtime}");
    }
}
