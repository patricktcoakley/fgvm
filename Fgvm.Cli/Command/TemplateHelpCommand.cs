using ConsoleAppFramework;
using Spectre.Console;

namespace Fgvm.Cli.Command;

/// <summary>
///     Prints grouped template-command help as a ConsoleAppFramework workaround.
/// </summary>
/// <remarks>
///     Template subcommands are registered as hidden commands so root help only shows `template, t`.
///     ConsoleAppFramework does not generate the grouped nested help shape we want for that command,
///     so this command owns the template help text and the pre-dispatch `template -h|--help` path.
/// </remarks>
public sealed class TemplateHelpCommand(IAnsiConsole console)
{
    private const string HelpText =
        """
        Manage Godot export templates.

        Usage: fgvm template <COMMAND>

        Commands:
          install, i     Install Godot export templates.
          list, l        List installed Godot export templates.
          remove, r      Remove installed Godot export templates.
        """;

    public static bool TryWriteHelp(string[] args, TextWriter writer)
    {
        if (args is not [("template" or "t")] and not [("template" or "t"), ("-h" or "--help")])
        {
            return false;
        }

        writer.WriteLine(HelpText);
        return true;
    }

    /// <summary>
    ///     Manage Godot export templates.
    /// </summary>
    [Command("template|t")]
    public void Show()
    {
        console.Profile.Out.Writer.WriteLine(HelpText);
    }
}
