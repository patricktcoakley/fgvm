using System.Security;
using Fgvm.Cli.Error;
using Fgvm.Types;

namespace Fgvm.Cli.Services;

internal static class TemplateInstallationResult
{
    public static string DescribeError(TemplateInstallationError error) =>
        error switch
        {
            TemplateInstallationError.InvalidQuery invalid => invalid.Message,
            TemplateInstallationError.NotFound notFound => $"Export templates for {notFound.Version} could not be found.",
            TemplateInstallationError.ChecksumMismatch mismatch =>
                $"Checksum mismatch for {mismatch.FileName}: expected {mismatch.Expected}, got {mismatch.Actual}.",
            TemplateInstallationError.Failed failed => failed.Reason,
            _ => "Unknown template installation error."
        };

    public static void EnsureSuccess(Result<TemplateInstallationOutcome, TemplateInstallationError> result)
    {
        switch (result)
        {
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Success:
                return;
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                TemplateInstallationError.InvalidQuery invalid):
                throw new ArgumentException(invalid.Message);
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                TemplateInstallationError.NotFound notFound):
                throw new ArgumentException(Messages.TemplateInstallationNotFound(notFound.Version));
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                TemplateInstallationError.ChecksumMismatch mismatch):
                throw new SecurityException(Messages.ChecksumMismatch(mismatch.FileName, mismatch.Expected, mismatch.Actual));
            case Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                TemplateInstallationError.Failed failed):
                throw new InvalidOperationException(Messages.TemplateInstallationFailed(failed.Reason));
            default:
                throw new InvalidOperationException("Unknown template installation result type.");
        }
    }
}
