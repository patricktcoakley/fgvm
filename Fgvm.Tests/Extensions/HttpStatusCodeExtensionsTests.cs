using System.Net;
using Fgvm.Extensions;

namespace Fgvm.Tests.Extensions;

public sealed class HttpStatusCodeExtensionsTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsTransient_TemporaryLoadOrAvailability_ReturnsTrue(HttpStatusCode statusCode) =>
        Assert.True(statusCode.IsTransient);

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public void IsTransient_RequestOrProtocolProblem_ReturnsFalse(HttpStatusCode statusCode) =>
        Assert.False(statusCode.IsTransient);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "403 (Forbidden)")]
    [InlineData(HttpStatusCode.NotFound, "404 (NotFound)")]
    [InlineData(HttpStatusCode.PartialContent, "206 (PartialContent)")]
    public void Describe_KeepsBothTheNumberAndTheName(HttpStatusCode statusCode, string expected) =>
        Assert.Equal(expected, statusCode.Describe());

    [Theory]
    [InlineData(599)]
    [InlineData(420)]
    public void Describe_NonstandardCode_RendersTheNumberAlone(int statusCode) =>
        // Enum.ToString falls back to the number, so naming it would just repeat it: "599 (599)".
        Assert.Equal(statusCode.ToString(), ((HttpStatusCode)statusCode).Describe());
}
