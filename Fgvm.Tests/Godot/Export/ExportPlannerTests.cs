using Fgvm.Environment;
using Fgvm.Godot.Export;
using Fgvm.Types;

namespace Fgvm.Tests.Godot.Export;

public sealed class ExportPlannerTests
{
    private static readonly string ProjectRoot = Path.Combine(Path.GetTempPath(), "fgvm-plan-project");
    private static readonly string OutputRoot = Path.Combine(Path.GetTempPath(), "fgvm-plan-out");

    [Fact]
    public void Plan_WithConfiguredExportPath_HonorsTheProjectConfiguration()
    {
        var target = Single(Plan([Preset(0, "Windows Demo", "Windows Desktop", "build/windows/game.exe")]));

        Assert.Equal(Path.Combine(ProjectRoot, "build", "windows", "game.exe"), target.Destination);
        Assert.Equal(Path.Combine(ProjectRoot, "build", "windows"), target.ArtifactPath);
        Assert.Equal(ExportArtifactKind.Directory, target.Kind);
        Assert.Equal(GodotExportKind.Release, target.ExportKind);
    }

    [Fact]
    public void Plan_WithBlankExportPath_UsesAnIsolatedStagedDirectory()
    {
        var target = Single(Plan([Preset(0, "Web", "Web", "")]));

        Assert.Equal(Path.Combine(ProjectRoot, "dist", "web", "index.html"), target.Destination);
        Assert.Equal(Path.Combine(ProjectRoot, "dist", "web"), target.ArtifactPath);
        Assert.Equal(ExportArtifactKind.Directory, target.Kind);
    }

    [Fact]
    public void Plan_WithOutputRoot_IgnoresConfiguredPathsAndIsolatesEveryPreset()
    {
        var targets = Succeeded(Plan(
        [
            Preset(0, "Windows Demo", "Windows Desktop", "build/game.exe"),
            Preset(1, "Web", "Web", "public/index.html")
        ], OutputRoot));

        Assert.Equal(Path.Combine(OutputRoot, "windows-demo"), targets[0].ArtifactPath);
        Assert.Equal(Path.Combine(OutputRoot, "web"), targets[1].ArtifactPath);
        Assert.All(targets, target => Assert.Equal(ExportArtifactKind.Directory, target.Kind));
    }

    [Theory]
    [InlineData("Windows Desktop", "game.exe")]
    [InlineData("Linux", "game.x86_64")]
    [InlineData("macOS", "game.app")]
    [InlineData("Web", "index.html")]
    [InlineData("Android", "game.apk")]
    [InlineData("iOS", "game.zip")]
    [InlineData("Custom Console", "game.pkgx")]
    public void Plan_WithArchive_UsesANativeGodotZipPack(string platform, string fileName)
    {
        var target = Single(Plan([Preset(0, "Game", platform, $"build/{fileName}")], archive: true));

        Assert.Equal(Path.Combine(ProjectRoot, "build", Path.ChangeExtension(fileName, ".zip")), target.ArtifactPath);
        Assert.Equal(ExportArtifactKind.File, target.Kind);
        Assert.Equal(target.ArtifactPath, target.Destination);
        Assert.Equal(GodotExportKind.Pack, target.ExportKind);
    }

    [Fact]
    public void Plan_WithArchive_DoesNotNeedAPlatformSpecificDefaultFileName()
    {
        var target = Single(Plan([Preset(0, "Console", "Custom Console", null)], archive: true));

        Assert.Equal(Path.Combine(ProjectRoot, "dist", "console", "console.zip"), target.ArtifactPath);
        Assert.Equal(GodotExportKind.Pack, target.ExportKind);
    }

    [Fact]
    public void Plan_WithConfiguredLinuxZip_TreatsItAsAnExecutableNameRatherThanAnArchive()
    {
        var target = Single(Plan([Preset(0, "Linux", "Linux", "build/game.zip")]));

        Assert.Equal(ExportArtifactKind.Directory, target.Kind);
        Assert.Equal(Path.Combine(ProjectRoot, "build"), target.ArtifactPath);
    }

    [Fact]
    public void Plan_WithConfiguredWebZip_ReportsTheZipFile()
    {
        var target = Single(Plan([Preset(0, "Web", "Web", "build/web.zip")]));

        Assert.Equal(ExportArtifactKind.File, target.Kind);
        Assert.Equal(target.Destination, target.ArtifactPath);
    }

    [Fact]
    public void Plan_WithConfiguredMacApp_ReportsTheAppBundleRatherThanItsParent()
    {
        var target = Single(Plan([Preset(0, "macOS", "macOS", "build/MyGame.app")]));

        Assert.Equal(ExportArtifactKind.Directory, target.Kind);
        Assert.Equal(target.Destination, target.ArtifactPath);
        Assert.Equal(ExportDestinationKind.Directory, target.DestinationKind);
    }

