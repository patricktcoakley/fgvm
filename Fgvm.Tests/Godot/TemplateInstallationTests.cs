using Fgvm.Godot;
using Fgvm.Types;

namespace Fgvm.Tests.Godot;

public sealed class TemplateInstallationTests
{
    [Theory]
    [InlineData("4.6.2.stable", "4.6.2-stable-standard", RuntimeEnvironment.Standard)]
    [InlineData("4.6.3.stable.mono", "4.6.3-stable-mono", RuntimeEnvironment.Mono)]
    [InlineData("4.5.rc1", "4.5-rc1-standard", RuntimeEnvironment.Standard)]
    [InlineData("4.5.dev3.mono", "4.5-dev3-mono", RuntimeEnvironment.Mono)]
    public void TryCreate_ParsesTemplateDirectoryNames(string templateVersion,
        string expectedRelease,
        RuntimeEnvironment expectedRuntime
    )
    {
        var installation = TemplateInstallation.TryCreate(templateVersion, $"/templates/{templateVersion}");

        Assert.NotNull(installation);
        Assert.Equal(templateVersion, installation.TemplateVersion);
        Assert.Equal(expectedRelease, installation.ReleaseNameWithRuntime);
        Assert.Equal(expectedRuntime, installation.RuntimeEnvironment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("4.6")]
    [InlineData("4.6.stable.extra")]
    [InlineData("x.y.stable")]
    public void TryCreate_ReturnsNullForInvalidTemplateDirectoryNames(string templateVersion)
    {
        Assert.Null(TemplateInstallation.TryCreate(templateVersion, $"/templates/{templateVersion}"));
    }

    [Fact]
    public void ToTemplateVersion_FormatsMonoAndStandardReleases()
    {
        var standard = Release.TryParse("4.6.2-stable-standard");
        var mono = Release.TryParse("4.6.3-stable-mono");

        Assert.NotNull(standard);
        Assert.NotNull(mono);
        Assert.Equal("4.6.2.stable", TemplateInstallation.ToTemplateVersion(standard));
        Assert.Equal("4.6.3.stable.mono", TemplateInstallation.ToTemplateVersion(mono));
    }
}
