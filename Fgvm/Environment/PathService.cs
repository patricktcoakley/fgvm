namespace Fgvm.Environment;

/// <summary>
///     Service for providing path-related functionality.
/// </summary>
public interface IPathService
{
    /// <summary>
    ///     The root path for Fgvm installations and configuration.
    /// </summary>
    string RootPath { get; }

    /// <summary>
    ///     Path to the Fgvm configuration file.
    /// </summary>
    string ConfigPath { get; }

    /// <summary>
    ///     Path to the releases catalog file.
    /// </summary>
    string ReleasesPath { get; }

    /// <summary>
    ///     Path to the installation registry file.
    /// </summary>
    string InstallationsPath { get; }

    /// <summary>
    ///     Path to the target-aware installations directory.
    /// </summary>
    string InstallationsDirectoryPath { get; }

    /// <summary>
    ///     Path to the bin directory.
    /// </summary>
    string BinPath { get; }

    /// <summary>
    ///     Path to the stable command shim.
    /// </summary>
    string ShimPath { get; }

    /// <summary>
    ///     Path to the selected executable symlink used outside macOS.
    /// </summary>
    string SymlinkPath { get; }

    /// <summary>
    ///     Path to the selected macOS Godot.app symlink.
    /// </summary>
    string MacAppSymlinkPath { get; }

    /// <summary>
    ///     Path to the selected Windows shortcut.
    /// </summary>
    string WindowsShortcutPath { get; }


    /// <summary>
    ///     Path to the log directory.
    /// </summary>
    string LogPath { get; }
}

public sealed class PathService : IPathService
{
    private static string? _fgvmHomeEnvVar => System.Environment.GetEnvironmentVariable("FGVM_HOME");

    /// <inheritdoc />
    public string RootPath =>
        _fgvmHomeEnvVar is not null
            ? Path.GetFullPath("fgvm", _fgvmHomeEnvVar)
            : Path.GetFullPath("fgvm", System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

    /// <inheritdoc />
    public string ConfigPath => Path.Combine(RootPath, "fgvm.ini");

    /// <inheritdoc />
    public string ReleasesPath => Path.Combine(RootPath, "releases.json");

    /// <inheritdoc />
    public string InstallationsPath => Path.Combine(RootPath, "installations.json");

    /// <inheritdoc />
    public string InstallationsDirectoryPath => Path.Combine(RootPath, "installations");

    /// <inheritdoc />
    public string BinPath => Path.Combine(RootPath, "bin");

    /// <inheritdoc />
    public string ShimPath => OperatingSystem.IsWindows()
        ? Path.Combine(BinPath, "godot.cmd")
        : Path.Combine(BinPath, "godot");

    /// <inheritdoc />
    public string SymlinkPath => OperatingSystem.IsWindows()
        ? Path.Combine(RootPath, "Godot.exe")
        : Path.Combine(RootPath, "Godot");

    /// <inheritdoc />
    public string MacAppSymlinkPath => Path.Combine(RootPath, "Godot.app");

    /// <inheritdoc />
    public string WindowsShortcutPath => Path.Combine(RootPath, "Godot.url");

    /// <inheritdoc />
    public string LogPath => Path.Combine(RootPath, "fgvm.log");
}
