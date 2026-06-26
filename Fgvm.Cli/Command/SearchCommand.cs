using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;


namespace Fgvm.Cli.Command;

public sealed class SearchCommand(
    IReleaseManager releaseManager,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<SearchCommand> logger
)
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
    public async Task Search(bool json = false,
        bool noCache = false,
        [Argument] string[]? query = null,
        CancellationToken cancellationToken = default
    )
    {
        var searchQuery = query ?? [];
        var fetchMode = noCache ? ReleaseFetchMode.ForceRemote : ReleaseFetchMode.UseCache;

        Task<Result<IEnumerable<string>, NetworkError>> SearchAsync() =>
            releaseManager.SearchRemoteReleases(searchQuery, fetchMode, cancellationToken);

        var result = json
            ? await SearchAsync()
            : await console.Status()
                .StartAsync("Fetching available versions...", async _ => await SearchAsync());

        switch (result)
        {
            case Result<IEnumerable<string>, NetworkError>.Success(var releaseNames):
                WriteReleases(releaseNames);
                return;

            case Result<IEnumerable<string>, NetworkError>.Failure(NetworkError.ManifestRefreshFailure(var releaseNames)):
                logger.LogWarning($"Failed to refresh release cache. Showing cached releases.");
                if (!json)
                {
                    console.MarkupLine(Messages.ReleaseCacheRefreshFailed);
                }

                WriteReleases(releaseNames);
                return;

            case Result<IEnumerable<string>, NetworkError>.Failure(var error):
                var errorMessage = error switch
                {
                    NetworkError.RequestFailure(var url, var statusCode, _) =>
                        $"Request to {url} failed with status code {statusCode}",
                    NetworkError.ConnectionFailure(var message, _) =>
                        $"Network error: {message}",
                    NetworkError.CacheReadFailure(var fileError) =>
                        $"Release cache read error: {fileError}",
                    NetworkError.CacheWriteFailure(var fileError) =>
                        $"Release cache write error: {fileError}",
                    _ => "Unknown network error"
                };

                logger.LogError("Error searching releases: {ErrorMessage}", errorMessage);
                console.MarkupLine(
                    Messages.SomethingWentWrong("when trying to search releases", pathService)
                );

                throw new InvalidOperationException(errorMessage);
        }

        void WriteReleases(IEnumerable<string> releaseNames)
        {
            var releases = releaseNames
                .Select(name => new RemoteReleaseView(name))
                .ToList();

            if (json)
            {
                console.Profile.Out.Writer.WriteLine(releases.ToJson());
            }
            else
            {
                console.Write(releases.ToColumns());
            }
        }
    }
}

internal readonly record struct RemoteReleaseView(
    [property: JsonPropertyName("name")]
    string Name
) : IJsonView<RemoteReleaseView>;

internal static class RemoteReleaseViewExtensions
{
    extension(IReadOnlyList<RemoteReleaseView> releases)
    {
        public Columns ToColumns()
        {
            return new Columns(releases.Select(r => new Markup(r.Name)));
        }
    }
}
