using System.Net;

namespace Fgvm.Types;

/// <summary>
///     Represents the possible errors that can occur during network operations.
/// </summary>
public abstract record NetworkError
{
    public record RequestFailure(string Url, HttpStatusCode StatusCode, string? Body = null) : NetworkError
    {
        private const int MaximumResponseSummaryLength = 512;

        public override string ToString()
        {
            var message = $"Request to {Url} failed with {StatusCode.Describe()}";
            if (SummarizeResponse(Body) is { } responseSummary)
            {
                message += $". Response: {responseSummary}";
            }

            return message;
        }

        private static string? SummarizeResponse(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var trimmed = body.AsSpan().Trim();
            var summaryLength = Math.Min(trimmed.Length, MaximumResponseSummaryLength);
            Span<char> summary = stackalloc char[summaryLength];
            for (var index = 0; index < summary.Length; index++)
            {
                summary[index] = char.IsControl(trimmed[index]) ? ' ' : trimmed[index];
            }

            return trimmed.Length > MaximumResponseSummaryLength
                ? $"{new string(summary)}..."
                : new string(summary);
        }
    }

    public record ConnectionFailure(string Message, string? Details = null) : NetworkError;

    public record ManifestRefreshFailure(IEnumerable<string> releaseIds) : NetworkError;

    public record CacheReadFailure(FileOperationError Error) : NetworkError
    {
        public override string ToString() => $"Unable to read release cache: {Error}";
    }

    public record CacheWriteFailure(FileOperationError Error) : NetworkError
    {
        public override string ToString() => $"Unable to write release cache: {Error}";
    }
}
