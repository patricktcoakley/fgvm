using System.Net.Sockets;
using Fgvm.Extensions;

namespace Fgvm.Tests.Extensions;

public sealed class HttpExceptionExtensionsTests
{
    public static TheoryData<Exception> RecoverableFailures =>
    [
        new HttpRequestException("connection refused"),
        new IOException("the response stream ended early"),
        new SocketException((int)SocketError.ConnectionReset),
        new TaskCanceledException("the stall timeout elapsed")
    ];

    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public void IsTransientTransportFailure_TransportAndStreamFailures_ReturnTrue(Exception exception) =>
        Assert.True(exception.IsTransientTransportFailure(CancellationToken.None));

    [Fact]
    public void IsTransientTransportFailure_CallerCancelled_ReturnsFalse()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(new TaskCanceledException().IsTransientTransportFailure(cancellation.Token));
    }

    [Fact]
    public void IsTransientTransportFailure_UnrelatedException_ReturnsFalse() =>
        Assert.False(new InvalidOperationException().IsTransientTransportFailure(CancellationToken.None));
}
