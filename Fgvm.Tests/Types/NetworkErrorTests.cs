using System.Net;
using Fgvm.Types;

namespace Fgvm.Tests.Types;

public sealed class NetworkErrorTests
{
    [Fact]
    public void RequestFailure_ToString_IncludesStatusAndSanitizedResponseSummary()
    {
        var error = new NetworkError.RequestFailure(
            "https://github.com/example",
            HttpStatusCode.Forbidden,
            "rate\nlimit\u001b [shared IP] exceeded");

        var message = error.ToString();

        Assert.Contains("403 (Forbidden)", message, StringComparison.Ordinal);
        Assert.Contains("Response: rate limit  [shared IP] exceeded", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', message);
    }

    [Fact]
    public void RequestFailure_ToString_TruncatesLongResponseBody()
    {
        var error = new NetworkError.RequestFailure(
            "https://github.com/example",
            HttpStatusCode.Forbidden,
            new string('x', 1_000));

        var expectedResponse = $"Response: {new string('x', 512)}...";

        Assert.EndsWith(expectedResponse, error.ToString(), StringComparison.Ordinal);
    }
}
