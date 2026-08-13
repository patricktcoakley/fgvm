using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Error;
using Fgvm.Godot.Export;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Cli.Services;

internal interface IExportRunner
{
    Task<ExportRunResult> RunAsync(string projectRoot,
        GodotLaunchTarget launchTarget,
        IReadOnlyList<PlannedExportTarget> targets,
        string manifestPath,
        Action<string>? onTargetStarted,
        CancellationToken cancellationToken
    );
}

internal sealed record ExportRunResult(ExportManifestView Manifest, string ManifestPath);

internal sealed class ExportRunException(string phase, string reason) : Exception(reason)
{
    internal string Phase { get; } = phase;
}

internal sealed class ExportRunner(
    IGodotLauncher godotLauncher,
    IHostSystem hostSystem,
    IDirectoryRemoval directoryRemoval,
    ILogger<ExportRunner> logger
) : IExportRunner
{
    public async Task<ExportRunResult> RunAsync(string projectRoot,
        GodotLaunchTarget launchTarget,
        IReadOnlyList<PlannedExportTarget> targets,
        string manifestPath,
        Action<string>? onTargetStarted,
        CancellationToken cancellationToken
    )
    {
        SweepExportParents(targets, manifestPath);

        await RunGodotAsync(
            launchTarget,
            ["--headless", "--import", "--path", projectRoot],
            "Godot import",
            cancellationToken);

        var stagedTargets = new List<StagedExportTarget>(targets.Count + 1);
        try
        {
            // Godot processes share the project's import cache. Keep exports serial so they do not
            // race over .godot state or multiply the same import and filesystem work.
            foreach (var target in targets)
            {
                onTargetStarted?.Invoke(target.Preset);
                stagedTargets.Add(await StageTargetAsync(
                    projectRoot,
                    launchTarget,
                    target,
                    cancellationToken));
            }

            var manifest = BuildManifest(projectRoot, launchTarget.VersionName, targets);
            stagedTargets.Add(StageManifest(manifestPath, manifest.ToJson()));

            if (ExportCommitter.Commit(
                    hostSystem,
                    directoryRemoval,
                    stagedTargets.Select(target => target.Artifact).ToArray(),
                    logger) is Result<Unit, ExportCommitError>.Failure(var error))
            {
                logger.LogError("Could not commit export output {Path}: {Reason}", error.Path, error.Reason);
                throw new ExportRunException("Export commit", error.Reason);
            }

            return new ExportRunResult(manifest, Relative(projectRoot, manifestPath));
        }
        finally
        {
            directoryRemoval.Discard(stagedTargets.Select(target => target.StagingPath));
        }
    }

    private async Task<StagedExportTarget> StageTargetAsync(string projectRoot,
        GodotLaunchTarget launchTarget,
        PlannedExportTarget target,
        CancellationToken cancellationToken
    ) =>
        target.Kind switch
        {
            ExportArtifactKind.Directory =>
                await StageDirectoryAsync(projectRoot, launchTarget, target, cancellationToken),
            ExportArtifactKind.File =>
                await StageFileAsync(projectRoot, launchTarget, target, cancellationToken),
            _ => throw new InvalidOperationException("Unexpected export artifact kind.")
        };

    private async Task<StagedExportTarget> StageDirectoryAsync(string projectRoot,
        GodotLaunchTarget launchTarget,
        PlannedExportTarget target,
        CancellationToken cancellationToken
    )
    {
        var stagingPath = PrepareStagingDirectory(target.ArtifactPath);
        try
        {
            var stagesArtifactItself = target is { DestinationKind: ExportDestinationKind.Directory } &&
                                       target.Destination == target.ArtifactPath;
            var relativeDestination = stagesArtifactItself
                ? Path.GetFileName(target.Destination)
                : Path.GetRelativePath(target.ArtifactPath, target.Destination);
            var stagingDestination = Path.GetFullPath(Path.Combine(stagingPath, relativeDestination));
            var stagedArtifact = stagesArtifactItself ? stagingDestination : stagingPath;
            if (Path.GetDirectoryName(stagingDestination) is { } parent)
            {
                CreateDirectory(parent, "export staging");
            }

            await ExportToAsync(projectRoot, launchTarget, target, stagingDestination, cancellationToken);

            return new StagedExportTarget(
                stagingPath,
                new StagedExportArtifact(
                    stagedArtifact,
                    target.ArtifactPath,
                    ExportArtifactKind.Directory,
                    target.OwnsArtifactPath));
        }
        catch
        {
            directoryRemoval.Discard([stagingPath]);
            throw;
        }
    }

    private async Task<StagedExportTarget> StageFileAsync(string projectRoot,
        GodotLaunchTarget launchTarget,
        PlannedExportTarget target,
        CancellationToken cancellationToken
    )
    {
        var stagingPath = PrepareStagingDirectory(target.ArtifactPath);
        try
        {
            var stagingDestination = Path.Combine(stagingPath, Path.GetFileName(target.ArtifactPath));
            await ExportToAsync(projectRoot, launchTarget, target, stagingDestination, cancellationToken);

            return new StagedExportTarget(
                stagingPath,
                new StagedExportArtifact(
                    stagingDestination,
                    target.ArtifactPath,
                    ExportArtifactKind.File,
                    target.OwnsArtifactPath));
        }
        catch
        {
            directoryRemoval.Discard([stagingPath]);
            throw;
        }
    }

    private async Task ExportToAsync(string projectRoot,
        GodotLaunchTarget launchTarget,
        PlannedExportTarget target,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await RunGodotAsync(
            launchTarget,
            [
                "--headless",
                "--path",
                projectRoot,
                target.ExportKind is GodotExportKind.Pack ? "--export-pack" : "--export-release",
                target.Preset,
                destination
            ],
            $"Export of '{target.Preset}'",
            cancellationToken);
        RequireDestination(target, destination);
    }

    private async Task RunGodotAsync(GodotLaunchTarget launchTarget,
        IReadOnlyList<string> arguments,
        string phase,
        CancellationToken cancellationToken
    )
    {
        var diagnostics = new Queue<string>(5);
        var result = await godotLauncher.LaunchAsync(
            new GodotLaunchRequest(launchTarget, arguments, GodotLaunchMode.Attached),
            line =>
            {
                switch (line)
                {
                    case GodotLaunchOutput.StandardOutput(var text):
                        logger.LogDebug("godot: {Line}", text);
                        break;
                    case GodotLaunchOutput.StandardError(var text):
                        if (diagnostics.Count == 5)
                        {
                            diagnostics.Dequeue();
                        }

                        diagnostics.Enqueue(text);
                        logger.LogWarning("godot: {Line}", text);
                        break;
                }
            },
            cancellationToken);

        switch (result)
        {
            case Result<GodotLaunchOutcome, GodotLaunchError>.Success(GodotLaunchOutcome.Exited(0)):
                return;
            case Result<GodotLaunchOutcome, GodotLaunchError>.Success(GodotLaunchOutcome.Exited(var exitCode)):
                logger.LogError("{Phase} exited with {ExitCode}.", phase, exitCode);
                throw new ExportRunException(
                    phase,
                    $"Godot exited with code {exitCode}. {DescribeDiagnostics(diagnostics)}");
            case Result<GodotLaunchOutcome, GodotLaunchError>.Failure(var error):
                logger.LogError("{Phase} could not be launched: {Error}", phase, error);
                throw new ExportRunException(phase, DescribeLaunchError(error));
            default:
                throw new InvalidOperationException("Unexpected Godot launch result type.");
        }
    }

    private void RequireDestination(PlannedExportTarget target, string destination)
    {
        if (target.DestinationKind switch
            {
                ExportDestinationKind.File => FileExists(destination),
                ExportDestinationKind.Directory => DirectoryExists(destination),
                ExportDestinationKind.Either => FileExists(destination) || DirectoryExists(destination),
                _ => throw new InvalidOperationException("Unexpected export destination kind.")
            })
        {
            return;
        }

        throw new ExportRunException(
            $"Export of '{target.Preset}'",
            $"Godot exited successfully but did not create '{destination}'.");
    }

    private bool FileExists(string path) => InspectOutput(hostSystem.FileExists(path), path);

    private bool DirectoryExists(string path) => InspectOutput(hostSystem.DirectoryExists(path), path);

    private static bool InspectOutput(Result<bool, FileOperationError> result, string path) =>
        result switch
        {
            Result<bool, FileOperationError>.Success(var exists) => exists,
            Result<bool, FileOperationError>.Failure(var error) =>
                throw new ConfigurationException($"Could not inspect export output '{path}': {error}"),
            _ => throw new InvalidOperationException("Unexpected output inspection result type.")
        };

    private string PrepareStagingDirectory(string artifactPath)
    {
        var parent = Path.GetDirectoryName(artifactPath)
                     ?? throw new ConfigurationException($"Export path '{artifactPath}' has no parent directory.");
        CreateDirectory(parent, "export");
        var stagingPath = StagedDirectoryNames.CreateExportStagingPath(parent);
        CreateDirectory(stagingPath, "export staging");
        return stagingPath;
    }

    private void CreateDirectory(string path, string description)
    {
        if (hostSystem.CreateDirectory(path) is Result<Unit, FileOperationError>.Failure(var error))
        {
            throw new ConfigurationException($"Could not create the {description} directory '{path}': {error}");
        }
    }

    private void SweepExportParents(IReadOnlyList<PlannedExportTarget> targets, string manifestPath)
    {
        var comparer = hostSystem.SystemInfo.CurrentOS == OS.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var parents = targets
            .Select(target => Path.GetDirectoryName(target.ArtifactPath))
            .Append(Path.GetDirectoryName(manifestPath))
            .OfType<string>()
            .Distinct(comparer);
        directoryRemoval.Sweep(parents);
    }

    private StagedExportTarget StageManifest(string path, string payload)
    {
        var stagingPath = PrepareStagingDirectory(path);
        var stagedManifest = Path.Combine(stagingPath, Path.GetFileName(path));
        if (hostSystem.WriteAllText(stagedManifest, payload) is Result<Unit, FileOperationError>.Failure(var error))
        {
            directoryRemoval.Discard([stagingPath]);
            throw new ConfigurationException($"Could not write the manifest to '{path}': {error}");
        }

        return new StagedExportTarget(
            stagingPath,
            new StagedExportArtifact(stagedManifest, path, ExportArtifactKind.File, true));
    }

    private ExportManifestView BuildManifest(string projectRoot,
        string godotVersion,
        IReadOnlyList<PlannedExportTarget> targets
    ) =>
        new(
            ExportManifestView.CurrentVersion,
            Guid.NewGuid(),
            godotVersion,
            [
                .. targets.Select(target => new ExportTargetView(
                    target.Preset,
                    target.Platform,
                    target.ExportKind is GodotExportKind.Pack ? "pack" : "release",
                    target.Kind == ExportArtifactKind.File ? "file" : "directory",
                    Relative(projectRoot, target.ArtifactPath)))
            ]);

    private static string Relative(string projectRoot, string path)
    {
        var relative = Path.GetRelativePath(projectRoot, path);
        var outsideProject = Path.IsPathRooted(relative) ||
                             relative is ".." ||
                             relative is ['.', '.', var separator, ..] &&
                             (separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar);

        return outsideProject
            ? path
            : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string DescribeDiagnostics(Queue<string> diagnostics) =>
        diagnostics.Count is 0 ? "Godot reported no diagnostics." : string.Join(" ", diagnostics);

    private static string DescribeLaunchError(GodotLaunchError error) => error switch
    {
        GodotLaunchError.StartFailed(var path, var reason) => $"Could not start '{path}': {reason}",
        GodotLaunchError.ProcessFailed(var path, var reason) => $"'{path}' failed: {reason}",
        _ => "Godot could not be launched."
    };

    private sealed record StagedExportTarget(string StagingPath, StagedExportArtifact Artifact);
}
