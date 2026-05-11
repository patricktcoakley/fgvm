namespace Fgvm.Services;

public interface IGodotArgumentService
{
    /// <summary>
    ///     Determines if certain Godot command line arguments require attached mode for proper output.
    /// </summary>
    /// <param name="argumentString">The command line arguments passed to Godot</param>
    /// <returns>True if attached mode should be forced</returns>
    bool ShouldForceAttachedMode(string? argumentString);
}

public sealed class GodotArgumentService : IGodotArgumentService
{
    private static readonly HashSet<string> AttachedModeFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        // General options
        "--help", "-h", "--version", "--verbose", "-v", "--quiet", "-q",
        "--headless", "--no-header",

        // Debug and profiling options
        "--debug", "-d", "--print-fps", "--debug-stringnames",
        "--gpu-profile", "--profiling", "--benchmark", "--benchmark-file",

        // Scripting and automation
        "--script", "-s", "--check-only",

        // Project operations
        "--import", "--export-release", "--export-debug", "--export-pack",
        "--export-patch", "--convert-3to4", "--validate-conversion-3to4",

        // Documentation tools
        "--doctool", "--gdscript-docs", "--gdextension-docs",

        // Build and development tools
        "--build-solutions", "--dump-gdextension-interface",
        "--dump-extension-api", "--dump-extension-api-with-docs",
        "--validate-extension-api", "--install-android-build-template"
    };

    /// <inheritdoc />
    public bool ShouldForceAttachedMode(string? argumentString)
    {
        if (string.IsNullOrEmpty(argumentString))
        {
            return false;
        }

        var args = argumentString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return args.Any(arg => AttachedModeFlags.Contains(arg));
    }
}
