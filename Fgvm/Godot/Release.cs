using Fgvm.Environment;

namespace Fgvm.Godot;

/// <summary>
///     A type to represent Godot releases. Currently, there is only support for MAJOR.MINOR(.PATCH), but in previous
///     editions of Godot there were "sub-patches", such as `2.0.4.1`. There's not much reason to handle these because in
///     all
///     likelihood people are using 3+, which is more closely following a more standardized versioning approach. Godot
///     itself does not follow semver, but it tries to mostly be compatible, and so we don't want to strictly follow semver
///     either.
/// </summary>
public sealed record Release : IComparable<Release>
{
    /// <summary>
    /// </summary>
    /// <param name="major">Major version</param>
    /// <param name="minor">Minor version</param>
    /// <param name="patch">Patch version</param>
    /// <param name="type">Release type ("stable", "rc1", etc)</param>
    /// <param name="runtimeEnvironment">Standard or Mono</param>
    /// <param name="platformString">The platform string, i.e., "linux_x86_64"</param>
    internal Release(
        int major,
        int minor,
        string? platformString = null,
        int? patch = null,
        ReleaseType? type = null,
        RuntimeEnvironment runtimeEnvironment = RuntimeEnvironment.Standard)
    {
        Major = major;
        Minor = minor;
        Patch = patch;

        PlatformString = platformString;
        Type = type;
        RuntimeEnvironment = runtimeEnvironment;

        Version = $"{major}.{minor}" + (patch is null ? string.Empty : $".{patch}");
        ReleaseName = $"{Version}-{type}";
        ReleaseNameWithRuntime = $"{ReleaseName}-{runtimeEnvironment.Name()}";
    }

    // Version 1.x releases had a different naming schema
    public string FileName => Major == 1 ? $"Godot_v{Version}_{Type}_{PlatformString}" : $"Godot_v{Version}-{Type}_{PlatformString}";

    public string ZipFileName => $"{FileName}.zip";

    public string ExecName => OS switch
    {
        OS.MacOS => RuntimeEnvironment == RuntimeEnvironment.Mono ? "Godot_mono.app" : "Godot.app",
        OS.Linux when RuntimeEnvironment == RuntimeEnvironment.Mono => FileName.Replace("linux_", "linux."),
        OS.Windows when RuntimeEnvironment == RuntimeEnvironment.Mono => $"{FileName}.exe",
        _ => FileName
    };

    /// <summary>Major version</summary>
    public int Major { get; }

    /// <summary>Minor version</summary>
    public int Minor { get; }
    public string? PlatformString { get; init; }
    public OS OS { get; init; }

    /// <summary>Patch version</summary>
    public int? Patch { get; }

    /// <summary>Standard or Mono</summary>
    public RuntimeEnvironment RuntimeEnvironment { get; }

    /// <summary>Release type ("stable", "rc", etc)</summary>
    public ReleaseType? Type { get; }

    /// <summary>MAJOR.MINOR(.PATCH)</summary>
    public string Version { get; }

    /// <summary>The name of the release itself, i.e., `4.3-stable`</summary>
    public string ReleaseName { get; }

    /// <summary>The release name with a runtime suffix, i.e., `4.3-stable-mono`</summary>
    public string ReleaseNameWithRuntime { get; }

    /// <summary>Gets the runtime display suffix for logging (e.g., " [.NET]" for Mono, empty for Standard)</summary>
    public string RuntimeDisplaySuffix => RuntimeEnvironment == RuntimeEnvironment.Mono ? " [[.NET]]" : "";

    /// <summary>Gets whether this release uses .NET runtime (true for Mono, false for Standard)</summary>
    public bool IsDotNet => RuntimeEnvironment == RuntimeEnvironment.Mono;

    public int CompareTo(Release? other)
    {
        if (ReferenceEquals(this, other))
        {
            return 0;
        }

        if (other is null)
        {
            return 1;
        }

        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        // Prefer stable release types before higher minor/patch versions
        // This ensures: 4.2-stable > 4.3-beta1 and 4.5-stable > 4.5.1-rc1
        var typeComparison = Comparer<ReleaseType?>.Default.Compare(Type, other.Type);
        if (typeComparison != 0)
        {
            return typeComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        if (minorComparison != 0)
        {
            return minorComparison;
        }

        var patchComparison = Nullable.Compare(Patch, other.Patch);
        if (patchComparison != 0)
        {
            return patchComparison;
        }

        var runtimeEnvironmentComparison = RuntimeEnvironment.CompareTo(other.RuntimeEnvironment);
        if (runtimeEnvironmentComparison != 0)
        {
            return runtimeEnvironmentComparison;
        }

        return string.Compare(ReleaseNameWithRuntime, other.ReleaseNameWithRuntime, StringComparison.Ordinal);
    }


    /// <summary>
    ///     The primary way to create a Release. Instead of using ctors, we want to try parsing the version string and figure
    ///     it out contextually.
    /// </summary>
    /// <param name="versionString"></param>
    // TODO: Replace with Result<Release, ParseError> ParseRelease(string versionString)
    internal static Release? TryParse(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return null;
        }

        var parts = versionString.Split(['-', '.'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return null;
        }


        // patch is optional in Godot releases
        int? patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : null;

        if (major <= 0 || minor < 0 || patch < 0)
        {
            return null;
        }

        var releaseType = ReleaseType.TryParse(versionString);

        if (releaseType is null)
        {
            return null;
        }

        var runtime = parts.Contains("mono", StringComparer.InvariantCultureIgnoreCase)
            ? RuntimeEnvironment.Mono
            : RuntimeEnvironment.Standard;

        return new Release(major, minor, patch: patch, type: releaseType, runtimeEnvironment: runtime);
    }

    public static bool operator >=(Release left, Release right) =>
        left.CompareTo(right) >= 0;

    public static bool operator <=(Release left, Release right) =>
        left.CompareTo(right) <= 0;
}
