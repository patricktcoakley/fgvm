using Fgvm.Progress;
using Spectre.Console;

namespace Fgvm.Cli.Progress;

/// <summary>
///     Renders one or more operations in the same progress display.
/// </summary>
public sealed class SpectreProgressHandler(IAnsiConsole console) : IProgressHandler
{
    /// <inheritdoc />
    public async Task<T> TrackProgressAsync<T>(Func<IProgressSession, Task<T>> operation) =>
        await console.Progress()
            // Keep finished rows on screen; they carry each operation's terminal state
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
                await operation(new ProgressSession(console, context)));

    private sealed class ProgressSession(IAnsiConsole console, ProgressContext context) : IProgressSession
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _operationNames = new(StringComparer.Ordinal);

        public IOperationProgress<TStage> AddOperation<TStage>(string name) where TStage : Enum
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            lock (_lock)
            {
                if (_operationNames.Add(name) is false)
                {
                    throw new InvalidOperationException($"Progress operation `{name}` is already registered.");
                }

                return new OperationProgressReporter<TStage>(name, console, context, _lock);
            }
        }
    }

    private sealed class OperationProgressReporter<TStage>(
        string name,
        IAnsiConsole console,
        ProgressContext context,
        Lock sharedLock
    ) : IOperationProgress<TStage>
        where TStage : Enum
    {
        private TStage _currentStage = default!;
        private bool _hasStage;
        private ProgressTask? _task;
        private bool _terminal;

        public void Report(OperationProgress<TStage> value)
        {
            lock (sharedLock)
            {
                if (_terminal)
                {
                    return;
                }

                if (value.IsVerboseDetail)
                {
                    console.MarkupLine(Markup.Escape(value.Message));
                    return;
                }

                var description = Markup.Escape(Description(value.Message));
                var task = _task ??= context.AddTask(description);
                task.Description = description;

                // `TStage?` is never null for an enum type argument, so the first report needs its own flag
                if (!_hasStage || EqualityComparer<TStage>.Default.Equals(_currentStage, value.Stage) is false)
                {
                    _hasStage = true;
                    _currentStage = value.Stage;
                    task.Value = 0d;
                    task.IsIndeterminate = true;
                }

                if (value.BytesDownloaded is { } downloaded &&
                    value.TotalBytes is { } total &&
                    total > 0)
                {
                    task.IsIndeterminate = false;
                    task.Value = Math.Clamp(downloaded * 100d / total, 0d, 100d);
                }

                context.Refresh();
            }
        }

        public void Complete(string message = "Completed") =>
            Stop(message, complete: true);

        public void Cancel(string message = "Canceled") =>
            Stop(message, complete: false);

        public void Fail(string message = "Failed") =>
            Stop(message, complete: false);

        private void Stop(string message, bool complete)
        {
            lock (sharedLock)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
                if (_task is not { } task)
                {
                    return;
                }

                task.Description = Markup.Escape(Description(message));
                task.IsIndeterminate = false;
                if (complete)
                {
                    task.Value = 100d;
                }

                task.StopTask();
                context.Refresh();
            }
        }

        private string Description(string message) => $"{name} • {message}";
    }
}
