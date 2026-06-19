using System.Text;
using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class GodotCommand(
    IVersionManagementService versionManagementService,
    IGodotArgumentService argumentService,
    IGodotLauncher godotLauncher,
    IProjectManager projectManager,
    IInstallationRegistry installationRegistry,
    IAnsiConsole console,
    ILogger<GodotCommand> logger
)
{
    /// <summary>
    ///     Launch the currently selected Godot version.
    /// </summary>
    /// <param name="interactive">-i, Creates a prompt to select and launch an installed Godot version.</param>
    /// <param name="attached">-a, Launches Godot in attached mode, keeping it connected to the terminal for output.</param>
    /// <param name="project">-P, Adds the detected project path to explicit Godot arguments.</param>
    /// <param name="args">Arguments to pass to the Godot executable (e.g., --args "--version --verbose").</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="InvalidOperationException">Thrown when version resolution or project-file lookup cannot continue.</exception>
    /// <exception cref="OperationCanceledException">Thrown when launch is canceled.</exception>
    [Command("godot|g")]
    public async Task Launch(bool interactive = false,
        bool attached = false,
        bool project = false,
        string args = "",
        CancellationToken cancellationToken = default
    )
    {
        var error = new StringBuilder();
        Result<VersionResolutionOutcome, VersionResolutionError>? versionResult = null;
        GodotLaunchTarget? launchTarget = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Use the version management service to resolve the appropriate version (explicit .fgvm-version only)
            versionResult = await versionManagementService.ResolveVersionForLaunchExplicitAsync(interactive, cancellationToken);

            // Handle interactive selection if required
            if (versionResult is Result<VersionResolutionOutcome, VersionResolutionError>.Success
                {
                    Value: VersionResolutionOutcome.InteractiveRequired interactiveRequired
                })
            {
                var installed = interactiveRequired.AvailableVersions;
                if (installed.Count == 0)
                {
                    console.MarkupLine(Messages.NoVersionsInstalled);
                    return;
                }

                var selection = await Prompts.Godot.ShowGodotSelectionPrompt(installed, console, cancellationToken);
                versionResult = versionManagementService.ResolveInteractiveVersion(selection);
            }

            VersionResolutionOutcome resolutionOutcome;
            switch (versionResult)
            {
                case Result<VersionResolutionOutcome, VersionResolutionError>.Failure(var resolutionError):
                    var errorMessage = resolutionError switch
                    {
                        VersionResolutionError.NotFound notFound => Messages.VersionResolutionNotFound(notFound.Version,
                            versionManagementService.HostSystem),
                        VersionResolutionError.Failed failed => Messages.VersionResolutionFailed(failed.Reason),
                        VersionResolutionError.InvalidVersion invalid => Messages.InvalidVersion(invalid.Version),
                        _ => Messages.UnknownResolutionError
                    };

                    console.MarkupLine(errorMessage);
                    throw new InvalidOperationException(errorMessage);
                case Result<VersionResolutionOutcome, VersionResolutionError>.Success(var outcome):
                    resolutionOutcome = outcome;
                    break;
                default:
                    console.MarkupLine(Messages.UnexpectedError);
                    return;
            }

            var found = resolutionOutcome switch
            {
                VersionResolutionOutcome.Found value => value,
                _ => throw new InvalidOperationException("Expected Found outcome for successful resolution")
            };
            launchTarget = GodotLaunchTarget.FromResolution(found);

            // Check if this is a help or version command that should output directly to console
            var argumentString = args;

            if (project || string.IsNullOrEmpty(argumentString))
            {
                argumentString = AddDetectedProjectArguments(argumentString, project);
            }

            // Force attached mode for certain arguments that need terminal output
            var forceAttached = argumentService.ShouldForceAttachedMode(argumentString);
            var useAttachedMode = attached || forceAttached;

            if (forceAttached && !attached)
            {
                console.MarkupLine(Messages.RunningAttachedMode(launchTarget.VersionName));
            }

            var request = new GodotLaunchRequest(
                launchTarget,
                argumentString,
                useAttachedMode ? GodotLaunchMode.Attached : GodotLaunchMode.Detached);

            var launchResult = await godotLauncher.LaunchAsync(
                request,
                output =>
                {
                    switch (output)
                    {
                        case GodotLaunchOutput.StandardOutput(var line):
                            console.MarkupLine($"[green]{line.EscapeMarkup()}[/]");
                            break;
                        case GodotLaunchOutput.StandardError(var line):
                            console.MarkupLine(line.EscapeMarkup());
                            error.Append(line + " ");
                            break;
                    }
                },
                cancellationToken);

            switch (launchResult)
            {
                case Result<GodotLaunchOutcome, GodotLaunchError>.Failure(var launchError):
                    throw new InvalidOperationException(DescribeLaunchError(launchError));
                case Result<GodotLaunchOutcome, GodotLaunchError>.Success(GodotLaunchOutcome.Detached(var processId)):
                    RecordLaunch(launchTarget);
                    console.MarkupLine(Messages.LaunchedGodotDetached(launchTarget.VersionName, processId));
                    return;
                case Result<GodotLaunchOutcome, GodotLaunchError>.Success(GodotLaunchOutcome.Exited(var exitCode)):
                    RecordLaunch(launchTarget);
                    if (exitCode != 0)
                    {
                        logger.ZLogError(
                            $"Godot exited with code {exitCode} and stderr: {error.ToString().EscapeMarkup()}");

                        console.MarkupLine(Messages.SomethingWentWrong("when running Godot."));
                        throw new ProcessExitCodeException(exitCode);
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unexpected Godot launch result.");
            }
        }
        catch (OperationCanceledException)
        {
            logger.ZLogError($"User cancelled running Godot.");
            console.MarkupLine(Messages.UserCancelled("godot"));

            throw;
        }
        catch (ProcessExitCodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            var execPath = launchTarget?.ExecutablePath ?? "unknown";
            var workingDir = launchTarget?.WorkingDirectory ?? "unknown";

            logger.ZLogError(
                $"Error running Godot at path {execPath} and working directory {workingDir} with the following error: {e.Message}");

            console.MarkupLine(
                Messages.SomethingWentWrong("when trying to launch Godot.")
            );

            throw;
        }
    }

    private static string DescribeLaunchError(GodotLaunchError error) => error switch
    {
        GodotLaunchError.StartFailed(var executablePath, var reason) =>
            $"Unable to start Godot at `{executablePath}`: {reason}",
        GodotLaunchError.ProcessFailed(var executablePath, var reason) =>
            $"Godot process at `{executablePath}` failed: {reason}",
        _ => "Unable to launch Godot."
    };

    private string AddDetectedProjectArguments(string argumentString, bool required)
    {
        switch (projectManager.FindProjectFilePath())
        {
            case Result<ProjectLookup<string>, ProjectError>.Failure:
                throw new InvalidOperationException("Unable to read project file information.");
            case Result<ProjectLookup<string>, ProjectError>.Success
            {
                Value: ProjectLookup<string>.Found(var projectFilePath)
            }:
                // Godot expects the directory path, not the file path.
                var projectDirectory = Path.GetDirectoryName(projectFilePath)
                                       ?? throw new InvalidOperationException("Unable to determine project directory.");
                console.MarkupLine(Messages.AutoDetectedProject(Path.GetFileName(projectFilePath)));
                return string.IsNullOrEmpty(argumentString)
                    ? $"--editor --path \"{projectDirectory}\""
                    : $"--path \"{projectDirectory}\" {argumentString}";
            case Result<ProjectLookup<string>, ProjectError>.Success when required:
                throw new InvalidOperationException("No project.godot file found in the current directory.");
            case Result<ProjectLookup<string>, ProjectError>.Success:
                return argumentString;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    private void RecordLaunch(GodotLaunchTarget target)
    {
        if (installationRegistry.RecordLaunch(target.InstallationKey) is Result<Unit, InstallationRegistryError>.Failure(var error))
        {
            logger.ZLogWarning($"Failed to record launch for {target.InstallationKey}: {error}");
        }
    }
}
