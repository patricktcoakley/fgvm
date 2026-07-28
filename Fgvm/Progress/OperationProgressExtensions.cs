namespace Fgvm.Progress;

/// <summary>
///     Helpers for driving an operation's progress row through to a terminal state.
/// </summary>
public static class OperationProgressExtensions
{
    /// <summary>
    ///     Runs an operation and reports its terminal state, so every caller marks cancellation and failure alike.
    /// </summary>
    /// <param name="progress">The operation row to drive.</param>
    /// <param name="operation">The work to run.</param>
    /// <param name="succeeded">Whether the returned result counts as success.</param>
    public static async Task<T> RunAsync<TStage, T>(this IOperationProgress<TStage> progress,
        Func<Task<T>> operation,
        Func<T, bool> succeeded
    )
        where TStage : Enum
    {
        try
        {
            var result = await operation();
            if (succeeded(result))
            {
                progress.Complete();
            }
            else
            {
                progress.Fail();
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            progress.Cancel();
            throw;
        }
        catch
        {
            progress.Fail();
            throw;
        }
    }
}
