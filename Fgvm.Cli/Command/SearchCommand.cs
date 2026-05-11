using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text.Json.Serialization;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class SearchCommand(
    IReleaseManager releaseManager,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<SearchCommand> logger)
{
    /// <summary>
    ///     Search available Godot versions.
    /// </summary>
    /// <param name="json">-j, Output results as JSON.</param>
    /// <param name="noCache">-F, Force a remote refresh instead of reading releases.json.</param>
    /// <param name="query">Optional query arguments.</param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="InvalidOperationException">Thrown when remote release lookup fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when search is canceled.</exception>
    [Command("search|s")]
    public async Task Search(bool json = false, bool noCache = false, [Argument] string[]? query = null, CancellationToken cancellationToken = default)
    {
        var searchQuery = query ?? [];
        var fetchMode = noCache ? ReleaseFetchMode.ForceRemote : ReleaseFetchMode.UseCache;
        var result = await releaseManager.SearchRemoteReleases(searchQuery, fetchMode, cancellationToken);

        switch (result)
        {
            case Result<IEnumerable<string>, NetworkError>.Success(var releaseNames):
                var releases = releaseNames
                    .Select(name => new RemoteReleaseView(name))
                    .ToList();

                if (json)
                {
                    console.WriteLine(releases.ToJson());
                }
                else
                {
                    console.Write(releases.ToPanel(searchQuery));
                }

                return;

            case Result<IEnumerable<string>, NetworkError>.Failure(var error):
                var errorMessage = error switch
                {
                    NetworkError.RequestFailure(var url, var statusCode, _) =>
                        $"Request to {url} failed with status code {statusCode}",
                    NetworkError.ConnectionFailure(var message, _) =>
                        $"Network error: {message}",
                    _ => "Unknown network error"
                };

                logger.ZLogError($"Error searching releases: {errorMessage}");
                console.MarkupLine(
                    Messages.SomethingWentWrong("when trying to search releases", pathService)
                );

                throw new InvalidOperationException(errorMessage);
        }
    }
}

internal readonly record struct RemoteReleaseView([property: JsonPropertyName("name")] string Name) : IJsonView<RemoteReleaseView>;

internal static class RemoteReleaseViewExtensions
{
    extension(IReadOnlyList<RemoteReleaseView> releases)
    {
        public Panel ToPanel(IReadOnlyList<string> query)
        {
            var content = releases.Count > 0
                ? string.Join("\n", releases.Select(r => r.Name))
                : "[dim]No releases found.[/]";

            var header = query.Count == 0
                ? Messages.AvailableVersionsHeader
                : "[green]List Of Available Versions[/]";

            return new Panel(content)
            {
                Header = new PanelHeader(header),
                Width = 40
            };
        }
    }
}
