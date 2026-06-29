using Fgvm.Godot;

namespace Fgvm.Types;

public sealed record TemplateInstallation(
    string TemplateVersion,
    string ReleaseNameWithRuntime,
    RuntimeEnvironment RuntimeEnvironment,
    string Path,
    DateTimeOffset? InstalledAt
)
{
    public static string ToTemplateVersion(Release release)
    {
        var version = $"{release.Version}.{release.Type}";
        return release.RuntimeEnvironment == RuntimeEnvironment.Mono
            ? $"{version}.mono"
            : version;
    }

    public static TemplateInstallation? TryCreate(string templateVersion, string path, DateTimeOffset? installedAt = null)
    {
        if (string.IsNullOrWhiteSpace(templateVersion))
        {
            return null;
        }

        var parts = templateVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var major) || major <= 0 ||
            !int.TryParse(parts[1], out var minor) || minor < 0)
        {
            return null;
        }

        var statusIndex = 2;
        string version;
        if (statusIndex < parts.Length && int.TryParse(parts[statusIndex], out var patch))
        {
            if (patch < 0)
            {
                return null;
            }

            version = $"{major}.{minor}.{patch}";
            statusIndex++;
        }
        else
        {
            version = $"{major}.{minor}";
        }

        if (statusIndex >= parts.Length)
        {
            return null;
        }

        var status = parts[statusIndex];
        var hasMonoSuffix = parts.Length == statusIndex + 2 &&
                            parts[^1].Equals("mono", StringComparison.OrdinalIgnoreCase);
        if (parts.Length > statusIndex + (hasMonoSuffix ? 2 : 1))
        {
            return null;
        }

        var runtime = hasMonoSuffix ? RuntimeEnvironment.Mono : RuntimeEnvironment.Standard;
        var releaseNameWithRuntime = $"{version}-{status}-{runtime.Name()}";

        return Release.TryParse(releaseNameWithRuntime) is null
            ? null
            : new TemplateInstallation(templateVersion, releaseNameWithRuntime, runtime, path, installedAt);
    }
}

public abstract record TemplateRegistryError
{
    public sealed record ReadFailed(FileOperationError Error) : TemplateRegistryError;

    public sealed record RemoveFailed(FileOperationError Error) : TemplateRegistryError;

    public sealed record NotFound(string TemplateVersion) : TemplateRegistryError;
}

public abstract record TemplateInstallationOutcome
{
    public sealed record NewInstallation(string TemplateVersion, string Path, ChecksumVerification ChecksumStatus)
        : TemplateInstallationOutcome;

    public sealed record AlreadyInstalled(string TemplateVersion, string Path) : TemplateInstallationOutcome;
}

public abstract record TemplateInstallationError
{
    public sealed record InvalidQuery(string Message) : TemplateInstallationError;

    public sealed record NotFound(string Version) : TemplateInstallationError;

    public sealed record Failed(string Reason) : TemplateInstallationError;

    public sealed record ChecksumMismatch(string Expected, string Actual, string FileName) : TemplateInstallationError;
}
