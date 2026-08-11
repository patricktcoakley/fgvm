using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Error;
using Spectre.Console;

namespace Fgvm.Cli.Filter;

internal sealed class ExitCodeFilter(IAnsiConsole console, ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        var exitCode = ExitCodes.Success;
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (InvalidSymlinkException)
        {
            exitCode = ExitCodes.SymlinkError;
        }
        catch (OperationCanceledException)
        {
            exitCode = ExitCodes.Cancelled;
        }
        catch (ConfigurationException ex)
        {
            exitCode = ExitCodes.ConfigurationError;
            Report(Messages.ConfigurationError(ex.Message));
        }
        catch (ProcessExitCodeException ex)
        {
            exitCode = ex.ExitCode;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException
                                      or ArgumentNullException
                                      or ArgumentException
                                      or ArgumentParseFailedException)
        {
            exitCode = ExitCodes.ArgumentError;
            Report(Messages.ExceptionMessage(string.IsNullOrWhiteSpace(e.Message) ? "Invalid arguments" : e.Message));
        }
        catch (Exception)
        {
            exitCode = ExitCodes.GeneralError;
        }
        finally
        {
            System.Environment.Exit(exitCode);
        }
    }

    /// <summary>
    ///     Renders a failure without wrapping so a path or query stays on one line and remains
    ///     greppable. Callers assign the exit code first, so a rendering fault can only degrade the
    ///     message, never turn the failure into a success.
    /// </summary>
    private void Report(string markup)
    {
        var width = console.Profile.Width;
        try
        {
            console.Profile.Width = int.MaxValue;
            console.MarkupLine(markup);
        }
        catch (Exception)
        {
            Console.Error.WriteLine(markup);
        }
        finally
        {
            console.Profile.Width = width;
        }
    }
}
