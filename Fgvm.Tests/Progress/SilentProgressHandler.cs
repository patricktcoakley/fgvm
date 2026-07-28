using Fgvm.Progress;

namespace Fgvm.Tests.Progress;

internal sealed class SilentProgressHandler : IProgressHandler
{
    public Task<T> TrackProgressAsync<T>(Func<IProgressSession, Task<T>> operation) =>
        operation(new SilentProgressSession());

    private sealed class SilentProgressSession : IProgressSession
    {
        public IOperationProgress<TStage> AddOperation<TStage>(string name) where TStage : Enum =>
            new SilentOperationProgress<TStage>();
    }

    private sealed class SilentOperationProgress<TStage> : IOperationProgress<TStage> where TStage : Enum
    {
        public void Report(OperationProgress<TStage> value)
        { }

        public void Complete(string message = "Completed")
        { }

        public void Cancel(string message = "Canceled")
        { }

        public void Fail(string message = "Failed")
        { }
    }
}
