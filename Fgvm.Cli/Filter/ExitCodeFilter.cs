using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Error;
using Spectre.Console;

namespace Fgvm.Cli.Filter;

internal sealed class ExitCodeFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
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
        catch (TaskCanceledException)
        {
            exitCode = ExitCodes.Cancelled;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine(Messages.ConfigurationError(ex.Message));
            exitCode = ExitCodes.ConfigurationError;
        }
        catch (ProcessExitCodeException ex)
        {
            exitCode = ex.ExitCode;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or ArgumentNullException or ArgumentException or ArgumentParseFailedException)
        {
            if (e is ArgumentParseFailedException)
            {
                AnsiConsole.MarkupLine(Messages.ExceptionMessage(e.Message));
            }

            exitCode = ExitCodes.ArgumentError;
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
}
