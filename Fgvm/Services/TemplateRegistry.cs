using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;

namespace Fgvm.Services;

public interface ITemplateRegistry
{
    Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError> ListInstallations();

    Result<TemplateInstallation, TemplateRegistryError> FindByReleaseName(string releaseNameWithRuntime);

    Result<Unit, TemplateRegistryError> Remove(string templateVersion);
}

public sealed class TemplateRegistry(
    IGodotPathService godotPathService,
    IHostSystem hostSystem
) : ITemplateRegistry
{
    public Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError> ListInstallations()
    {
        var rootPath = godotPathService.ExportTemplatesRootPath;
        switch (hostSystem.DirectoryExists(rootPath))
        {
            case Result<bool, FileOperationError>.Failure(var error):
                return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(
                    new TemplateRegistryError.ReadFailed(error));
            case Result<bool, FileOperationError>.Success { Value: false }:
                return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]);
            case Result<bool, FileOperationError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        IReadOnlyList<HostDirectoryEntry> directories;
        switch (hostSystem.EnumerateDirectories(rootPath))
        {
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var error):
                return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(
                    new TemplateRegistryError.ReadFailed(error));
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var entries):
                directories = entries;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        var installations = directories
            .Where(entry => !entry.Attributes.HasFlag(FileAttributes.Hidden) &&
                            !entry.Name.StartsWith(".", StringComparison.Ordinal))
            .Select(entry => TemplateInstallation.TryCreate(
                entry.Name,
                entry.FullName,
                GetDirectoryCreatedAt(entry.FullName)))
            .OfType<TemplateInstallation>()
            .OrderByDescending(installation => Release.TryParse(installation.ReleaseNameWithRuntime))
            .ThenBy(installation => installation.TemplateVersion, StringComparer.Ordinal)
            .ToArray();

        return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(installations);
    }

    public Result<TemplateInstallation, TemplateRegistryError> FindByReleaseName(string releaseNameWithRuntime)
    {
        return ListInstallations() switch
        {
            Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(var installations)
                when installations.FirstOrDefault(installation =>
                    string.Equals(installation.ReleaseNameWithRuntime, releaseNameWithRuntime, StringComparison.OrdinalIgnoreCase)) is
                    { } installation =>
                new Result<TemplateInstallation, TemplateRegistryError>.Success(installation),
            Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success =>
                new Result<TemplateInstallation, TemplateRegistryError>.Failure(
                    new TemplateRegistryError.NotFound(releaseNameWithRuntime)),
            Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(var error) =>
                new Result<TemplateInstallation, TemplateRegistryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    public Result<Unit, TemplateRegistryError> Remove(string templateVersion)
    {
        var path = godotPathService.GetExportTemplateVersionPath(templateVersion);
        return hostSystem.DeleteDirectoryIfExists(path, true) switch
        {
            Result<Unit, FileOperationError>.Success =>
                new Result<Unit, TemplateRegistryError>.Success(Unit.Value),
            Result<Unit, FileOperationError>.Failure(var error) =>
                new Result<Unit, TemplateRegistryError>.Failure(new TemplateRegistryError.RemoveFailed(error)),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private DateTimeOffset? GetDirectoryCreatedAt(string path) =>
        hostSystem.GetDirectoryCreatedAtUtc(path) is Result<DateTimeOffset, FileOperationError>.Success(var createdAt)
            ? createdAt
            : null;
}
