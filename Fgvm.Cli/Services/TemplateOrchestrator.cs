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
        bool verbose = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Installs export templates for an already-resolved release using a caller-owned progress operation. Writes
    ///     nothing to the console; the caller renders with <see cref="RenderResult" /> once the session has ended.
    /// </summary>
    /// <param name="release">The release whose export templates to install.</param>
    /// <param name="progress">The progress operation to drive.</param>
    /// <param name="force">Whether to replace existing export templates.</param>
    /// <param name="verbose">Whether to report each download source as it is tried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(Release release,
        IOperationProgress<TemplateInstallationStage> progress,
        bool force = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Renders the outcome of an export template installation. Call only outside a progress session.
    /// </summary>
    /// <param name="result">The installation result to render.</param>
    void RenderResult(Result<TemplateInstallationOutcome, TemplateInstallationError> result);

    Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError> List();

    Task<Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>> SelectForRemovalAsync(string[] query,
        CancellationToken cancellationToken = default
    );
}

public sealed class TemplateOrchestrator(
    IReleaseManager releaseManager,
    IInstallationRegistry installationRegistry,
    ITemplateRegistry templateRegistry,
    ITemplateInstallationService templateInstallationService,
    IProgressHandler progressHandler,
    IAnsiConsole console
) : ITemplateOrchestrator
{
    public async Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(string[] query,
        bool force = false,
        bool verbose = false,
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

        var result = await progressHandler.TrackProgressAsync(session =>
            InstallAsync(
                release,
                session.AddOperation<TemplateInstallationStage>("Templates"),
                force,
                verbose,
                cancellationToken));

        RenderResult(result);
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<TemplateInstallationOutcome, TemplateInstallationError>> InstallAsync(Release release,
        IOperationProgress<TemplateInstallationStage> progress,
        bool force = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    ) =>
        await progress.RunAsync(
            async () => await templateInstallationService.InstallAsync(
                release, progress, force, verbose, cancellationToken),
            result => result is Result<TemplateInstallationOutcome, TemplateInstallationError>.Success);

    /// <inheritdoc />
    public void RenderResult(Result<TemplateInstallationOutcome, TemplateInstallationError> result)
    {
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
    }

    public Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError> List() =>
        templateRegistry.ListInstallations();

    public async Task<Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>> SelectForRemovalAsync(string[] query,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<TemplateInstallation> installations;
        switch (templateRegistry.ListInstallations())
        {
            case Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(var installed):
                installations = installed;
                break;
            case Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(var error):
                return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(error);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        if (installations.Count == 0)
        {
            console.MarkupLine(Messages.NoTemplatesToRemove);
            return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]);
        }

        var installedReleaseNames = installations.Select(installation => installation.ReleaseNameWithRuntime).ToArray();
        var filtered = releaseManager.FilterReleasesByQueryWithoutPlatform(query, installedReleaseNames).ToArray();
        if (filtered.Length == 0)
        {
            console.MarkupLine(Messages.NoTemplatesMatchingQuery(string.Join(' ', query)));
            return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]);
        }

        IEnumerable<string> releasesToRemove;
        if (filtered.Length == 1)
        {
            releasesToRemove = filtered;
            console.MarkupLine(Messages.FoundExactTemplateMatch(filtered[0]));
        }
        else
        {
            if (!console.Profile.Capabilities.Interactive)
            {
                throw new ArgumentException(Messages.AmbiguousQueryInNonInteractiveShell(
                    "fgvm template remove", string.Join(' ', query), filtered));
            }

            releasesToRemove = await Remove.ShowVersionRemovalPrompt(filtered, console, cancellationToken);
        }

        var selected = new List<TemplateInstallation>();
        foreach (var releaseNameWithRuntime in releasesToRemove)
        {
            if (installations.FirstOrDefault(installation =>
                    string.Equals(installation.ReleaseNameWithRuntime, releaseNameWithRuntime, StringComparison.OrdinalIgnoreCase)) is
                not { } installation)
            {
                return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(
                    new TemplateRegistryError.NotFound(releaseNameWithRuntime));
            }

            selected.Add(installation);
        }

        return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(selected);
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
            if (!console.Profile.Capabilities.Interactive)
            {
                return new Result<Release, TemplateInstallationError>.Failure(
                    new TemplateInstallationError.InvalidQuery(
                        Messages.VersionQueryRequiredInNonInteractiveShell("fgvm template install")));
            }

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