    [Theory]
    [InlineData("Android", "build/game.apk")]
    [InlineData("Custom Console", "build/game.pkgx")]
    public void Plan_WithConfiguredPlatform_DoesNotSecondGuessGodot(string platform, string exportPath)
    {
        var target = Single(Plan([Preset(0, "Game", platform, exportPath)]));

        Assert.Equal(platform, target.Platform);
    }

    [Fact]
    public void Plan_WithUnknownPlatformAndBlankPath_RequiresAConfiguredPath()
    {
        var error = Failed(Plan([Preset(0, "Console", "Custom Console", null)]));

        Assert.Contains("export_path", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Linux", "name")]
    [InlineData("Linux", null, "platform")]
    public void Plan_WithAnIncompletePreset_Fails(string? name, string? platform, string missingField)
    {
        var error = Failed(Plan([Preset(0, name, platform, "build/game")]));

        Assert.Contains(missingField, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WithDuplicatePresetNames_FailsBeforeInvokingGodot()
    {
        var result = Plan([
            Preset(0, "Game", "Windows Desktop", "windows/game.exe"),
            Preset(1, "Game", "Linux", "linux/game")
        ]);

        Assert.Contains("unique", Failed(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WithExactDuplicateDestinations_Fails()
    {
        var result = Plan(
        [
            Preset(0, "One", "Linux", "build/game"),
            Preset(1, "Two", "Linux", "build/game")
        ]);

        Assert.Contains("One", Failed(result), StringComparison.Ordinal);
        Assert.Contains("Two", Failed(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WithDistinctFilesInTheSameConfiguredDirectory_FailsBecauseTheArtifactIsShared()
    {
        var result = Plan(
        [
            Preset(0, "Windows", "Windows Desktop", "build/game.exe"),
            Preset(1, "Linux", "Linux", "build/game.x86_64")
        ]);

        Assert.Contains("Windows", Failed(result), StringComparison.Ordinal);
        Assert.Contains("Linux", Failed(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WhenAFileArtifactMatchesAnotherPresetDestination_Fails()
    {
        var result = Plan(
        [
            Preset(0, "Web", "Web", "build/game.zip"),
            Preset(1, "Linux", "Linux", "build/game.zip")
        ]);

        Assert.Contains("Web", Failed(result), StringComparison.Ordinal);
        Assert.Contains("Linux", Failed(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WhenAStagedDirectoryWouldReplaceAnotherPresetOutput_Fails()
    {
        var result = Plan(
        [
            Preset(0, "Web", "Web", ""),
            Preset(1, "Linux", "Linux", "dist/web/game")
        ]);

        Assert.Contains("Web", Failed(result), StringComparison.Ordinal);
        Assert.Contains("Linux", Failed(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OnLinux_RejectsCaseDistinctFilesThatShareAnArtifactDirectory()
    {
        var result = Plan(
        [
            Preset(0, "Upper", "Linux", "build/Game"),
            Preset(1, "Lower", "Linux", "build/game")
        ], hostOS: OS.Linux);

        Assert.NotEmpty(Failed(result));
    }

    [Fact]
    public void Plan_OnWindows_RejectsCaseDistinctConfiguredDestinations()
    {
        var result = Plan(
        [
            Preset(0, "Upper", "Linux", "build/Game"),
            Preset(1, "Lower", "Linux", "build/game")
        ], hostOS: OS.Windows);

        Assert.NotEmpty(Failed(result));
    }

    [Fact]
    public void Plan_PreservesPresetOrder()
    {
        var targets = Succeeded(Plan(
        [
            Preset(0, "Windows", "Windows Desktop", null),
            Preset(1, "Linux", "Linux", null),
            Preset(2, "Web", "Web", null)
        ]));

        Assert.Equal(["Windows", "Linux", "Web"], targets.Select(target => target.Preset));
    }

    private static ExportPreset Preset(int index, string? name, string? platform, string? exportPath) =>
        new(index, name, platform, exportPath, null);

    private static Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError> Plan(IReadOnlyList<ExportPreset> presets,
        string? outputRoot = null,
        bool archive = false,
        OS hostOS = OS.Linux
    ) =>
        ExportPlanner.Plan(presets, ProjectRoot, hostOS, outputRoot, archive);

    private static PlannedExportTarget Single(Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError> result) =>
        Assert.Single(Succeeded(result));

    private static IReadOnlyList<PlannedExportTarget> Succeeded(Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError> result) =>
        result switch
        {
            Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Success(var targets) => targets,
            Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Failure(var error) =>
                throw new Xunit.Sdk.XunitException($"Expected a plan, got {error.Message}"),
            _ => throw new InvalidOperationException("Unexpected result type")
        };

    private static string Failed(Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError> result) =>
        result switch
        {
            Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Failure(var error) => error.Message,
            _ => throw new Xunit.Sdk.XunitException("Expected the plan to fail.")
        };
}
