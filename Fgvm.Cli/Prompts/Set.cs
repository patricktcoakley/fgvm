using Fgvm.Cli.Error;
using Spectre.Console;

namespace Fgvm.Cli.Prompts;

public static class Set
{
    public static async Task<string> ShowSetVersionPrompt(string[] installed, IAnsiConsole console, CancellationToken cancellationToken)
    {
        var prompt = CreateSetVersionPrompt(installed);
        return await prompt.ShowAsync(console, cancellationToken);
    }

    private static SelectionPrompt<string> CreateSetVersionPrompt(string[] installed) =>
        new SelectionPrompt<string>()
            .Title($"{Messages.SelectAVersionTo("set")}")
            .PageSize(10)
            .WrapAround()
            .EnableSearch()
            .MoreChoicesText(Messages.MoreChoicesText)
            .AddChoices(installed);
}
