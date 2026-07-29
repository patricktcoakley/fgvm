using System.Net.Sockets;

namespace Fgvm.Extensions;

/// <summary>
///     Classifies exceptions raised while sending a request or reading its body.
/// </summary>
public static class HttpExceptionExtensions
{
    extension(Exception exception)
    {
        /// <summary>
        ///     Whether a fresh request can recover from the failure. Caller cancellation cannot, so this stays a
        ///     method: the answer depends on the caller's token, not on the exception alone.
        /// </summary>
        /// <remarks>
        ///     SocketException is defensive. HttpClient normally wraps connect and send failures in
        ///     HttpRequestException, and body-read failures in HttpIOException, so both are already covered by the
        ///     types ahead of it.
        /// </remarks>
        public bool IsTransientTransportFailure(CancellationToken cancellationToken) =>
            exception is HttpRequestException or IOException or SocketException ||
            exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
    }
}
