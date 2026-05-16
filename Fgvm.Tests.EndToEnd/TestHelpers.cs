using Fgvm.Error;
using System.Text;

namespace Fgvm.Tests.EndToEnd;

public static class TestHelpers
{
    extension(CommandResult result)
    {
        public void AssertSuccessfulExecution(string? expectedOutput = null, string[]? additionalContext = null)
        {
            if (result.ExitCode != ExitCodes.Success)
            {
                var builder = new StringBuilder();
                if (!string.IsNullOrEmpty(expectedOutput))
                {
                    builder.AppendLine($"Context: {expectedOutput}");
                }

                builder.AppendLine($"Exit Code: {result.ExitCode}");
                builder.AppendLine("STDOUT:");
                builder.AppendLine(result.Stdout);
                builder.AppendLine("STDERR:");
                builder.AppendLine(result.Stderr);

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

            Assert.Equal(ExitCodes.Success, result.ExitCode);
        }

        public void AssertFailedExecution(string? expectedError = null)
        {
            Assert.NotEqual(ExitCodes.Success, result.ExitCode);

            if (expectedError != null)
            {
                Assert.Contains(expectedError, result.Stderr, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
