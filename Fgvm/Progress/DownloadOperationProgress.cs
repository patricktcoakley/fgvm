using System.Diagnostics;
using Fgvm.Godot.Download;

namespace Fgvm.Progress;

/// <summary>
///     Adapts byte-level download progress to a typed operation stage.
/// </summary>
internal sealed class DownloadOperationProgress<TStage>(
    IProgress<OperationProgress<TStage>> progress,
    TStage stage,
    string label,
    bool verbose
) : IProgress<DownloadProgress>
    where TStage : Enum
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public void Report(DownloadProgress value)
    {
        if (value.SourceUrl is { } sourceUrl)
        {
            if (verbose)
            {
                progress.Report(new OperationProgress<TStage>(
                    stage,
                    $"Downloading from {sourceUrl}...",
                    true));
            }

            return;
        }

        if (value.TotalBytes is not ({ } totalBytes and > 0))
        {
            return;
        }

        var progressText = DownloadProgressFormatter.Format(
            value.BytesDownloaded, totalBytes, Stopwatch.GetElapsedTime(_startTimestamp));
        progress.Report(new OperationProgress<TStage>(stage, $"{label} • {progressText}"));
    }
}
