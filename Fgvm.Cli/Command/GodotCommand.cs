using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Diagnostics;
using System.Text;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class GodotCommand(
    IVersionManagementService versionManagementService,
    IGodotArgumentService argumentService,
    IProjectManager projectManager,
    IInstallationRegistry installationRegistry,
    IAnsiConsole console,
    ILogger<GodotCommand> logger)
{
    /// <summary>
    ///     Launch the currently selected Godot version.
    /// </summary>
    /// <param name="interactive">-i, Creates a prompt to select and launch an installed Godot version.</param>
    /// <param name="attached">-a, Launches Godot in attached mode, keeping it connected to the terminal for output.</param>
    /// <param name="args">Arguments to pass to the Godot executable (e.g., --args="--version --verbose").</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="InvalidOperationException">Thrown when version resolution or project-file lookup cannot continue.</exception>
    /// <exception cref="OperationCanceledException">Thrown when launch is canceled.</exception>
    [Command("godot|g")]
    public async Task Launch(bool interactive = false, bool attached = false, string args = "", CancellationToken cancellationToken = default)
    {
        var error = new StringBuilder();
        var process = new Process();
        Result<VersionResolutionOutcome, VersionResolutionError>? versionResult = null;

        // Register cancellation callback
        await using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                logger.ZLogWarning($"Failed to kill process during cancellation: {ex.Message}");
            }
        });

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
                        VersionResolutionError.NotFound notFound => Messages.VersionResolutionNotFound(notFound.Version, versionManagementService.HostSystem),
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

            var (execPath, workingDirectory, versionName) = resolutionOutcome switch
            {
                VersionResolutionOutcome.Found found => (found.ExecutablePath, found.WorkingDirectory, found.VersionName),
                _ => throw new InvalidOperationException("Expected Found outcome for successful resolution")
            };

            // Check if this is a help or version command that should output directly to console
            var argumentString = args;

            // Auto-detect project file and add it to arguments if we're in a project directory
            if (string.IsNullOrEmpty(argumentString))
            {
                switch (projectManager.FindProjectFilePath())
                {
                    case Result<ProjectLookup<string>, ProjectError>.Failure:
                        throw new InvalidOperationException("Unable to read project file information.");
                    case Result<ProjectLookup<string>, ProjectError>.Success
                    {
                        Value: ProjectLookup<string>.Found(var projectFilePath)
                    }:
                        // Godot expects the directory path, not the file path
                        var projectDirectory = Path.GetDirectoryName(projectFilePath);
                        argumentString = $"--editor --path \"{projectDirectory}\"";
                        console.MarkupLine(Messages.AutoDetectedProject(Path.GetFileName(projectFilePath)));
                        break;
                    case Result<ProjectLookup<string>, ProjectError>.Success:
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }
            }

            // Force attached mode for certain arguments that need terminal output
            var forceAttached = argumentService.ShouldForceAttachedMode(argumentString);
            var useAttachedMode = attached || forceAttached;

            if (!useAttachedMode)
            {
                // In detached mode (default), completely disconnect from terminal
                process.StartInfo = new ProcessStartInfo
                {
                    Arguments = argumentString,
                    FileName = execPath,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workingDirectory
                };

                process.Start();
                RecordLaunch(resolutionOutcome);

                // Close the streams to fully disconnect from terminal
                process.StandardInput.Close();

                console.MarkupLine(Messages.LaunchedGodotDetached(versionName, process.Id));
                return;
            }

            // For attached mode, redirect and handle output through Spectre.Console
            process.StartInfo = new ProcessStartInfo
            {
                Arguments = argumentString,
                FileName = execPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            if (forceAttached && !attached)
            {
                console.MarkupLine(Messages.RunningAttachedMode(versionName));
            }

            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } data)
                {
                    console.MarkupLine($"[green]{data.EscapeMarkup()}[/]");
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { } data)
                {
                    return;
                }

                console.MarkupLine(data.EscapeMarkup());
                error.Append(data + " ");
            };

            process.Start();
            RecordLaunch(resolutionOutcome);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(CancellationToken.None);
            process.CancelOutputRead();
            process.CancelErrorRead();
            var godotExitCode = process.ExitCode;

            // Check if cancellation was requested after process completed
            cancellationToken.ThrowIfCancellationRequested();

            if (godotExitCode != 0)
            {
                logger.ZLogError(
                    $"Godot exited with code {godotExitCode} and stderr: {error.ToString().EscapeMarkup()}");

                console.MarkupLine(Messages.SomethingWentWrong("when running Godot."));
                throw new ProcessExitCodeException(godotExitCode);
            }
        }
        catch (TaskCanceledException)
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
            var (execPath, workingDir) = versionResult switch
            {
                Result<VersionResolutionOutcome, VersionResolutionError>.Success { Value: VersionResolutionOutcome.Found found } => (found.ExecutablePath,
                    found.WorkingDirectory),
                _ => ("unknown", "unknown")
            };

            logger.ZLogError(
                $"Error running Godot at path {execPath} and working directory {workingDir} with the following error: {e.Message}");

            console.MarkupLine(
                Messages.SomethingWentWrong("when trying to launch Godot.")
            );

            throw;
        }
    }

    private void RecordLaunch(VersionResolutionOutcome outcome)
    {
        if (outcome is not VersionResolutionOutcome.Found { InstallationKey: { } installationKey })
        {
            return;
        }

        if (installationRegistry.RecordLaunch(installationKey) is Result<Unit, InstallationRegistryError>.Failure(var error))
        {
            logger.ZLogWarning($"Failed to record launch for {installationKey}: {error}");
        }
    }
}
