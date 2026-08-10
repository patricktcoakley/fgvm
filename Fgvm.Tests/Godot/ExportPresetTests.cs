using Fgvm.Types;

namespace Fgvm.Tests.Godot;

public sealed class ExportPresetTests
{
    [Theory]
    [InlineData(null, "Web", "build/web/index.html")]
    [InlineData("Web", null, "build/web/index.html")]
    [InlineData("Web", "Web", null)]
    [InlineData(" ", "\t", "\r\n")]
    public void IsConfigured_RequiresNonWhitespaceNamePlatformAndExportPath(string? name,
        string? platform,
        string? exportPath
    )
    {
        var preset = new ExportPreset(0, name, platform, exportPath, null);

        Assert.False(preset.IsConfigured);
    }
}
