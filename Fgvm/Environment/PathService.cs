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
    ///     Path to the bin directory.
    /// </summary>
    string BinPath { get; }

    /// <summary>
    ///     Path to the Godot symlink.
    /// </summary>
    string SymlinkPath { get; }

    /// <summary>
    ///     Path to the macOS Godot.app symlink.
    /// </summary>
    string MacAppSymlinkPath { get; }

    /// <summary>
    ///     Path to the log directory.
    /// </summary>
    string LogPath { get; }
}

public sealed class PathService : IPathService
{
    private static string? _fgvmHomeEnvVar => System.Environment.GetEnvironmentVariable("FGVM_HOME");
    public string RootPath =>
        _fgvmHomeEnvVar is not null
            ? Path.GetFullPath("fgvm", _fgvmHomeEnvVar)
            : Path.GetFullPath("fgvm", System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
    public string ConfigPath => Path.Combine(RootPath, "fgvm.ini");
    public string ReleasesPath => Path.Combine(RootPath, "releases.json");
    public string BinPath => Path.Combine(RootPath, "bin");
    public string SymlinkPath => Path.Combine(BinPath, "godot");
    public string MacAppSymlinkPath => Path.Combine(BinPath, "Godot.app");
    public string LogPath => Path.Combine(RootPath, "fgvm.log");
}
