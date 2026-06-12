using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class ListCommand(
    IInstallationRegistry installationRegistry,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<ListCommand> logger
)
{
    /// <summary>
    ///     List all installed Godot versions.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when installed versions cannot be read.</exception>
    [Command("list|l")]
    public void List(bool json = false)
    {
        try
        {
            var defaultInstallation = installationRegistry.GetDefault() switch
            {
                Result<Installation, InstallationRegistryError>.Success(var installation) => installation.Key,
                _ => string.Empty
            };

            var installationRecords = installationRegistry.ListInstallations() switch
            {
                Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(var records) => records,
                Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure =>
                    throw new InvalidOperationException("Unable to read installed Godot versions."),
                _ => throw new InvalidOperationException("Unexpected Result type")
            };

            var installations = installationRecords
                .Select(installation => ListView.Create(installation, defaultInstallation))
                .ToList();

            // Always render JSON if the flag is set
            if (json)
            {
                console.Profile.Out.Writer.WriteLine(installations.ToJson());
                return;
            }

            if (installations.Count == 0)
            {
                console.MarkupLine(Messages.NoInstallationsFound);
                return;
            }

            console.MarkupLine(Messages.ListPanelHeader);
            console.Write(installations.ToColumns());
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error listing installations: {e.Message}");
            console.MarkupLine(
                Messages.SomethingWentWrong("when trying to list installations", pathService)
            );

            throw;
        }
    }
}

internal readonly record struct ListView(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("isDefault")]
    bool IsDefault
) : IJsonView<ListView>
{
    private string ToDisplayString() => IsDefault
        ? $"{Messages.DefaultInstallationMarkerMarkup}  {Name}"
        : $"{Messages.NonDefaultInstallationIndent}{Name}";

    public static ListView Create(Installation installation, string defaultInstallation) =>
        new(installation.ReleaseNameWithRuntime, string.Equals(installation.Key, defaultInstallation, StringComparison.Ordinal));

    public string ToDisplay() => ToDisplayString();
}

internal static class ListViewExtensions
{
    extension(IReadOnlyList<ListView> views)
    {
        public Columns ToColumns()
        {
            return new Columns(views.Select(v => new Markup(v.ToDisplay())));
        }
    }
}
