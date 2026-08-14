using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Error;

namespace Fgvm.Cli.Filter;

internal sealed class ExitCodeFilter(DiagnosticConsole diagnostics, ConsoleAppFilter next) : ConsoleAppFilter(next)
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
            diagnostics.MarkupLine(Messages.ConfigurationError(ex.Message));
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
            diagnostics.MarkupLine(Messages.ExceptionMessage(string.IsNullOrWhiteSpace(e.Message) ? "Invalid arguments" : e.Message));
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
