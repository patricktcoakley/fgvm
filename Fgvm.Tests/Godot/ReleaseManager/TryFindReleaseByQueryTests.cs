using Fgvm.Godot;
using Fgvm.Types;

namespace Fgvm.Tests.Godot.ReleaseManager;

using static TestData;

public class TryFindReleaseByQueryTests
{
    [Fact]
    public void TryFindReleaseByQuery_ValidArguments_DoesNotThrow()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();
        string[][] testCases =
        [
            ["4", "stable", "mono"],
            ["4.2", "rc1"],
            ["4.2.1"],
            ["latest", "stable"],
            ["4.3-beta2", "standard"],
            ["mono"],
            ["standard"]
        ];

        foreach (var query in testCases)
        {
            var exception = Record.Exception(() =>
                releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray()));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void ResolveReleaseQuery_InvalidArguments_ReturnsInvalidQueryFailure()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();
        string[][] testCases =
        [
            ["4", "invalidarg"],
            ["4", "five", "xi"],
            ["invalidversion", "mono"],
            ["4.2", "unknowntype"],
            ["stable", "badruntime"]
        ];

        foreach (var query in testCases)
        {
            var result = releaseManager.ResolveReleaseQuery(query, TestReleases.ToArray());

            var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
            var invalid = Assert.IsType<QueryError.InvalidQuery>(failure.Error);
            Assert.Contains("Invalid arguments:", invalid.Message);
        }
    }

    [Fact]
    public void TryFindReleaseByQuery_SingleStringWithRuntimeSuffix_ParsesCorrectly()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        // Valid queries with runtime suffix should work
        string[][] validTestCases =
        [
            ["4.3-stable-mono"],
            ["4.2-rc1-mono"],
            ["4.5-stable-standard"],
            ["4.3-beta2-mono"]
        ];

        foreach (var query in validTestCases)
        {
            var exception = Record.Exception(() =>
                releaseManager.TryFindReleaseByQuery(query, TestReleases.ToArray()));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void ResolveReleaseQuery_SingleStringWithInvalidParts_ReturnsInvalidQueryFailure()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        string[][] invalidTestCases =
        [
            ["4.5-stabl-mono"], // typo in "stable" - will split to ["4.5", "stabl", "mono"]
            ["4.5-st-mono"], // incomplete release type - will split to ["4.5", "st", "mono"]
            ["4.5-invalidtype-mono"] // invalid release type - will split to ["4.5", "invalidtype", "mono"]
        ];

        foreach (var query in invalidTestCases)
        {
            var result = releaseManager.ResolveReleaseQuery(query, TestReleases.ToArray());

            var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
            var invalid = Assert.IsType<QueryError.InvalidQuery>(failure.Error);
            Assert.Contains("Invalid arguments:", invalid.Message);
        }
    }

    [Fact]
    public void ResolveReleaseQuery_InvalidReleaseTypeWithValidRuntimeSuffix_ReturnsInvalidQueryFailure()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.ResolveReleaseQuery(["4.5-bad-mono"], TestReleases.ToArray());

        var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
        var invalid = Assert.IsType<QueryError.InvalidQuery>(failure.Error);
        Assert.Contains("Invalid arguments:", invalid.Message);
    }

    [Fact]
    public void ResolveReleaseQuery_InvalidReleaseTypeWithoutRuntimeSuffix_ReturnsInvalidQueryFailure()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.ResolveReleaseQuery(["4.5-bad"], TestReleases.ToArray());

        var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
        var invalid = Assert.IsType<QueryError.InvalidQuery>(failure.Error);
        Assert.Contains("Invalid arguments:", invalid.Message);
    }

    [Fact]
    public void TryFindReleaseByQuery_MonoSuffix_ReturnsMonoRelease()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.TryFindReleaseByQuery(["4.2-stable-mono"], TestReleases.ToArray());

        Assert.NotNull(result);
        Assert.Equal(RuntimeEnvironment.Mono, result.RuntimeEnvironment);
        Assert.Equal("4.2-stable-mono", result.ReleaseNameWithRuntime);
    }

    [Fact]
    public void TryFindReleaseByQuery_StandardSuffix_ReturnsStandardRelease()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.TryFindReleaseByQuery(["4.2-stable-standard"], TestReleases.ToArray());

        Assert.NotNull(result);
        Assert.Equal(RuntimeEnvironment.Standard, result.RuntimeEnvironment);
        Assert.Equal("4.2-stable-standard", result.ReleaseNameWithRuntime);
    }

    [Fact]
    public void ResolveReleaseQuery_ValidQuery_ReturnsSuccess()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.ResolveReleaseQuery(["4.2-stable-standard"], TestReleases.ToArray());

        var success = Assert.IsType<Result<Release, QueryError>.Success>(result);
        Assert.Equal("4.2-stable-standard", success.Value.ReleaseNameWithRuntime);
    }

    [Fact]
    public void ResolveReleaseQuery_InvalidQuery_ReturnsInvalidQueryFailure()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.ResolveReleaseQuery(["4.5-bad"], TestReleases.ToArray());

        var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
        Assert.IsType<QueryError.InvalidQuery>(failure.Error);
    }

    [Fact]
    public void ResolveReleaseQuery_NoMatch_ReturnsNotFoundFailure()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.ResolveReleaseQuery(["9.9"], TestReleases.ToArray());

        var failure = Assert.IsType<Result<Release, QueryError>.Failure>(result);
        Assert.IsType<QueryError.NotFound>(failure.Error);
    }
}
