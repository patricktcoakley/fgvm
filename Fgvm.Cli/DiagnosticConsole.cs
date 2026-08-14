using Spectre.Console;

namespace Fgvm.Cli;

public sealed class DiagnosticConsole
{
    private const int Unwrapped = int.MaxValue;

    private readonly IAnsiConsole _console;

    public DiagnosticConsole(IAnsiConsole console)
    {
        _console = console;
        _console.Profile.Width = Unwrapped;
    }

    public IAnsiConsole AnsiConsole => _console;

    public void MarkupLine(string markup)
    {
        try
        {
            _console.MarkupLine(markup);
        }
        catch (Exception)
        {
            Console.Error.WriteLine(markup);
        }
    }
}
