using Fgvm.Progress;
using Spectre.Console;

namespace Fgvm.Cli.Progress;

/// <summary>
///     Spectre.Console-specific progress handler for CLI operations
/// </summary>
/// <typeparam name="TStage">The enum type representing operation stages</typeparam>
public class SpectreProgressHandler<TStage>(IAnsiConsole console) : IProgressHandler<TStage>
    where TStage : Enum
{
    /// <inheritdoc />
    public async Task<T> TrackProgressAsync<T>(Func<IProgress<OperationProgress<TStage>>, Task<T>> operation)
    {
        return await console.Status()
            .StartAsync("Starting operation...", async ctx =>
            {
                return await operation(new StatusProgress(ctx));
            });
    }

    private sealed class StatusProgress(StatusContext context) : IProgress<OperationProgress<TStage>>
    {
        private readonly object _lock = new();

        public void Report(OperationProgress<TStage> value)
        {
            lock (_lock)
            {
                context.Status = value.Message;
                context.Refresh();
            }
        }
    }
}
