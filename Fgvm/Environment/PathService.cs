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
    public string BinPath => Path.Combine(RootPath, "bin");

    /// <inheritdoc />
    public string SymlinkPath => Path.Combine(BinPath, "godot");

    /// <inheritdoc />
    public string MacAppSymlinkPath => Path.Combine(BinPath, "Godot.app");

    /// <inheritdoc />
    public string LogPath => Path.Combine(RootPath, "fgvm.log");
}
