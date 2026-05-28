using Fgvm.Godot;

namespace Fgvm.Tests.Godot;

public class ReleaseTests
{
    [Theory]
    [InlineData("4.2-stable", 4, 2, null, "stable", RuntimeEnvironment.Standard)]
    [InlineData("4.2.1-stable", 4, 2, 1, "stable", RuntimeEnvironment.Standard)]
    [InlineData("4.2-rc1", 4, 2, null, "rc1", RuntimeEnvironment.Standard)]
    [InlineData("3.6.1-beta2", 3, 6, 1, "beta2", RuntimeEnvironment.Standard)]
    [InlineData("4.2-dev3", 4, 2, null, "dev3", RuntimeEnvironment.Standard)]
    [InlineData("4.2-stable-mono", 4, 2, null, "stable", RuntimeEnvironment.Mono)]
    [InlineData("3.5.1-alpha1-mono", 3, 5, 1, "alpha1", RuntimeEnvironment.Mono)]
    [InlineData("4.3.1-rc4-mono", 4, 3, 1, "rc4", RuntimeEnvironment.Mono)]
    public void TryParse_ValidVersions_ReturnsTrue(string input,
        int expectedMajor,
        int expectedMinor,
        int? expectedPatch,
        string expectedReleaseType,
        RuntimeEnvironment expectedRuntimeEnvironment
    )
    {
        var release = Release.TryParse(input);

        Assert.NotNull(release);
        Assert.Equal(expectedMajor, release.Major);
        Assert.Equal(expectedMinor, release.Minor);
        Assert.Equal(expectedPatch, release.Patch);
        Assert.NotNull(release.Type);
        Assert.Equal(expectedReleaseType, release.Type);
        Assert.Equal(expectedRuntimeEnvironment, release.RuntimeEnvironment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("4.x")]
    [InlineData("invalid")]
    [InlineData("4.2-invalid")]
    public void TryParse_InvalidVersions_ReturnsFalse(string input)
    {
        var release = Release.TryParse(input);

        Assert.Null(release);
    }

    [Theory]
    [InlineData("4.2-stable")]
    [InlineData("4.2-stable-mono")]
    public void TryParse_WithRuntimeOverride_UsesProvidedRuntime(string input)
    {
        var release = Release.TryParse(input);
        var expectedRuntime = input.Contains("mono") ? RuntimeEnvironment.Mono : RuntimeEnvironment.Standard;
        Assert.NotNull(release);
        Assert.Equal(expectedRuntime, release.RuntimeEnvironment);
    }

    [Theory]
    [InlineData("4.2-stable-standard", "4.1-stable-standard", 1)]
    [InlineData("4.2-stable-standard", "4.3-stable-standard", -1)]
    [InlineData("4.2-stable-standard", "4.2.1-stable-standard", -1)]
    [InlineData("4.2.1-stable-standard", "4.2.0-stable-standard", 1)]
    [InlineData("4.2.2-stable-standard", "4.2.1-stable-standard", 1)]
    [InlineData("4.5.1-rc1-standard", "4.5-stable-standard", 1)]
    [InlineData("4.5-dev1-standard", "4.4.1-stable-standard", 1)]
    public void CompareTo_VersionNumbers_ComparesCorrectly(string version1, string version2, int expectedSign)
    {
        var release1 = Release.TryParse(version1);
        var release2 = Release.TryParse(version2);
        Assert.NotNull(release1);

        var result = release1.CompareTo(release2);

        Assert.NotNull(release1);
        Assert.NotNull(release2);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(result));
    }

    [Theory]
    [InlineData("4.2-stable-standard", "4.2-rc1-standard", 1)]
    [InlineData("4.2-rc1-standard", "4.2-beta1-standard", 1)]
    [InlineData("4.2-beta1-standard", "4.2-alpha1-standard", 1)]
    [InlineData("4.2-alpha1-standard", "4.2-dev1-standard", 1)]
    [InlineData("4.2-dev1-standard", "4.2-stable-standard", -1)]
    [InlineData("4.2-rc3-standard", "4.2-rc1-standard", 1)]
    [InlineData("4.2-beta5-standard", "4.2-beta2-standard", 1)]
    public void CompareTo_ReleaseTypes_ComparesCorrectly(string version1, string version2, int expectedSign)
    {
        var release1 = Release.TryParse(version1);
        var release2 = Release.TryParse(version2);

        Assert.NotNull(release1);
        var result = release1.CompareTo(release2);

        Assert.NotNull(release1);
        Assert.NotNull(release2);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(result));
    }

    [Theory]
    [InlineData("4.2-stable-mono", "4.2-stable-standard", 1)]
    [InlineData("4.2-stable-standard", "4.2-stable-mono", -1)]
    public void CompareTo_RuntimeEnvironment_ComparesCorrectly(string version1, string version2, int expectedSign)
    {
        var release1 = Release.TryParse(version1);
        var release2 = Release.TryParse(version2);

        Assert.NotNull(release1);

        var result = release1.CompareTo(release2);

        Assert.NotNull(release1);
        Assert.NotNull(release2);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(result));
    }

    [Fact]
    public void CompareTo_OriginalIssueScenario_SortsCorrectly()
    {
        var versions = new[]
        {
            "4.5-dev5-standard",
            "4.5-beta5-standard",
            "4.4.1-stable-standard",
            "4.4.1-stable-mono"
        };

        var releases = versions
            .Select(Release.TryParse)
            .OfType<Release>()
            .OrderByDescending(r => r)
            .ToList();

        Assert.Equal("4.5-beta5-standard", releases[0].ReleaseNameWithRuntime);
        Assert.Equal("4.5-dev5-standard", releases[1].ReleaseNameWithRuntime);
        Assert.Equal("4.4.1-stable-mono", releases[2].ReleaseNameWithRuntime);
        Assert.Equal("4.4.1-stable-standard", releases[3].ReleaseNameWithRuntime);
    }

    [Fact]
    public void CompareTo_ComprehensiveSorting_HandlesAllScenarios()
    {
        var versions = new[]
        {
            "3.6-stable-standard",
            "4.0.1-stable-standard",
            "4.1-dev2-standard",
            "4.2.0-stable-standard",
            "4.2.1-stable-standard",
            "4.2.2-stable-standard",
            "4.2-dev1-standard",
            "4.2-alpha2-standard",
            "4.2-beta4-standard",
            "4.2-rc3-standard",
            "4.2-stable-standard",
            "4.2.1-stable-mono"
        };

        var releases = versions
            .Select(Release.TryParse)
            .OfType<Release>()
            .OrderByDescending(r => r)
            .Select(r => r.ReleaseNameWithRuntime)
            .ToList();

        var expectedOrder = new[]
        {
            "4.2.2-stable-standard",
            "4.2.1-stable-mono",
            "4.2.1-stable-standard",
            "4.2.0-stable-standard",
            "4.2-stable-standard",
            "4.2-rc3-standard",
            "4.2-beta4-standard",
            "4.2-alpha2-standard",
            "4.2-dev1-standard",
            "4.1-dev2-standard",
            "4.0.1-stable-standard",
            "3.6-stable-standard"
        };

        Assert.Equal(expectedOrder, releases);
    }
}
