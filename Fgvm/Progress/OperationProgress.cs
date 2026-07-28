namespace Fgvm.Progress;

/// <summary>
///     Progress model for any operation with typed stages
/// </summary>
/// <typeparam name="TStage">The enum type representing operation stages</typeparam>
/// <param name="Stage">The current stage of the operation</param>
/// <param name="Message">A descriptive message about the current progress</param>
/// <param name="IsVerboseDetail">Whether the message is an additional verbose line rather than a status replacement.</param>
/// <param name="BytesDownloaded">Downloaded bytes when reporting byte progress.</param>
/// <param name="TotalBytes">Total bytes when known.</param>
public readonly record struct OperationProgress<TStage>(
    TStage Stage,
    string Message,
    bool IsVerboseDetail = false,
    long? BytesDownloaded = null,
    long? TotalBytes = null
)
    where TStage : Enum;
