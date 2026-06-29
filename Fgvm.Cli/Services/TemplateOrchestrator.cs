using Fgvm.Cli.Error;
using Fgvm.Cli.Prompts;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Spectre.Console;

namespace Fgvm.Cli.Services;

public interface ITemplateOrchestrator
{
    Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(string[] query,
        bool force = false,
        CancellationToken cancellationToken = default
    );

    Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError> List();

    Task<Result<Unit, TemplateRegistryError>> RemoveAsync(string[] query, CancellationToken cancellationToken = default);
}

public sealed class TemplateOrchestrator(
    IReleaseManager releaseManager,
    IInstallationRegistry installationRegistry,
    ITemplateRegistry templateRegistry,
    ITemplateInstallationService templateInstallationService,
    IProgressHandler<TemplateInstallationStage> progressHandler,
    IAnsiConsole console
) : ITemplateOrchestrator
{
    public async Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(string[] query,
        bool force = false,
        CancellationToken cancellationToken = default
    )
    {
        Release release;
        switch (await ResolveTemplateRelease(query, cancellationToken))
        {
            case Result<Release, TemplateInstallationError>.Success(var resolvedRelease):
                release = resolvedRelease;
                break;
            case Result<Release, TemplateInstallationError>.Failure(var error):
                return new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(error);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        var result = await progressHandler.TrackProgressAsync(progress =>
            templateInstallationService.InstallAsync(release, progress, force, cancellationToken));

        switch (result)
        {
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                TemplateInstallationOutcome.NewInstallation(var templateVersion, var path, var checksumStatus)):
                console.MarkupLine(Messages.TemplateInstallationSuccess(templateVersion, path));
                if (checksumStatus is ChecksumVerification.Unavailable)
                {
                    console.MarkupLine(Messages.TemplateChecksumUnavailable(templateVersion));
                }

                break;
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                TemplateInstallationOutcome.AlreadyInstalled(var templateVersion, var path)):
                console.MarkupLine(Messages.TemplateAlreadyInstalled(templateVersion, path));
                break;
        }

        return result;
    }

    public Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError> List() =>
        templateRegistry.ListInstallations();

    public async Task<Result<Unit, TemplateRegistryError>> RemoveAsync(string[] query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TemplateInstallation> installations;
        switch (templateRegistry.ListInstallations())
        {
            case Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(var installed):
                installations = installed;
                break;
            case Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(var error):
                return new Result<Unit, TemplateRegistryError>.Failure(error);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        if (installations.Count == 0)
        {
            console.MarkupLine(Messages.NoTemplatesToRemove);
            return new Result<Unit, TemplateRegistryError>.Success(Unit.Value);
        }

        var installedReleaseNames = installations.Select(installation => installation.ReleaseNameWithRuntime).ToArray();
        var filtered = releaseManager.FilterReleasesByQueryWithoutPlatform(query, installedReleaseNames).ToArray();
        if (filtered.Length == 0)
        {
            console.MarkupLine(Messages.NoTemplatesMatchingQuery(string.Join(' ', query)));
            return new Result<Unit, TemplateRegistryError>.Success(Unit.Value);
        }

        IEnumerable<string> releasesToRemove;
        if (filtered.Length == 1)
        {
            releasesToRemove = filtered;
            console.MarkupLine(Messages.FoundExactTemplateMatch(filtered[0]));
        }
        else
        {
            releasesToRemove = await Remove.ShowVersionRemovalPrompt(filtered, console, cancellationToken);
        }

        foreach (var releaseNameWithRuntime in releasesToRemove)
        {
            if (installations.FirstOrDefault(installation =>
                    string.Equals(installation.ReleaseNameWithRuntime, releaseNameWithRuntime, StringComparison.OrdinalIgnoreCase)) is
                not { } installation)
            {
                return new Result<Unit, TemplateRegistryError>.Failure(
                    new TemplateRegistryError.NotFound(releaseNameWithRuntime));
            }

            switch (templateRegistry.Remove(installation.TemplateVersion))
            {
                case Result<Unit, TemplateRegistryError>.Success:
                    console.MarkupLine(Messages.TemplateSuccessfullyRemoved(installation.TemplateVersion, installation.Path));
                    break;
                case Result<Unit, TemplateRegistryError>.Failure(var error):
                    return new Result<Unit, TemplateRegistryError>.Failure(error);
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }

        return new Result<Unit, TemplateRegistryError>.Success(Unit.Value);
    }

    private async Task<Result<Release, TemplateInstallationError>> ResolveTemplateRelease(string[] query,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<Installation> localInstallations;
        switch (installationRegistry.ListInstallations())
        {
            case Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(var installations):
                localInstallations = installations;
                break;
            case Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure(var error):
                return new Result<Release, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.Failed($"Unable to read installed Godot versions: {error}"));
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        var installedReleaseNames = localInstallations
            .Select(installation => installation.ReleaseNameWithRuntime)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (installedReleaseNames.Length == 0)
        {
            return new Result<Release, TemplateInstallationError>.Failure(
                new TemplateInstallationError.Failed(
                    "No installed Godot versions found. Install a Godot version first with `fgvm install <version>`."));
        }

        if (query.Length == 0)
        {
            var selectedRelease = await Install.CreateVersionSelectionPrompt(installedReleaseNames)
                .ShowAsync(console, cancellationToken);
            return CreateTemplateRelease(selectedRelease);
        }

        var filtered = releaseManager.FilterReleasesByQueryWithoutPlatform(query, installedReleaseNames).ToArray();
        if (filtered.Length == 0)
        {
            return new Result<Release, TemplateInstallationError>.Failure(
                new TemplateInstallationError.NotFound(string.Join(' ', query)));
        }

        return CreateTemplateRelease(SelectBestLocalTemplateMatch(filtered, query));
    }

    private Result<Release, TemplateInstallationError> CreateTemplateRelease(string releaseNameWithRuntime) =>
        releaseManager.CreateReleaseWithoutPlatform(releaseNameWithRuntime) switch
        {
            Result<Release, ReleaseParseError>.Success(var release) =>
                new Result<Release, TemplateInstallationError>.Success(release),
            Result<Release, ReleaseParseError>.Failure =>
                new Result<Release, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.InvalidQuery($"Invalid Godot version: {releaseNameWithRuntime}")),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private static string SelectBestLocalTemplateMatch(string[] filteredReleaseNames, string[] query)
    {
        if (HasRuntimeFilter(query))
        {
            return filteredReleaseNames[0];
        }

        return filteredReleaseNames.FirstOrDefault(name =>
                   name.Split('-', StringSplitOptions.RemoveEmptyEntries)
                       .Contains(RuntimeEnvironment.Standard.Name(), StringComparer.OrdinalIgnoreCase)) ??
               filteredReleaseNames[0];
    }

    private static bool HasRuntimeFilter(string[] query) =>
        query.SelectMany(part => part.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .Any(part => part.Equals(RuntimeEnvironment.Mono.Name(), StringComparison.OrdinalIgnoreCase) ||
                         part.Equals(RuntimeEnvironment.Standard.Name(), StringComparison.OrdinalIgnoreCase));
}
