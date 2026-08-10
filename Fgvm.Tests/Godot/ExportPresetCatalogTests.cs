using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Godot;

public sealed class ExportPresetCatalogTests : IDisposable
{
    private readonly ExportPresetCatalog _catalog;
    private readonly string _tempDirectory;

    public ExportPresetCatalogTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"fgvm-export-presets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        var pathService = new Mock<IPathService>();
        var hostSystem = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);
        _catalog = new ExportPresetCatalog(hostSystem);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void Read_WithCompletePresets_ReturnsAllPresetMetadataInIndexOrder()
    {
        WritePresets("""
                     [preset.1]

                     name="Web"
                     platform="Web"
                     runnable=false
                     export_path="build/web/index.html"

                     [preset.0]

                     name="Linux/X11"
                     platform="Linux/X11"
                     runnable=true
                     export_path="build/linux/game.x86_64"
                     """);

        var presets = ReadPresets();

        Assert.Collection(
            presets,
            preset =>
            {
                Assert.Equal(0, preset.Index);
                Assert.Equal("Linux/X11", preset.Name);
                Assert.Equal("Linux/X11", preset.Platform);
                Assert.True(preset.Runnable is true);
                Assert.Equal("build/linux/game.x86_64", preset.ExportPath);
                Assert.True(preset.IsConfigured);
            },
            preset =>
            {
                Assert.Equal(1, preset.Index);
                Assert.Equal("Web", preset.Name);
                Assert.Equal("Web", preset.Platform);
                Assert.True(preset.Runnable is false);
                Assert.Equal("build/web/index.html", preset.ExportPath);
                Assert.True(preset.IsConfigured);
            });
    }

    [Fact]
    public void Read_WithoutPresetZero_ReturnsEmptyCatalog()
    {
        WritePresets("""
                     [preset.1]
                     name="Web"
                     platform="Web"
                     export_path="build/web/index.html"
                     """);

        var presets = ReadPresets();

        Assert.Empty(presets);
    }

    [Fact]
    public void Read_AfterIndexGap_IgnoresLaterPresets()
    {
        WritePresets("""
                     [preset.0]
                     name="Linux"
                     platform="Linux/BSD"
                     export_path="build/linux/game.x86_64"

                     [preset.2]
                     name="Web"
                     platform="Web"
                     export_path="build/web/index.html"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal(0, preset.Index);
        Assert.Equal("Linux", preset.Name);
    }

    [Fact]
    public void Read_WithNoExportPresetsFile_ReturnsEmptyCatalog()
    {
        var presets = ReadPresets();

        Assert.Empty(presets);
    }

    [Fact]
    public void Read_WithIncompletePreset_PreservesItWithoutMarkingItConfigured()
    {
        WritePresets("""
                     [preset.0]

                     name="Linux/X11"
                     platform="Linux/X11"
                     runnable=true
                     export_path=""
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.False(preset.IsConfigured);
    }

    [Fact]
    public void Read_WithOmittedRunnable_PreservesMissingValue()
    {
        WritePresets("""
                     [preset.0]
                     name="Web"
                     platform="Web"
                     export_path="build/web/index.html"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Null(preset.Runnable);
        Assert.True(preset.IsConfigured);
    }

    [Fact]
    public void Read_IgnoresPresetOptionsAndUnrelatedSections()
    {
        WritePresets("""
                     [preset.0]
                     name="Web"
                     platform="Web"
                     runnable=true
                     export_path="build/web/index.html"
                     advanced_options=false
                     custom_features="dedicated_server"

                     [preset.0.options]
                     name="not the preset name"
                     platform="Android"
                     export_path="outside.zip"

                     [application]
                     name="also ignored"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Web", preset.Name);
        Assert.Equal("Web", preset.Platform);
        Assert.Equal("build/web/index.html", preset.ExportPath);
    }

    [Fact]
    public void Read_WithGodotComments_StripsSemicolonsOnlyOutsideStrings()
    {
        WritePresets("""
                     ; file comment
                     [preset.0] ; section comment
                     name="Web; \"Release\"" ; field comment
                     platform="Web" ; field comment
                     runnable=false ; field comment
                     export_path="build/web;beta/index.html" ; field comment
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Web; \"Release\"", preset.Name);
        Assert.Equal("build/web;beta/index.html", preset.ExportPath);
        Assert.False(preset.Runnable);
    }

    [Fact]
    public void Read_WithHashPseudoComment_ReturnsLineAwareParseError()
    {
        WritePresets("""
                     [preset.0]
                     # not a Godot ConfigFile comment
                     name="Web"
                     platform="Web"
                     export_path="build/web/index.html"
                     """);

        var result = _catalog.Read(_tempDirectory);

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.InvalidConfiguration>(failure.Error);
        Assert.Equal(2, error.Line);
        Assert.Contains("key/value", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_DecodesQuotedEscapesInRelevantValues()
    {
        WritePresets("""
                     [preset.0]
                     name="Windows \"Desktop\""
                     platform="Windows Desktop"
                     runnable=true
                     export_path="build\\windows\\game.exe"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Windows \"Desktop\"", preset.Name);
        Assert.Equal("build\\windows\\game.exe", preset.ExportPath);
    }

    [Theory]
    [InlineData("perhaps")]
    [InlineData("TRUE")]
    [InlineData("False")]
    public void Read_WithInvalidRunnable_ReturnsLineAwareParseError(string value)
    {
        WritePresets($"""
                      [preset.0]
                      name="Web"
                      platform="Web"
                      runnable={value}
                      export_path="build/web/index.html"
                      """);

        var result = _catalog.Read(_tempDirectory);

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.InvalidConfiguration>(failure.Error);
        Assert.Equal(4, error.Line);
        Assert.Contains("runnable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("name=Web", "quoted string")]
    [InlineData("name=\"Web\\q\"", "unsupported escape")]
    [InlineData("name=\"Web\" trailing", "after the closing quote")]
    [InlineData("name=\"Web\\", "unterminated escape")]
    [InlineData("name=\"Web", "missing closing quote")]
    public void Read_WithMalformedRelevantString_ReturnsSpecificParseError(string assignment, string expectedMessage)
    {
        WritePresets($"[preset.0]\n{assignment}\n");

        var result = _catalog.Read(_tempDirectory);

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.InvalidConfiguration>(failure.Error);
        Assert.Equal(2, error.Line);
        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WithDuplicateRelevantKey_ReturnsParseError()
    {
        WritePresets("""
                     [preset.0]
                     name="Web"
                     name="Duplicate"
                     """);

        var result = _catalog.Read(_tempDirectory);

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.InvalidConfiguration>(failure.Error);
        Assert.Equal(3, error.Line);
        Assert.Contains("duplicate name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenPresetFileCannotBeRead_ReturnsFileAccessError()
    {
        var fileError = new FileOperationError.PermissionDenied("/project/export_presets.cfg");
        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(system => system.ReadAllText(It.IsAny<string>()))
            .Returns(new Result<string, FileOperationError>.Failure(fileError));
        var catalog = new ExportPresetCatalog(hostSystem.Object);

        var result = catalog.Read("/project");

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.FileAccess>(failure.Error);
        Assert.Equal(fileError, error.Error);
    }

    [Fact]
    public void Read_WhenFileReadFails_ReturnsFileAccessError()
    {
        var fileError = new FileOperationError.IoFailure("/project/export_presets.cfg");
        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(system => system.ReadAllText(It.IsAny<string>()))
            .Returns(new Result<string, FileOperationError>.Failure(fileError));
        var catalog = new ExportPresetCatalog(hostSystem.Object);

        var result = catalog.Read("/project");

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.FileAccess>(failure.Error);
        Assert.Equal(fileError, error.Error);
    }

    [Fact]
    public void Read_WithDuplicatePresetIndex_ReturnsParseError()
    {
        WritePresets("""
                     [preset.0]
                     name="Linux"

                     [preset.0]
                     name="Web"
                     """);

        var result = _catalog.Read(_tempDirectory);

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.InvalidConfiguration>(failure.Error);
        Assert.Equal(4, error.Line);
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WithMultilineOptionScript_IgnoresItsBodyEntirely()
    {
        WritePresets("""
                     [preset.0]
                     name="Windows Desktop"
                     platform="Windows Desktop"
                     runnable=true
                     export_path="build/windows/game.exe"

                     [preset.0.options]
                     ssh_remote_deploy/run_script="Param($a)
                     [System.IO.File]::WriteAllText($a, 'x')
                     $trigger = New-ScheduledTaskTrigger -Once -At 00:00
                     Start-ScheduledTask -TaskName godot_remote_debug"
                     binary_format/architecture="x86_64"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Windows Desktop", preset.Name);
        Assert.Equal("build/windows/game.exe", preset.ExportPath);
    }

    [Fact]
    public void Read_WithPresetHeaderInsideMultilineString_DoesNotCreateAPhantomPreset()
    {
        WritePresets("""
                     [preset.0]
                     name="Linux"
                     platform="Linux"
                     export_path="build/linux/game"

                     [preset.0.options]
                     ssh_remote_deploy/cleanup_script="echo start
                     [preset.1]
                     echo done"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Linux", preset.Name);
    }

    [Fact]
    public void Read_WithDuplicatePresetHeaderInsideMultilineString_DoesNotReportADuplicateIndex()
    {
        WritePresets("""
                     [preset.0]
                     name="Linux"
                     platform="Linux"
                     export_path="build/linux/game"

                     [preset.0.options]
                     ssh_remote_deploy/run_script="echo start
                     [preset.0]
                     echo done"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Linux", preset.Name);
    }

    [Fact]
    public void Read_WithMultilineValueForUnconsumedKey_IgnoresIt()
    {
        WritePresets("""
                     [preset.0]
                     name="Linux"
                     platform="Linux"
                     export_path="build/linux/game"
                     custom_features="alpha
                     beta"
                     """);

        var preset = Assert.Single(ReadPresets());

        Assert.Equal("Linux", preset.Name);
        Assert.Equal("build/linux/game", preset.ExportPath);
    }

    [Fact]
    public void Read_WithUnterminatedValueOnIgnoredKey_DoesNotSilentlyDiscardLaterPresets()
    {
        WritePresets("""
                     [preset.0]
                     name="Web"
                     platform="Web"
                     custom_features="oops
                     export_path="build/web/index.html"

                     [preset.1]
                     name="Linux"
                     platform="Linux"
                     export_path="build/linux/game"
                     """);

        var result = _catalog.Read(_tempDirectory);

        // Either outcome is defensible; silently returning one incomplete preset is not.
        switch (result)
        {
            case Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure:
                return;
            case Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(var presets):
                Assert.Equal(2, presets.Count);
                return;
        }
    }

    [Fact]
    public void Read_WithMultilineConsumedValue_ReturnsLineAwareParseError()
    {
        WritePresets("""
                     [preset.0]
                     name="Web
                     still the name"
                     platform="Web"
                     """);

        var result = _catalog.Read(_tempDirectory);

        var failure = Assert.IsType<Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure>(result);
        var error = Assert.IsType<ExportPresetCatalogError.InvalidConfiguration>(failure.Error);
        Assert.Equal(2, error.Line);
        Assert.Contains("name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<ExportPreset> ReadPresets() =>
        _catalog.Read(_tempDirectory) switch
        {
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(var presets) => presets,
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(var error) =>
                throw new Xunit.Sdk.XunitException($"Expected export presets, got {error}"),
            _ => throw new InvalidOperationException("Unexpected result type")
        };

    private void WritePresets(string content) =>
        File.WriteAllText(Path.Combine(_tempDirectory, ExportPresetCatalog.FileName), content);
}
