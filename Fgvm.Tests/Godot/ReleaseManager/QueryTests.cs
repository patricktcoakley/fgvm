using Fgvm.Godot;
using Fgvm.Types;
using Moq;

namespace Fgvm.Tests.Godot.ReleaseManager;

using static TestData;

public class QueryTests
{
    [Theory]
    [MemberData(nameof(SearchReleaseTestCases), MemberType = typeof(TestData))]
    public async Task SearchReleases_Query_ReturnsExpectedResults(string[] query, string[] expected)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();
        var result = await releaseManager.SearchRemoteReleases(query, ReleaseFetchMode.UseCache, CancellationToken.None);
        Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(result);
        var success = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(result);
        Assert.Equal(expected, success.Value);
    }

    [InlineData(new[] { "latest" }, "4.2-stable-standard")]
    [InlineData(new[] { "latest", "mono" }, "4.2-stable-mono")]
    [InlineData(new[] { "latest", "standard" }, "4.2-stable-standard")]
    [InlineData(new[] { "latest", "rc" }, "4.1-rc2-standard")]
    [InlineData(new[] { "latest", "beta" }, "4.0-beta3-standard")]
    [Theory]
    public void FindReleaseByQuery_Latest_ReturnsCorrectVersion(string[] query, string expected)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();
        var result = releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray());
        Assert.NotNull(result);
        Assert.Equal(expected, result.ReleaseNameWithRuntime);
    }


    [InlineData("latest")]
    [InlineData("latest", "standard")]
    [Theory]
    public void FindReleaseByQuery_Latest_ShouldReturnLatestStableWithStandard(params string[] query)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();
        var result = releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray());

        Assert.NotNull(result);
        Assert.Equal("4.2-stable-standard", result.ReleaseNameWithRuntime);
    }

    [InlineData(new[] { "3.1" }, "3.1.2-rc1-standard")]
    [InlineData(new[] { "4.4" }, "4.4-dev5-standard")]
    [InlineData(new[] { "4" }, "4.2-stable-standard")]
    [InlineData(new[] { "4", "mono" }, "4.2-stable-mono")]
    [InlineData(new[] { "3" }, "3.5-stable-standard")]
    [InlineData(new[] { "4.2", "standard" }, "4.2-stable-standard")]
    [InlineData(new[] { "3.5", "standard" }, "3.5-stable-standard")]
    [InlineData(new[] { "standard", "4.2" }, "4.2-stable-standard")]
    [InlineData(new[] { "standard", "3.5" }, "3.5-stable-standard")]
    [InlineData(new[] { "4.2", "mono" }, "4.2-stable-mono")]
    [InlineData(new[] { "4", "stable" }, "4.2-stable-standard")]
    [InlineData(new[] { "4", "rc", "mono" }, "4.1-rc2-mono")]
    [InlineData(new[] { "rc", "mono" }, "4.1-rc2-mono")]
    [InlineData(new[] { "3.5", "mono" }, "3.5-stable-mono")]
    [InlineData(new[] { "4", "beta" }, "4.0-beta3-standard")]
    [InlineData(new[] { "3", "alpha4" }, "3.2-alpha4-standard")]
    [InlineData(new[] { "4.4", "mono", "dev5" }, "4.4-dev5-mono")]
    [InlineData(new[] { "alpha4" }, "3.2-alpha4-standard")]
    [Theory]
    public void FindReleaseByQuery_SpecificVersion_ShouldParseVersionCorrectly(string[] query, string expected)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();
        var result = releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray());

        Assert.NotNull(result);
        Assert.Equal(expected, result.ReleaseNameWithRuntime);
    }

    [InlineData]
    [InlineData("5.1", "mono")]
    [InlineData("5.1", "standard")]
    [Theory]
    public void TryFindReleaseByQuery_InvalidVersion_ShouldReturnNull(params string[] query)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        if (query.Length == 0)
        {
            var result = releaseManager.ResolveReleaseQuery(query, TestReleases.ToArray());
            var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
            Assert.IsType<QueryError.EmptyQuery>(failure.Error);
        }
        else
        {
            Assert.Null(releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray()));
        }
    }


    [InlineData]
    [InlineData("latest", "mono", "standard")]
    [InlineData("latest", "stable", "rc")]
    [Theory]
    public void TryFindReleaseByQuery_InvalidLatestQuery_ShouldReturnNull(params string[] query)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        if (query.Length == 0)
        {
            var result = releaseManager.ResolveReleaseQuery(query, TestReleases.ToArray());
            var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
            Assert.IsType<QueryError.EmptyQuery>(failure.Error);
        }
        else
        {
            Assert.Null(releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray()));
        }
    }

    [Fact]
    public async Task SearchRemoteReleases_WithEmptyResults_ReturnsEmptyList()
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithReleases(new List<string>())
            .Build();

        var result = await releaseManager.SearchRemoteReleases(["stable"], ReleaseFetchMode.UseCache, CancellationToken.None);
        Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(result);
        var success = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(result);
        Assert.Empty(success.Value);
    }

    [Fact]
    public async Task SearchRemoteReleases_ManifestRefreshFailure_FiltersCachedReleaseIdsInFailure()
    {
        var releaseManager = new ReleaseManagerBuilder()
            .ConfigureReleaseCatalog(mock =>
            {
                mock.Setup(x => x.ReadReleaseIds(ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Result<string[], NetworkError>.Failure(
                        new NetworkError.ManifestRefreshFailure(["4.5-stable", "3.5-stable"])));
            })
            .Build();

        var result = await releaseManager.SearchRemoteReleases(["4"], ReleaseFetchMode.UseCache, CancellationToken.None);

        var failure = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Failure>(result);
        var manifestRefreshFailure = Assert.IsType<NetworkError.ManifestRefreshFailure>(failure.Error);
        Assert.Equal(["4.5-stable"], manifestRefreshFailure.releaseIds);
    }

    [Fact]
    public async Task SearchRemoteReleases_WithCancellation_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        var releaseManager = new ReleaseManagerBuilder()
            .ConfigureReleaseCatalog(mock =>
            {
                mock.Setup(x => x.ReadReleaseIds(It.IsAny<ReleaseFetchMode>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new OperationCanceledException());
            })
            .Build();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            releaseManager.SearchRemoteReleases(["stable"], ReleaseFetchMode.UseCache, cts.Token));
    }

    [Fact]
    public void FindReleaseByQuery_4_5_ShouldSelectBetaOverDev()
    {
        var releaseNames = new[]
        {
            "4.5-dev5",
            "4.5-dev4",
            "4.5-dev3",
            "4.5-beta3",
            "4.5-beta2",
            "4.5-beta1"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.TryFindReleaseByQuery(["4.5"], releaseNames);

        Assert.NotNull(result);
        Assert.Equal("4.5-beta3-standard", result.ReleaseNameWithRuntime);
    }

    [Fact]
    public void FindReleaseByQuery_4_5_ShouldSelectStableOverHigherPatchRC()
    {
        var releaseNames = new[]
        {
            "4.6-dev1",
            "4.5.1-rc1",
            "4.5-stable",
            "4.5-rc2",
            "4.5-rc1",
            "4.5-dev5"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.TryFindReleaseByQuery(["4.5"], releaseNames);

        Assert.NotNull(result);
        Assert.Equal("4.5-stable-standard", result.ReleaseNameWithRuntime);
    }

    [Fact]
    public void FindReleaseByQuery_4_ShouldSelectHighestStableOverNewerUnstable()
    {
        var releaseNames = new[]
        {
            "4.5-dev1",
            "4.4-stable",
            "4.3-stable",
            "4.2-rc1",
            "4.1-beta2"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.TryFindReleaseByQuery(["4"], releaseNames);

        Assert.NotNull(result);
        // Should pick 4.4-stable, not 4.5-dev1
        Assert.Equal("4.4-stable-standard", result.ReleaseNameWithRuntime);
    }

    [Fact]
    public async Task SearchReleases_ShouldSortByVersionFirst()
    {
        var releaseNames = new[]
        {
            "4.5-dev1",
            "4.5.1-rc1",
            "4.5-stable",
            "4.4-stable",
            "4.3-beta1"
        };

        var releaseManager = new ReleaseManagerBuilder()
            .WithReleases(releaseNames)
            .Build();

        // Test chronological ordering (for search/display)
        var chronologicalResult = await releaseManager.SearchRemoteReleases(["4"], ReleaseFetchMode.UseCache, CancellationToken.None);
        var success = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(chronologicalResult);
        var chronologicalArray = success.Value.ToArray();

        // Should be ordered by version first, then release type within the same version.
        Assert.Equal("4.5.1-rc1", chronologicalArray[0]);
        Assert.Equal("4.5-stable", chronologicalArray[1]);
        Assert.Equal("4.5-dev1", chronologicalArray[2]);
        Assert.Equal("4.4-stable", chronologicalArray[3]);
        Assert.Equal("4.3-beta1", chronologicalArray[4]);

        // Test stability-first ordering (for selection)
        var stabilityResult = releaseManager.FilterReleasesByQuery(["4"], releaseNames).ToArray();

        // Should be ordered: stable releases first, then rc, then beta, then dev
        Assert.Equal("4.5-stable", stabilityResult[0]);
        Assert.Equal("4.4-stable", stabilityResult[1]);
        Assert.Equal("4.5.1-rc1", stabilityResult[2]);
        Assert.Equal("4.3-beta1", stabilityResult[3]);
        Assert.Equal("4.5-dev1", stabilityResult[4]);
    }

    [Fact]
    public void FindReleaseByQuery_WhenOnlyUnstableAvailable_SelectsBestUnstable()
    {
        var releaseNames = new[]
        {
            "4.5-dev5",
            "4.5-dev4",
            "4.5-beta3",
            "4.5-beta2",
            "4.5-beta1"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.TryFindReleaseByQuery(["4.5"], releaseNames);

        Assert.NotNull(result);
        // When no stable exists, pick the most stable type available (beta > dev)
        Assert.Equal("4.5-beta3-standard", result.ReleaseNameWithRuntime);
    }
}
