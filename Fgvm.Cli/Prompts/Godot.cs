using Fgvm.Cli.Error;
using Spectre.Console;

namespace Fgvm.Cli.Prompts;

public static class Godot
{
    public static async Task<string> ShowGodotSelectionPrompt(IReadOnlyList<string> installed,
        IAnsiConsole console,
        CancellationToken cancellationToken
    )
    {
        var prompt = CreateGodotSelectionPrompt(installed);
        return await prompt.ShowAsync(console, cancellationToken);
    }

    private static SelectionPrompt<string> CreateGodotSelectionPrompt(IReadOnlyList<string> installed) =>
        new SelectionPrompt<string>()
            .Title($"{Messages.SelectAVersionTo("launch")}")
            .PageSize(10)
            .WrapAround()
            .EnableSearch()
            .MoreChoicesText(Messages.MoreChoicesText)
            .AddChoices(installed);
}
