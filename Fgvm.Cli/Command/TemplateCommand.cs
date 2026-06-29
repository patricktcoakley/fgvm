using System.Security;
using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Fgvm.Cli.Command;

public sealed class TemplateCommand(
    ITemplateOrchestrator templateOrchestrator,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<TemplateCommand> logger
)
{
    /// <summary>
    ///     Install Godot export templates.
    /// </summary>
    /// <param name="force">Replace existing export templates for the selected version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="query">Version query arguments.</param>
    [Hidden]
    [Command("install")]
    public async Task Install(bool force = false, CancellationToken cancellationToken = default, [Argument] params string[] query)
    {
        try
        {
            switch (await templateOrchestrator.InstallAsync(query, force, cancellationToken))
            {
                case Result<TemplateInstallationOutcome, TemplateInstallationError>.Success:
                    return;
                case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.InvalidQuery invalid):
                    throw new ArgumentException(invalid.Message);
                case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.NotFound notFound):
                    throw new ArgumentException(Messages.TemplateInstallationNotFound(notFound.Version));
                case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.ChecksumMismatch mismatch):
                    throw new SecurityException(Messages.ChecksumMismatch(mismatch.FileName, mismatch.Expected, mismatch.Actual));
                case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.Failed failed):
                    throw new InvalidOperationException(Messages.TemplateInstallationFailed(failed.Reason));
                default:
                    throw new InvalidOperationException("Unknown template installation result type.");
            }
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled template installation.");
            console.MarkupLine(Messages.UserCancelled("template installation"));
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error installing export templates: {Message}", ex.Message);
            console.MarkupLine(Messages.SomethingWentWrong("when trying to install export templates", pathService));
            throw;
        }
    }

    /// <summary>
    ///     Install Godot export templates.
    /// </summary>
    /// <param name="force">Replace existing export templates for the selected version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="query">Version query arguments.</param>
    [Hidden]
    [Command("i")]
    public async Task InstallAlias(bool force = false, CancellationToken cancellationToken = default, [Argument] params string[] query) =>
        await Install(force, cancellationToken, query);

    /// <summary>
    ///     List installed Godot export templates.
    /// </summary>
    /// <param name="json">-j, Output results as JSON.</param>
    [Hidden]
    [Command("list")]
    public void List(bool json = false)
    {
        try
        {
            IReadOnlyList<TemplateInstallation> installations;
            switch (templateOrchestrator.List())
            {
                case Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(var result):
                    installations = result;
                    break;
                case Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure:
                    throw new InvalidOperationException("Unable to read installed export templates.");
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            var view = installations.Select(TemplateListView.Create).ToList();
            if (json)
            {
                console.Profile.Out.Writer.WriteLine(view.ToJson());
                return;
            }

            if (view.Count == 0)
            {
                console.MarkupLine(Messages.NoTemplatesInstalled);
                return;
            }

            foreach (var template in view)
            {
                console.MarkupLine(template.ToDisplay());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing export templates: {Message}", ex.Message);
            console.MarkupLine(Messages.SomethingWentWrong("when trying to list export templates", pathService));
            throw;
        }
    }

    /// <summary>
    ///     List installed Godot export templates.
    /// </summary>
    /// <param name="json">-j, Output results as JSON.</param>
    [Hidden]
    [Command("l")]
    public void ListAlias(bool json = false) => List(json);

    /// <summary>
    ///     Remove installed Godot export templates.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="query">Version query arguments.</param>
    [Hidden]
    [Command("remove")]
    public async Task Remove(CancellationToken cancellationToken = default, [Argument] params string[] query)
    {
        try
        {
            switch (await templateOrchestrator.RemoveAsync(query, cancellationToken))
            {
                case Result<Unit, TemplateRegistryError>.Success:
                    return;
                case Result<Unit, TemplateRegistryError>.Failure(var error):
                    throw new InvalidOperationException($"Unable to remove export templates: {error}");
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled template removal.");
            console.MarkupLine(Messages.UserCancelled("template removal"));
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing export templates: {Message}", ex.Message);
            console.MarkupLine(Messages.SomethingWentWrong("when trying to remove export templates", pathService));
            throw;
        }
    }

    /// <summary>
    ///     Remove installed Godot export templates.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="query">Version query arguments.</param>
    [Hidden]
    [Command("r")]
    public async Task RemoveAlias(CancellationToken cancellationToken = default, [Argument] params string[] query) =>
        await Remove(cancellationToken, query);
}

internal readonly record struct TemplateListView(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("release")]
    string Release,
    [property: JsonPropertyName("runtime")]
    string Runtime,
    [property: JsonPropertyName("path")]
    string Path
) : IJsonView<TemplateListView>
{
    public string ToDisplay() => $"{Name}  {Path}";

    public static TemplateListView Create(TemplateInstallation installation) =>
        new(
            installation.TemplateVersion,
            installation.ReleaseNameWithRuntime,
            installation.RuntimeEnvironment.Name(),
            installation.Path);
}
