using Fgvm.Cli.Error;
using Fgvm.Godot;
using Spectre.Console;

namespace Fgvm.Cli.Prompts;

public static class Install
{
    public static async Task<string> ShowVersionSelectionPrompt(string[] releaseNames,
        IAnsiConsole console,
        CancellationToken cancellationToken
    )
    {
        var versionPrompt = CreateVersionSelectionPrompt(releaseNames);
        var version = await versionPrompt.ShowAsync(console, cancellationToken);

        var runtimePrompt = CreateRuntimePrompt();
        var godotPlatform = await runtimePrompt.ShowAsync(console, cancellationToken) == "Mono"
            ? RuntimeEnvironment.Mono
            : RuntimeEnvironment.Standard;

        return $"{version}-{godotPlatform.Name()}";
    }

    public static SelectionPrompt<string> CreateRuntimePrompt()
    {
        var isMonoPrompt = new SelectionPrompt<string>()
            .Title(Messages.SelectARuntime)
            .AddChoices("Standard", "Mono");

        return isMonoPrompt;
    }

    public static SelectionPrompt<string> CreateVersionSelectionPrompt(string[] releaseNames)
    {
        var versionPrompt = new SelectionPrompt<string>()
            .Title($"{Messages.SelectAVersionTo("install")}")
            .PageSize(20)
            .WrapAround()
            .EnableSearch()
            .MoreChoicesText(Messages.MoreChoicesText)
            .SearchPlaceholderText(Messages.TypeToSearch)
            .AddChoices(releaseNames);

        return versionPrompt;
    }
}
