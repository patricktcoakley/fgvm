using Fgvm.Environment;
using Fgvm.Types;

namespace Fgvm.Godot;

/// <summary>
///     Resolves, filters, and fetches Godot releases.
/// </summary>
public interface IReleaseManager
{
    /// <summary>
    ///     Lists available release identifiers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Release identifiers, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when release lookup is canceled.</exception>
    Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken);

    /// <summary>
    ///     Searches available remote releases using the supplied query.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="fetchMode">Whether to use the local cache or force a remote refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching release identifiers, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when release lookup is canceled.</exception>
    Task<Result<IEnumerable<string>, NetworkError>> SearchRemoteReleases(string[] query,
        ReleaseFetchMode fetchMode,
        CancellationToken cancellationToken
    );

    /// <summary>
    ///     Gets SHA512 checksum metadata for a release.
    /// </summary>
    /// <param name="release">The release whose checksum metadata should be fetched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Checksum metadata, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<string, NetworkError>> GetSha512(Release release, CancellationToken cancellationToken);

    /// <summary>
    ///     Gets the release zip stream for a release file.
    /// </summary>
    /// <param name="filename">The zip filename to fetch.</param>
    /// <param name="release">The release that owns the zip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The zip stream, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the request is canceled.</exception>
    Task<Result<ZipDownload, NetworkError>> GetZipFile(string filename, Release release, CancellationToken cancellationToken);

    /// <summary>
    ///     Resolves a version query against available release identifiers.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="releaseIds">Available release identifiers.</param>
    /// <returns>The resolved release, or a query error.</returns>
    Result<Release, QueryError> ResolveReleaseQuery(string[] query, string[] releaseIds);

    /// <summary>
    ///     Resolves a version query without binding it to the current host platform.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="releaseIds">Available release identifiers.</param>
    /// <returns>The resolved release, or a query error.</returns>
    Result<Release, QueryError> ResolveReleaseQueryWithoutPlatform(string[] query, string[] releaseIds);

    /// <summary>
    ///     Filters release identifiers by version query.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="releaseNames">Release identifiers to filter.</param>
    /// <param name="chronological">Whether to sort by version-first display order instead of selection preference.</param>
    /// <returns>Matching release identifiers.</returns>
    IEnumerable<string> FilterReleasesByQuery(string[] query, string[] releaseNames, bool chronological = false);

    /// <summary>
    ///     Filters release identifiers without binding them to the current host platform.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="releaseNames">Release identifiers to filter.</param>
    /// <param name="chronological">Whether to sort by version-first display order instead of selection preference.</param>
    /// <returns>Matching release identifiers.</returns>
    IEnumerable<string> FilterReleasesByQueryWithoutPlatform(string[] query, string[] releaseNames, bool chronological = false);

    /// <summary>
    ///     Finds an installed release compatible with a project requirement.
    /// </summary>
    /// <param name="projectVersion">The project-required version.</param>
    /// <param name="isDotNet">Whether the project requires Mono/.NET.</param>
    /// <param name="installedReleaseIds">Installed release identifiers.</param>
    /// <returns>The compatible release identifier, or a compatibility error.</returns>
    Result<string, CompatibilityError> FindCompatibleVersionResult(string projectVersion,
        bool isDotNet,
        IEnumerable<string> installedReleaseIds
    );

    /// <summary>
    ///     Creates a release model for the current host platform.
    /// </summary>
    /// <param name="versionString">The release version string.</param>
    /// <returns>The release, or a parse/platform error.</returns>
    Result<Release, ReleaseParseError> CreateRelease(string versionString);

    /// <summary>
    ///     Creates a release model without host platform selection.
    /// </summary>
    /// <param name="versionString">The release version string.</param>
    /// <returns>The release, or a parse error.</returns>
    Result<Release, ReleaseParseError> CreateReleaseWithoutPlatform(string versionString);
}

