namespace Fgvm.Extensions;

/// <summary>
///     Marks requests whose caller already owns a retry budget.
/// </summary>
public static class HttpRequestExtensions
{
    private static readonly HttpRequestOptionsKey<bool> SelfRetryingKey = new("Fgvm.SelfRetrying");

    extension(HttpRequestMessage request)
    {
        /// <summary>
        ///     Declares that the caller retries this request itself, so outer handlers must send it exactly once.
        /// </summary>
        public void MarkSelfRetrying() => request.Options.Set(SelfRetryingKey, true);

        /// <summary>
        ///     Whether the caller retries this request itself. Retrying again on its behalf would multiply its
        ///     attempt budget and its backoff delays.
        /// </summary>
        public bool IsSelfRetrying => request.Options.TryGetValue(SelfRetryingKey, out var selfRetrying) && selfRetrying;
    }
}
