using Fgvm.Environment;

namespace Fgvm.Godot;

public interface IGodotPathService
{
    string ExportTemplatesRootPath { get; }

    string GetExportTemplateVersionPath(string templateVersion);
}

public sealed class GodotPathService(SystemInfo systemInfo) : IGodotPathService
{
    public const string ExportTemplatesDirectoryOverride = "FGVM_GODOT_EXPORT_TEMPLATES_DIR";

    public string ExportTemplatesRootPath
    {
        get
        {
            var overridePath = System.Environment.GetEnvironmentVariable(ExportTemplatesDirectoryOverride);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                if (!Path.IsPathFullyQualified(overridePath))
                {
                    throw new InvalidOperationException($"{ExportTemplatesDirectoryOverride} must be an absolute path.");
                }

                return Path.GetFullPath(overridePath);
            }

            return Path.Combine(GetGodotEditorDataPath(), "export_templates");
        }
    }

    public string GetExportTemplateVersionPath(string templateVersion) =>
        Path.Combine(ExportTemplatesRootPath, templateVersion);

    private string GetGodotEditorDataPath()
    {
        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return systemInfo.CurrentOS switch
        {
            OS.Windows => Path.Combine(GetWindowsAppDataPath(userProfile), "Godot"),
            OS.MacOS => Path.Combine(userProfile, "Library", "Application Support", "Godot"),
            OS.Linux or OS.FreeBSD => Path.Combine(GetXdgDataHome(userProfile), "godot"),
            _ => throw new PlatformNotSupportedException($"Godot editor data path is not known for {systemInfo.CurrentOS}.")
        };
    }

    private static string GetWindowsAppDataPath(string userProfile)
    {
        var appData = System.Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return appData;
        }

        var specialFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        return !string.IsNullOrWhiteSpace(specialFolder)
            ? specialFolder
            : Path.Combine(userProfile, "AppData", "Roaming");
    }

    private static string GetXdgDataHome(string userProfile)
    {
        var xdgDataHome = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return Path.Combine(userProfile, ".local", "share");
        }

        if (!Path.IsPathFullyQualified(xdgDataHome))
        {
            throw new InvalidOperationException("XDG_DATA_HOME must be an absolute path.");
        }

        return Path.GetFullPath(xdgDataHome);
    }
}
