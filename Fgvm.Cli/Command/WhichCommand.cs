using System.Text.Json.Serialization;
using Fgvm.Cli.Services;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Types;
using Spectre.Console;

namespace Fgvm.Cli.Command;

public sealed class WhichCommand(
    IVersionManagementService versionManagementService,
    IAnsiConsole console
)
{
    /// <summary>
    ///     Show the path to the effective Godot version for the current directory.
    /// </summary>
    public async Task Which(bool json = false, CancellationToken cancellationToken = default)
    {
        var view = WhichView.Create(await versionManagementService.ResolveEffectiveVersionAsync(cancellationToken));

        if (json)
        {
            console.Profile.Out.Writer.WriteLine(view.ToJson());
            return;
        }

        console.MarkupLine(view.ToDisplay());
    }
}

internal readonly record struct WhichView : IJsonView<WhichView>
{
    private WhichView(bool hasVersion, string? executablePath, string? message)
    {
        HasVersion = hasVersion;
        ExecutablePath = executablePath;
        Message = message;
    }

    [JsonPropertyName("hasVersion")]
    public bool HasVersion { get; init; }

    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    public static WhichView Create(Result<VersionResolutionOutcome.Found, VersionResolutionError> result) => result switch
    {
        Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(var found) =>
            new WhichView(true, found.ExecutablePath, null),
        Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(VersionResolutionError.NotFound) =>
            new WhichView(false, null, "No Godot version is currently set."),
        Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(VersionResolutionError.InvalidVersion) =>
            new WhichView(false, null, "Current Godot version is invalid."),
        Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(VersionResolutionError.Failed failed) =>
            new WhichView(false, null, failed.Reason),
        _ => new WhichView(false, null, "Unknown version resolution error.")
    };

    public string ToDisplay()
    {
        if (HasVersion && ExecutablePath is not null)
        {
            return Messages.CurrentVersionSetTo(ExecutablePath);
        }

        return Message is not null ? $"[red]{Message}[/]" : Messages.UnknownSymlinkError;
    }
}
