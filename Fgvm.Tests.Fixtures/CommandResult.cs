using System.Text;
using Fgvm.Error;

namespace Fgvm.Tests.Fixtures;

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr)
{
    public void AssertSuccessfulExecution(string? expectedOutput = null, string[]? additionalContext = null)
    {
        if (ExitCode != ExitCodes.Success)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(expectedOutput))
            {
                builder.AppendLine($"Context: {expectedOutput}");
            }

            builder.AppendLine($"Exit Code: {ExitCode}");
            builder.AppendLine("STDOUT:");
            builder.AppendLine(Stdout);
            builder.AppendLine("STDERR:");
            builder.AppendLine(Stderr);

            if (additionalContext != null)
            {
                foreach (var context in additionalContext)
                {
                    builder.AppendLine("Additional Context:");
                    builder.AppendLine(context);
                }
            }

            Assert.Fail(builder.ToString());
        }

        Assert.Equal(ExitCodes.Success, ExitCode);
    }

    public void AssertFailedExecution(string? expectedError = null)
    {
        Assert.NotEqual(ExitCodes.Success, ExitCode);

        if (expectedError != null)
        {
            Assert.Contains(expectedError, Stderr, StringComparison.OrdinalIgnoreCase);
        }
    }
}
