using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Error;
using Fgvm.Types;
using Spectre.Console;

namespace Fgvm.Cli.Command;

public sealed class WhichCommand(
    IVersionManagementService versionManagementService,
    IAnsiConsole console
)
{
    /// <summary>
    ///     Show the path to the effective Godot version for the current directory.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Optional installed version query arguments.</param>
    public async Task Which(CancellationToken cancellationToken = default,
        [Argument] params string[] query
    )
    {
        var result = query.Length == 0
            ? await versionManagementService.ResolveEffectiveVersionAsync(cancellationToken)
            : await versionManagementService.ResolveInstalledVersionAsync(query, cancellationToken);

        switch (result)
        {
            case Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(var found):
                console.Profile.Out.Writer.WriteLine(found.ExecutablePath);
                return;
            case Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(var error):
                Console.Error.WriteLine(error switch
                {
                    VersionResolutionError.NotFound when query.Length > 0 =>
                        Messages.NoInstalledGodotVersionMatching(string.Join(" ", query)),
                    VersionResolutionError.NotFound =>
                        Messages.NoVersionCurrentlySet,
                    VersionResolutionError.InvalidVersion =>
                        Messages.CurrentVersionInvalid,
                    VersionResolutionError.Failed failed =>
                        failed.Reason,
                    _ => Messages.UnknownVersionResolutionError
                });
                throw new ProcessExitCodeException(ExitCodes.GeneralError);
            default:
                Console.Error.WriteLine(Messages.UnknownVersionResolutionError);
                throw new ProcessExitCodeException(ExitCodes.GeneralError);
        }
    }
}
