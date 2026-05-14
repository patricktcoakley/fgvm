using Fgvm.Types;

namespace Fgvm.Tests.Godot.ReleaseManager;

public class FindCompatibleVersionTests
{
    private static readonly IEnumerable<string> TestInstalledVersions =
    [
        "4.3-stable-standard",
        "4.3-stable-mono",
        "4.3-rc1-standard",
        "4.3-rc1-mono",
        "4.2-stable-standard",
        "4.2-stable-mono",
        "4.2-rc2-standard",
        "4.1-stable-standard",
        "4.0-beta3-standard",
        "3.5-stable-standard",
        "3.5-stable-mono"
    ];

    [Theory]
    [InlineData("4.3", false, "4.3-stable-standard")]
    [InlineData("4.3", true, "4.3-stable-mono")]
    [InlineData("4.2", false, "4.2-stable-standard")]
    [InlineData("4.2", true, "4.2-stable-mono")]
    [InlineData("4", false, "4.3-stable-standard")]
    [InlineData("4", true, "4.3-stable-mono")]
    [InlineData("3.5", false, "3.5-stable-standard")]
    [InlineData("3.5", true, "3.5-stable-mono")]
    public void FindCompatibleVersion_ValidProjectVersion_ReturnsCorrectVersion(string projectVersion, bool isDotNet, string expected)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, TestInstalledVersions);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3-stable-standard", false, "4.3-stable-standard")]
    [InlineData("4.3-stable-mono", true, "4.3-stable-mono")]
    [InlineData("4.2-rc2-standard", false, "4.2-rc2-standard")]
    public void FindCompatibleVersion_ExactMatch_ReturnsExactMatch(string projectVersion, bool isDotNet, string expected)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, TestInstalledVersions);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("5.0", false)]
    [InlineData("5.0", true)]
    [InlineData("3.4", false)]
    [InlineData("3.4", true)]
    public void FindCompatibleVersion_VersionNotAvailable_ReturnsNull(string projectVersion, bool isDotNet)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, TestInstalledVersions);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("4.3", false)]
    [InlineData("4.1", true)]
    public void FindCompatibleVersion_RuntimeMismatch_ReturnsNull(string projectVersion, bool isDotNet)
    {
        IEnumerable<string> limitedVersions = isDotNet
            ? new[] { "4.3-stable-standard", "4.1-stable-standard" }
            : new[] { "4.3-stable-mono", "4.1-stable-mono" };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, limitedVersions);

        Assert.Null(result);
    }

    [Fact]
    public void FindCompatibleVersion_EmptyInstalledVersions_ReturnsNull()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion("4.3", false, []);

        Assert.Null(result);
    }

    [Fact]
    public void FindCompatibleVersionResult_ValidProjectVersion_ReturnsSuccess()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersionResult("4.3", false, TestInstalledVersions);

        var success = Assert.IsType<Result<string, CompatibilityError>.Success>(result);
        Assert.Equal("4.3-stable-standard", success.Value);
    }

    [Fact]
    public void FindCompatibleVersionResult_EmptyInstalledVersions_ReturnsNoInstalledVersions()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersionResult("4.3", false, []);

        var failure = Assert.IsType<Result<string, CompatibilityError>.Failure>(result);
        Assert.IsType<CompatibilityError.NoInstalledVersions>(failure.Error);
    }

    [Fact]
    public void FindCompatibleVersionResult_NoMatch_ReturnsNotFound()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersionResult("9.9", false, TestInstalledVersions);

        var failure = Assert.IsType<Result<string, CompatibilityError>.Failure>(result);
        Assert.IsType<CompatibilityError.NotFound>(failure.Error);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-stable-standard")]
    [InlineData("4.3", true, "4.3-stable-mono")]
    public void FindCompatibleVersion_MultipleReleasesAvailable_PrefersStableOverRC(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithRC = new[]
        {
            "4.3-rc1-standard",
            "4.3-rc1-mono",
            "4.3-stable-standard",
            "4.3-stable-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithRC);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-rc2-standard")]
    [InlineData("4.3", true, "4.3-rc2-mono")]
    public void FindCompatibleVersion_MultipleRCVersions_PrefersHigherRCNumber(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithMultipleRC = new[]
        {
            "4.3-rc1-standard",
            "4.3-rc1-mono",
            "4.3-rc2-standard",
            "4.3-rc2-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithMultipleRC);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4", false, "4.3-stable-standard")]
    [InlineData("4", true, "4.3-stable-mono")]
    public void FindCompatibleVersion_MajorVersionOnly_ReturnsLatestMinorVersion(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithMultipleMinor = new[]
        {
            "4.1-stable-standard",
            "4.1-stable-mono",
            "4.2-stable-standard",
            "4.2-stable-mono",
            "4.3-stable-standard",
            "4.3-stable-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithMultipleMinor);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("invalid-version", false)]
    [InlineData("", false)]
    [InlineData("not.a.version", true)]
    public void FindCompatibleVersion_InvalidVersionStrings_ReturnsNull(string projectVersion, bool isDotNet)
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, TestInstalledVersions);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-beta2-standard")]
    [InlineData("4.3", true, "4.3-beta2-mono")]
    public void FindCompatibleVersion_MultipleBetaVersions_PrefersHigherBetaNumber(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithMultipleBeta = new[]
        {
            "4.3-beta1-standard",
            "4.3-beta1-mono",
            "4.3-beta2-standard",
            "4.3-beta2-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithMultipleBeta);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-alpha3-standard")]
    [InlineData("4.3", true, "4.3-alpha3-mono")]
    public void FindCompatibleVersion_MultipleAlphaVersions_PrefersHigherAlphaNumber(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithMultipleAlpha = new[]
        {
            "4.3-alpha1-standard",
            "4.3-alpha1-mono",
            "4.3-alpha3-standard",
            "4.3-alpha3-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithMultipleAlpha);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-dev5-standard")]
    [InlineData("4.3", true, "4.3-dev5-mono")]
    public void FindCompatibleVersion_MultipleDevVersions_PrefersHigherDevNumber(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithMultipleDev = new[]
        {
            "4.3-dev2-standard",
            "4.3-dev2-mono",
            "4.3-dev5-standard",
            "4.3-dev5-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithMultipleDev);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-stable-standard")]
    [InlineData("4.3", true, "4.3-stable-mono")]
    public void FindCompatibleVersion_AllReleaseTypes_PrefersStableOverAll(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithAllTypes = new[]
        {
            "4.3-dev1-standard",
            "4.3-dev1-mono",
            "4.3-alpha2-standard",
            "4.3-alpha2-mono",
            "4.3-beta3-standard",
            "4.3-beta3-mono",
            "4.3-rc1-standard",
            "4.3-rc1-mono",
            "4.3-stable-standard",
            "4.3-stable-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithAllTypes);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-rc2-standard")]
    [InlineData("4.3", true, "4.3-rc2-mono")]
    public void FindCompatibleVersion_NoStable_PrefersRCOverBetaAlphaDev(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithoutStable = new[]
        {
            "4.3-dev1-standard",
            "4.3-dev1-mono",
            "4.3-alpha2-standard",
            "4.3-alpha2-mono",
            "4.3-beta1-standard",
            "4.3-beta1-mono",
            "4.3-rc2-standard",
            "4.3-rc2-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithoutStable);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-beta3-standard")]
    [InlineData("4.3", true, "4.3-beta3-mono")]
    public void FindCompatibleVersion_NoStableOrRC_PrefersBetaOverAlphaDev(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithoutStableOrRC = new[]
        {
            "4.3-dev2-standard",
            "4.3-dev2-mono",
            "4.3-alpha1-standard",
            "4.3-alpha1-mono",
            "4.3-beta3-standard",
            "4.3-beta3-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithoutStableOrRC);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-alpha4-standard")]
    [InlineData("4.3", true, "4.3-alpha4-mono")]
    public void FindCompatibleVersion_NoStableRCBeta_PrefersAlphaOverDev(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithoutStableRCBeta = new[]
        {
            "4.3-dev1-standard",
            "4.3-dev1-mono",
            "4.3-alpha4-standard",
            "4.3-alpha4-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithoutStableRCBeta);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3", false, "4.3-dev7-standard")]
    [InlineData("4.3", true, "4.3-dev7-mono")]
    public void FindCompatibleVersion_OnlyDevVersions_PrefersHigherDevNumber(string projectVersion, bool isDotNet, string expected)
    {
        var versionsOnlyDev = new[]
        {
            "4.3-dev3-standard",
            "4.3-dev3-mono",
            "4.3-dev7-standard",
            "4.3-dev7-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsOnlyDev);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("4.3-beta2-standard", false, "4.3-beta2-standard")]
    [InlineData("4.3-alpha1-mono", true, "4.3-alpha1-mono")]
    [InlineData("4.3-dev5-standard", false, "4.3-dev5-standard")]
    public void FindCompatibleVersion_ExactMatchAllReleaseTypes_ReturnsExactMatch(string projectVersion, bool isDotNet, string expected)
    {
        var versionsWithAllTypes = new[]
        {
            "4.3-dev5-standard",
            "4.3-dev5-mono",
            "4.3-alpha1-standard",
            "4.3-alpha1-mono",
            "4.3-beta2-standard",
            "4.3-beta2-mono",
            "4.3-rc1-standard",
            "4.3-rc1-mono",
            "4.3-stable-standard",
            "4.3-stable-mono"
        };

        var releaseManager = new ReleaseManagerBuilder().Build();

        var result = releaseManager.FindCompatibleVersion(projectVersion, isDotNet, versionsWithAllTypes);

        Assert.Equal(expected, result);
    }
}
