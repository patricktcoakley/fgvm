using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Godot.Export;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Fgvm.Cli.Command;

internal sealed class ExportCommand(
    IVersionManagementService versionManagementService,
    IExportPresetCatalog exportPresetCatalog,
    ITemplateOrchestrator templateOrchestrator,
    IExportRunner exportRunner,
    IHostSystem hostSystem,
    TextWriter standardOutput,
    IAnsiConsole console,
    ILogger<ExportCommand> logger
)
{
    private const string DefaultManifestPath = ".fgvm-export.json";

    /// <summary>
    ///     Export every configured preset in this project.
    /// </summary>
    /// <param name="output">
    ///     -o, Write every target beneath this root, ignoring each preset's configured export path.
    /// </param>
    /// <param name="archive">-a, Use Godot's native ZIP pack export instead of a playable release export.</param>
    /// <param name="json">-j, Emit the export result as JSON.</param>
    /// <param name="manifest">Write the export manifest to this path instead of .fgvm-export.json.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Version query arguments</param>
    /// <exception cref="ConfigurationException">Thrown when the project or export plan is invalid.</exception>
    /// <exception cref="ProcessExitCodeException">Thrown when preparation or an export process fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    [Command("export")]
    public async Task Export(string? output = null,
        bool archive = false,
        bool json = false,
        string? manifest = null,
        CancellationToken cancellationToken = default,
        [Argument] params string[] query
    )
    {
        var projectRoot = Directory.GetCurrentDirectory();

        try
        {
            RequireProject(projectRoot);
            var targets = PlanTargets(projectRoot, output, archive);
            var manifestPath = ResolveManifestPath(projectRoot, manifest ?? DefaultManifestPath, targets);
            InvalidateManifest(manifestPath);

            var release = await versionManagementService.SetLocalVersionAsync(
                query is [] ? null : query,
                cancellationToken: cancellationToken);
            await InstallTemplatesAsync(release, cancellationToken);
            var launchTarget = await ResolveLaunchTargetAsync(cancellationToken);

            var run = await exportRunner.RunAsync(
                projectRoot,
                launchTarget,
                targets,
                manifestPath,
                json ? null : preset => console.MarkupLine(Messages.ExportingTarget(preset)),
                cancellationToken);
            var payload = run.Manifest.ToJson();

            if (json)
            {
                standardOutput.WriteLine(payload);
                return;
            }

            foreach (var target in run.Manifest.Targets)
            {
                console.MarkupLine(Messages.ExportedTarget(target.Preset, target.Path));
            }

            console.MarkupLine(Messages.ExportManifestWritten(run.ManifestPath));
        }
        catch (ExportRunException exception)
        {
            console.MarkupLine(Messages.ExportPhaseFailed(exception.Phase, exception.Message));
            throw new ProcessExitCodeException(ExitCodes.GeneralError);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("User cancelled the export.");
            console.MarkupLine(Messages.UserCancelled("export"));
            throw;
        }
        catch (Exception exception) when (exception is not ConfigurationException
                                              and not ProcessExitCodeException
                                              and not ArgumentException)
        {
            logger.LogError(exception, "Error exporting the project: {Message}", exception.Message);
            console.MarkupLine(Messages.SomethingWentWrong("when trying to export the project"));
            throw;
        }
    }

    private void RequireProject(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "project.godot");
        if (!Inspect(hostSystem.FileExists(path), path))
        {
            throw new ConfigurationException(
                $"No Godot project was found in '{projectRoot}'. Run fgvm export from a directory containing project.godot.");
        }
    }

    private IReadOnlyList<PlannedExportTarget> PlanTargets(string projectRoot, string? output, bool archive)
    {
        var presets = ReadExportPresets();
        var outputRoot = output is null ? null : Path.GetFullPath(Path.Combine(projectRoot, output));

        return ExportPlanner.Plan(presets, projectRoot, hostSystem.SystemInfo.CurrentOS, outputRoot, archive) switch
        {
            Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Success(var targets) => targets,
            Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Failure(var error) =>
                throw new ConfigurationException(error.Message),
            _ => throw new InvalidOperationException("Unexpected export plan result type.")
        };
    }

    private IReadOnlyList<ExportPreset> ReadExportPresets() =>
        exportPresetCatalog.Read() switch
        {
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(var presets) => presets,
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                ExportPresetCatalogError.InvalidConfiguration(var path, var line, var message)) =>
                throw new ConfigurationException($"Invalid export preset configuration in '{path}' at line {line}: {message}"),
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                ExportPresetCatalogError.FileAccess(var error)) =>
                throw new ConfigurationException($"Unable to read export presets: {error}"),
            _ => throw new InvalidOperationException("Unexpected export preset catalog result type.")
        };

    private async Task InstallTemplatesAsync(Release release, CancellationToken cancellationToken)
    {
        Result<TemplateInstallationOutcome, TemplateInstallationError> result;
        try
        {
            result = await templateOrchestrator.InstallAsync(
                [release.ReleaseNameWithRuntime],
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Export templates for {Release} could not be installed: {Reason}",
                release.ReleaseNameWithRuntime,
                exception.Message);
            console.MarkupLine(Messages.LocalTemplateInstallationFailed(
                release.ReleaseNameWithRuntime,
                exception.Message));
            throw new ProcessExitCodeException(ExitCodes.GeneralError);
        }

        if (result is Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(var error))
        {
            var reason = TemplateInstallationResult.DescribeError(error);
            logger.LogError("Export templates for {Release} could not be installed: {Reason}",
                release.ReleaseNameWithRuntime,
                reason);
            console.MarkupLine(Messages.LocalTemplateInstallationFailed(release.ReleaseNameWithRuntime, reason));
            throw new ProcessExitCodeException(ExitCodes.GeneralError);
        }
    }

    private async Task<GodotLaunchTarget> ResolveLaunchTargetAsync(CancellationToken cancellationToken) =>
        await versionManagementService.ResolveEffectiveVersionAsync(cancellationToken) switch
        {
            Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(var found) =>
                GodotLaunchTarget.FromResolution(found),
            Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(VersionResolutionError.NotFound(var version)) =>
                throw new ConfigurationException($"The prepared Godot version '{version}' is not installed."),
            Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(VersionResolutionError.InvalidVersion(var version)) =>
                throw new ConfigurationException($"The prepared Godot version '{version}' is invalid."),
            Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(VersionResolutionError.Failed(var reason)) =>
                throw new ConfigurationException($"The prepared Godot version could not be resolved: {reason}"),
            _ => throw new InvalidOperationException("Unexpected Godot version resolution result type.")
        };

    private string ResolveManifestPath(string projectRoot,
        string manifestPath,
        IReadOnlyList<PlannedExportTarget> targets
    )
    {
        var resolved = Path.GetFullPath(Path.Combine(projectRoot, manifestPath));
        var comparison = hostSystem.SystemInfo.CurrentOS == OS.Windows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var target in targets)
        {
            if (PathsOverlap(resolved, target.ArtifactPath, comparison))
            {
                throw new ConfigurationException(
                    $"Manifest path '{resolved}' conflicts with export preset '{target.Preset}'. Choose another manifest path.");
            }
        }

        return resolved;
    }

    private void InvalidateManifest(string path)
    {
        if (hostSystem.DeleteFileIfExists(path) is Result<Unit, FileOperationError>.Failure(var error))
        {
            throw new ConfigurationException($"Could not invalidate the previous export manifest at '{path}': {error}");
        }
    }

    private static bool Inspect(Result<bool, FileOperationError> result, string path) =>
        result switch
        {
            Result<bool, FileOperationError>.Success(var exists) => exists,
            Result<bool, FileOperationError>.Failure(var error) =>
                throw new ConfigurationException($"Could not inspect '{path}': {error}"),
            _ => throw new InvalidOperationException("Unexpected filesystem inspection result type.")
        };

    private static bool PathsOverlap(string first, string second, StringComparison comparison) =>
        IsSameOrNested(first, second, comparison) || IsSameOrNested(second, first, comparison);

    private static bool IsSameOrNested(string parent, string candidate, StringComparison comparison) =>
        string.Equals(parent, candidate, comparison) ||
        candidate.StartsWith(parent, comparison) &&
        (Path.EndsInDirectorySeparator(parent) ||
         candidate[parent.Length] == Path.DirectorySeparatorChar ||
         candidate[parent.Length] == Path.AltDirectorySeparatorChar);
}
