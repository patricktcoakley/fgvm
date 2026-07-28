namespace Fgvm.Progress;

/// <summary>
///     Interface for progress handling that can be implemented by different UI frameworks
/// </summary>
public interface IProgressHandler
{
    /// <summary>
    ///     Executes one or more operations in a shared progress session.
    /// </summary>
    /// <param name="operation">The work to perform with the shared progress session.</param>
    /// <returns>The result of the operation</returns>
    Task<T> TrackProgressAsync<T>(Func<IProgressSession, Task<T>> operation);
}

/// <summary>
///     Registers the operations displayed within one progress session.
/// </summary>
public interface IProgressSession
{
    /// <summary>
    ///     Registers one operation. Its row is created when it first reports non-verbose progress.
    /// </summary>
    /// <param name="name">Stable display name for the operation.</param>
    IOperationProgress<TStage> AddOperation<TStage>(string name) where TStage : Enum;
}

/// <summary>
///     Reports typed stage updates and the terminal state for one operation.
/// </summary>
public interface IOperationProgress<TStage> : IProgress<OperationProgress<TStage>> where TStage : Enum
{
    /// <summary>
    ///     Marks the operation finished. Later reports are ignored.
    /// </summary>
    /// <param name="message">The final message to display.</param>
    void Complete(string message = "Completed");

    /// <summary>
    ///     Marks the operation canceled. Later reports are ignored.
    /// </summary>
    /// <param name="message">The final message to display.</param>
    void Cancel(string message = "Canceled");

    /// <summary>
    ///     Marks the operation failed. Later reports are ignored.
    /// </summary>
    /// <param name="message">The final message to display.</param>
    void Fail(string message = "Failed");
}
