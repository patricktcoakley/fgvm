using System.Runtime.InteropServices;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Tests.Environment;

namespace Fgvm.Tests.Godot;

[Collection(nameof(EnvironmentVariableCollection))]
public sealed class GodotPathServiceTests : IDisposable
{
    private readonly string? _originalAppData = System.Environment.GetEnvironmentVariable("APPDATA");
    private readonly string? _originalOverride = System.Environment.GetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride);
    private readonly string? _originalXdgDataHome = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    [Fact]
    public void ExportTemplatesRootPath_UsesOverride_WhenSet()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "godot-export-templates");
        System.Environment.SetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride, overridePath);

        var pathService = new GodotPathService(new SystemInfo(OS.Linux, Architecture.X64));

        Assert.Equal(Path.GetFullPath(overridePath), pathService.ExportTemplatesRootPath);
    }

    [Fact]
    public void ExportTemplatesRootPath_RejectsRelativeOverride()
    {
        System.Environment.SetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride, "relative/templates");

        var pathService = new GodotPathService(new SystemInfo(OS.Linux, Architecture.X64));

        var exception = Assert.Throws<InvalidOperationException>(() => pathService.ExportTemplatesRootPath);
        Assert.Equal("FGVM_GODOT_EXPORT_TEMPLATES_DIR must be an absolute path.", exception.Message);
    }

    [Fact]
    public void ExportTemplatesRootPath_UsesWindowsAppDataDefault()
    {
        System.Environment.SetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride, null);
        var appData = Path.Combine(Path.GetTempPath(), "AppData", "Roaming");
        System.Environment.SetEnvironmentVariable("APPDATA", appData);

        var pathService = new GodotPathService(new SystemInfo(OS.Windows, Architecture.X64));

        Assert.Equal(Path.Combine(appData, "Godot", "export_templates"), pathService.ExportTemplatesRootPath);
    }

    [Fact]
    public void ExportTemplatesRootPath_UsesMacOSDefault()
    {
        System.Environment.SetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride, null);
        var pathService = new GodotPathService(new SystemInfo(OS.MacOS, Architecture.Arm64));

        var expected = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Godot",
            "export_templates");

        Assert.Equal(expected, pathService.ExportTemplatesRootPath);
    }

    [Fact]
    public void ExportTemplatesRootPath_UsesXdgDataHomeDefault()
    {
        System.Environment.SetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride, null);
        var xdgDataHome = Path.Combine(Path.GetTempPath(), "xdg-data-home");
        System.Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDataHome);

        var pathService = new GodotPathService(new SystemInfo(OS.Linux, Architecture.X64));

        Assert.Equal(Path.Combine(xdgDataHome, "godot", "export_templates"), pathService.ExportTemplatesRootPath);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
        System.Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
        System.Environment.SetEnvironmentVariable(GodotPathService.ExportTemplatesDirectoryOverride, _originalOverride);
    }
}
