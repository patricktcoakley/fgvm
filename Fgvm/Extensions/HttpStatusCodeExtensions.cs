using System.Globalization;
using System.Net;

namespace Fgvm.Extensions;

/// <summary>
///     Shared HTTP status-code predicates and formatting used by every retrying code path.
/// </summary>
public static class HttpStatusCodeExtensions
{
    extension(HttpStatusCode statusCode)
    {
        /// <summary>
        ///     Whether the status is associated with temporary load or availability. Other 4xx responses indicate a
        ///     request or protocol problem that another attempt is unlikely to fix.
        /// </summary>
        public bool IsTransient => statusCode switch
        {
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => true,
            >= HttpStatusCode.InternalServerError => true,
            _ => false
        };

        /// <summary>
        ///     Renders the status as "404 (NotFound)" so the number stays searchable in logs and issue reports while
        ///     the name explains it. Nonstandard codes have no name to add, so they render as the number alone.
        /// </summary>
        public string Describe() => Enum.IsDefined(statusCode)
            ? $"{(int)statusCode} ({statusCode})"
            : ((int)statusCode).ToString(CultureInfo.InvariantCulture);
    }
}