public sealed class ReleaseManager(
    IHostSystem hostSystem,
    PlatformStringProvider platformStringProvider,
    IDownloadClient downloadClient,
    IReleaseCatalog releaseCatalog
) : IReleaseManager
{
    /// <inheritdoc />
    public async Task<Result<IEnumerable<string>, NetworkError>> ListReleases(CancellationToken cancellationToken)
    {
        var result = await releaseCatalog.ReadReleaseIds(ReleaseFetchMode.UseCache, cancellationToken);
        return result switch
        {
            Result<string[], NetworkError>.Success(var releases) =>
                new Result<IEnumerable<string>, NetworkError>.Success(releases),
            Result<string[], NetworkError>.Failure(NetworkError.ManifestRefreshFailure(var releaseIds)) =>
                new Result<IEnumerable<string>, NetworkError>.Failure(
                    new NetworkError.ManifestRefreshFailure(releaseIds)),
            Result<string[], NetworkError>.Failure(var error) =>
                new Result<IEnumerable<string>, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public async Task<Result<IEnumerable<string>, NetworkError>> SearchRemoteReleases(string[] query,
        ReleaseFetchMode fetchMode,
        CancellationToken cancellationToken
    )
    {
        var result = await releaseCatalog.ReadReleaseIds(fetchMode, cancellationToken);
        return result switch
        {
            Result<string[], NetworkError>.Success(var releases) =>
                new Result<IEnumerable<string>, NetworkError>.Success(
                    FilterReleasesByQuery(query, releases, true)),
            Result<string[], NetworkError>.Failure(NetworkError.ManifestRefreshFailure(var releaseIds)) =>
                new Result<IEnumerable<string>, NetworkError>.Failure(
                    new NetworkError.ManifestRefreshFailure(FilterReleasesByQuery(query, releaseIds.ToArray(), true).ToArray())),
            Result<string[], NetworkError>.Failure(var error) =>
                new Result<IEnumerable<string>, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public async Task<Result<string, NetworkError>> GetSha512(Release release, CancellationToken cancellationToken) =>
        await downloadClient.GetSha512(release, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<ZipDownload, NetworkError>>
        GetZipFile(string filename, Release release, CancellationToken cancellationToken) =>
        await downloadClient.GetZipFile(filename, release, cancellationToken);

    /// <inheritdoc />
    public Result<Release, QueryError> ResolveReleaseQuery(string[] query, string[] releaseIds)
    {
        if (query.Length == 0)
        {
            return new Result<Release, QueryError>.Failure(new QueryError.EmptyQuery());
        }

        return FindReleaseByQuery(query, releaseIds, TryCreateRelease) switch
        {
            Result<Release?, QueryError>.Success(var release) when release is not null =>
                new Result<Release, QueryError>.Success(release),
            Result<Release?, QueryError>.Success =>
                new Result<Release, QueryError>.Failure(new QueryError.NotFound(string.Join(" ", query))),
            Result<Release?, QueryError>.Failure(var error) =>
                new Result<Release, QueryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<Release, QueryError> ResolveReleaseQueryWithoutPlatform(string[] query, string[] releaseIds)
    {
        if (query.Length == 0)
        {
            return new Result<Release, QueryError>.Failure(new QueryError.EmptyQuery());
        }

        return FindReleaseByQuery(query, releaseIds, TryCreateReleaseWithoutPlatform) switch
        {
            Result<Release?, QueryError>.Success(var release) when release is not null =>
                new Result<Release, QueryError>.Success(release),
            Result<Release?, QueryError>.Success =>
                new Result<Release, QueryError>.Failure(new QueryError.NotFound(string.Join(" ", query))),
            Result<Release?, QueryError>.Failure(var error) =>
                new Result<Release, QueryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public IEnumerable<string> FilterReleasesByQuery(string[] query, string[] releaseNames, bool chronological = false)
        => FilterReleasesByQueryCore(query, releaseNames, chronological, TryCreateRelease);

    /// <inheritdoc />
    public IEnumerable<string> FilterReleasesByQueryWithoutPlatform(string[] query, string[] releaseNames, bool chronological = false)
        => FilterReleasesByQueryCore(query, releaseNames, chronological, TryCreateReleaseWithoutPlatform);

    private IEnumerable<string> FilterReleasesByQueryCore(string[] query,
        string[] releaseNames,
        bool chronological,
        Func<string, Release?> createRelease
    )
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
            .Select(name => new { OriginalName = name, Release = createRelease(name) ?? createRelease($"{name}-standard") })
            .Where(x => x.Release != null)
            .Select(x => new { x.OriginalName, Release = x.Release! });

        // Display/search paths use the natural Release ordering. Selection paths use explicit preference ordering.
        var sorted = chronological
            ? filtered.OrderByDescending(x => x.Release)
            : OrderBySelectionPreference(filtered, x => x.Release);

        return sorted.Select(x => x.OriginalName);
    }

    /// <inheritdoc />
    public Result<Release, ReleaseParseError> CreateRelease(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.EmptyVersion());
        }

        var release = Release.TryParse(versionString);
        if (release == null)
        {
            return new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.InvalidVersion(versionString));
        }

        var platformStringResult = platformStringProvider.GetPlatformString(release);

        return platformStringResult switch
        {
            Result<string, PlatformError>.Success(var platformString) =>
                new Result<Release, ReleaseParseError>.Success(release with
                {
                    OS = hostSystem.SystemInfo.CurrentOS,
                    PlatformString = platformString
                }),
            Result<string, PlatformError>.Failure(PlatformError.Unsupported(var unsupportedRelease, var os, var arch)) =>
                new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.UnsupportedPlatform(unsupportedRelease, os, arch)),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<Release, ReleaseParseError> CreateReleaseWithoutPlatform(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.EmptyVersion());
        }

        var release = Release.TryParse(versionString);
        return release is null
            ? new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.InvalidVersion(versionString))
            : new Result<Release, ReleaseParseError>.Success(release);
    }

    /// <inheritdoc />
    public Result<string, CompatibilityError> FindCompatibleVersionResult(string projectVersion,
        bool isDotNet,
        IEnumerable<string> installedReleaseIds
    )
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

        // Compatibility keeps project resolution stability-first instead of treating prereleases as upgrades.
        var bestRelease = OrderBySelectionPreference(compatibleReleases, release => release)
            .First();

        return new Result<string, CompatibilityError>.Success(bestRelease.ReleaseNameWithRuntime);
    }

    internal Release? TryFindReleaseByQuery(string[] query, string[] releaseNames)
    {
        var result = ResolveReleaseQuery(query, releaseNames);
        return result switch
        {
            Result<Release, QueryError>.Success(var release) => release,
            Result<Release, QueryError>.Failure => null,
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    internal Release? TryFindReleaseByQueryWithoutPlatform(string[] query, string[] releaseNames)
    {
        var result = ResolveReleaseQueryWithoutPlatform(query, releaseNames);
        return result switch
        {
            Result<Release, QueryError>.Success(var release) => release,
            Result<Release, QueryError>.Failure => null,
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    internal Release? TryCreateRelease(string versionString)
    {
        var result = CreateRelease(versionString);
        return result is Result<Release, ReleaseParseError>.Success(var release) ? release : null;
    }

    internal Release? TryCreateReleaseWithoutPlatform(string versionString)
    {
        var result = CreateReleaseWithoutPlatform(versionString);
        return result is Result<Release, ReleaseParseError>.Success(var release) ? release : null;
    }

    internal string? FindCompatibleVersion(string projectVersion, bool isDotNet, IEnumerable<string> installedVersions)
    {
        var result = FindCompatibleVersionResult(projectVersion, isDotNet, installedVersions);
        return result switch
        {
            Result<string, CompatibilityError>.Success(var version) => version,
            Result<string, CompatibilityError>.Failure => null,
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private Result<Release?, QueryError> FindReleaseByQuery(string[] query,
        string[] releaseNames,
        Func<string, Release?> createRelease
    )
    {
        var release = query switch
        {
            // Handle latest stable standard
            ["latest"] => TryFilterLatest("stable", "standard", releaseNames, createRelease),
            // Handle latest stable by with runtime
            ["latest", var releaseType and ("mono" or "standard")] => TryFilterLatest("stable", releaseType, releaseNames, createRelease),
            // Handle latest standard with release type
            ["latest", var releaseType] when ReleaseType.Prefixes.Contains(releaseType, StringComparer.OrdinalIgnoreCase)
                => TryFilterLatest(releaseType, "standard", releaseNames, createRelease),
            // Handle latest with release type and runtime
            ["latest", var releaseType, var runtime] when ReleaseType.Prefixes.Contains(releaseType, StringComparer.OrdinalIgnoreCase) &&
                                                          runtime is "mono" or "standard"
                => TryFilterLatest(releaseType, runtime, releaseNames, createRelease),
            // Explicit version, i.e. `4.2-stable(-mono)`
            _ => null
        };

        if (release is not null)
        {
            return new Result<Release?, QueryError>.Success(release);
        }

        return query switch
        {
            ["latest"] or ["latest", _] or ["latest", _, _] =>
                new Result<Release?, QueryError>.Success(null),
            _ => TryFilterRelease(query, releaseNames, createRelease)
        };
    }


    private Result<Release?, QueryError> TryFilterRelease(string[] query,
        string[] releaseNames,
        Func<string, Release?> createRelease
    )
    {
        // Split on single arguments to for exact version queries like `4.2-stable-mono` or `4.3-beta2`
        if (query.Length == 1)
        {
            query = query[0].Split('-', StringSplitOptions.RemoveEmptyEntries);
        }

        var invalidArgs = ArgumentValidator.GetInvalidArguments(query);
        if (invalidArgs.Count > 0)
        {
            return new Result<Release?, QueryError>.Failure(
                new QueryError.InvalidQuery(
                    $"Invalid arguments: {string.Join(", ", invalidArgs)}. Valid arguments are version numbers (e.g. `4`, `4.2`), release types ( {string.Join(", ", ReleaseType.Prefixes.Select(p => $"`{p}`"))}), and runtime environments (`mono`, `standard`)."));
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
        var candidates = releaseNames
            .Where(x => x.StartsWith(possibleVersion))
            .Where(x => string.IsNullOrEmpty(releaseType) || x.Contains(releaseType))
            .Select(releaseName => createRelease($"{releaseName}-{runtime.Name()}"))
            .OfType<Release>();

        var release = OrderBySelectionPreference(candidates, candidate => candidate).FirstOrDefault();

        return new Result<Release?, QueryError>.Success(release);
    }


    private Release? TryFilterLatest(string type,
        string runtime,
        string[] releaseNames,
        Func<string, Release?> createRelease
    )
    {
        var version = releaseNames
            .OrderByDescending(x => x.Contains(type, StringComparison.InvariantCultureIgnoreCase))
            .FirstOrDefault();

        return createRelease($"{version}-{runtime}");
    }

    private static IOrderedEnumerable<T> OrderBySelectionPreference<T>(IEnumerable<T> source, Func<T, Release> releaseSelector) =>
        source
            .OrderByDescending(item => releaseSelector(item).Major)
            .ThenByDescending(item => releaseSelector(item).Type)
            .ThenByDescending(item => releaseSelector(item).Minor)
            .ThenByDescending(item => releaseSelector(item).Patch)
            .ThenByDescending(item => releaseSelector(item).RuntimeEnvironment)
            .ThenByDescending(item => releaseSelector(item).ReleaseNameWithRuntime, StringComparer.Ordinal);
}
