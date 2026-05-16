namespace Fgvm.Cli.Error;

internal sealed class ProcessExitCodeException(int exitCode) : Exception($"Process exited with code {exitCode}.")
{
    public int ExitCode { get; } = exitCode;
}
