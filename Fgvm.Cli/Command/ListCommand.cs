using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text.Json.Serialization;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class ListCommand(IHostSystem hostSystem, IPathService pathService, IAnsiConsole console, ILogger<ListCommand> logger)
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
            var symlinkResult = hostSystem.ResolveCurrentSymlinks();
            var symlinkTarget = symlinkResult switch
            {
                Result<SymlinkInfo, SymlinkError>.Success(var info) => info.SymlinkPath,
                _ => string.Empty
            };

            var installationNames = hostSystem.ListInstallations() switch
            {
                Result<IReadOnlyList<string>, FileSystemError>.Success(var names) => names,
                Result<IReadOnlyList<string>, FileSystemError>.Failure =>
                    throw new InvalidOperationException("Unable to read installed Godot versions."),
                _ => throw new InvalidOperationException("Unexpected Result type")
            };

            var installations = installationNames
                .Select(name => ListView.Create(name, symlinkTarget, pathService.RootPath))
                .ToList();

            // Always render JSON if the flag is set
            if (json)
            {
                console.WriteLine(installations.ToJson());
                return;
            }

            if (installations.Count == 0)
            {
                console.MarkupLine(Messages.NoInstallationsFound);
                return;
            }

            console.Write(installations.ToPanel());
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
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isDefault")]
    bool IsDefault) : IJsonView<ListView>
{
    private string ToDisplayString() => IsDefault
        ? $"{Messages.DefaultInstallationMarkerMarkup}  {Name}"
        : $"{Messages.NonDefaultInstallationIndent}{Name}";

    public static ListView Create(string name, string symlinkTarget, string rootPath)
    {
        var isDefault = IsDefaultInstallation(name, symlinkTarget, rootPath);
        return new ListView(name, isDefault);
    }

    public string ToDisplay() => ToDisplayString();

    private static bool IsDefaultInstallation(string name, string symlinkTarget, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(symlinkTarget) ||
            string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(rootPath);
            var target = Path.GetFullPath(symlinkTarget);

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = Path.GetRelativePath(root, target);
            var firstSegment = relative
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            return !string.IsNullOrEmpty(firstSegment) &&
                   string.Equals(firstSegment, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

internal static class ListViewExtensions
{
    extension(IReadOnlyList<ListView> views)
    {
        public Panel ToPanel()
        {
            var content = string.Join("\n", views.Select(view => view.ToDisplay()));

            return new Panel(content)
            {
                Header = new PanelHeader(Messages.ListPanelHeader),
                Width = 40,
                Border = BoxBorder.Rounded
            };
        }
    }
}
