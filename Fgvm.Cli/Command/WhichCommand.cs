using System.Text.Json.Serialization;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Spectre.Console;

namespace Fgvm.Cli.Command;

public sealed class WhichCommand(
    IInstallationRegistry installationRegistry,
    IReleaseManager releaseManager,
    IPathService pathService,
    IAnsiConsole console
)
{
    /// <summary>
    ///     Show the path to the current Godot version.
    /// </summary>
    public void Which(bool json = false)
    {
        var view = WhichView.Create(installationRegistry.GetDefault(), releaseManager, pathService);

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

    public static WhichView Create(Result<Installation, InstallationRegistryError> result,
        IReleaseManager releaseManager,
        IPathService pathService
    ) => result switch
    {
        Result<Installation, InstallationRegistryError>.Success(var installation) =>
            CreateSuccess(installation, releaseManager, pathService),
        Result<Installation, InstallationRegistryError>.Failure(InstallationRegistryError.NotFound) =>
            new WhichView(false, null, Messages.NoCurrentVersionSet),
        _ => new WhichView(false, null, "Unknown installation registry error.")
    };

    public string ToDisplay()
    {
        if (HasVersion && ExecutablePath is not null)
        {
            return Messages.CurrentVersionSetTo(ExecutablePath);
        }

        return Message ?? Messages.UnknownSymlinkError;
    }

    private static WhichView CreateSuccess(Installation installation, IReleaseManager releaseManager, IPathService pathService)
    {
        if (releaseManager.CreateRelease(installation.ReleaseNameWithRuntime) is not
            Result<Release, ReleaseParseError>.Success(var release))
        {
            return new WhichView(false, null, "Default Godot version is invalid.");
        }

        var executablePath = Path.Combine(pathService.RootPath, installation.RelativePath, release.ExecName);
        if (executablePath.EndsWith(".app", StringComparison.Ordinal))
        {
            executablePath = Path.Combine(executablePath, "Contents", "MacOS", "Godot");
        }

        return new WhichView(true, executablePath, null);
    }
}
