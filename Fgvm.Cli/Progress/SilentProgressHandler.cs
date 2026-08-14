using Fgvm.Progress;

namespace Fgvm.Cli.Progress;

internal sealed class SilentOperationProgress<TStage> : IOperationProgress<TStage> where TStage : Enum
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
