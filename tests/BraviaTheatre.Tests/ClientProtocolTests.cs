using BraviaTheatre.Core.Engine;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;

namespace BraviaTheatre.Tests;

public class ClientProtocolTests
{
    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.OutOfRange)]
    public void UnsupportedPathStatusesAreExplicitlyClassified(StatusCode statusCode)
    {
        Assert.True(BraviaClient.IsUnsupportedPathStatus(statusCode));
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unknown)]
    [InlineData(StatusCode.Unimplemented)]
    public void SystemicStatusesAreNotClassifiedAsUnsupported(StatusCode statusCode)
    {
        Assert.False(BraviaClient.IsUnsupportedPathStatus(statusCode));
    }

    [Fact]
    public void ReturnedRollingSessionIdReplacesRequestSessionId()
    {
        Assert.Equal(
            "rotated-session",
            BraviaClient.ResolveSessionId("original-session", "rotated-session"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRollingSessionIdKeepsRequestSessionId(string? responseSessionId)
    {
        Assert.Equal(
            "current-session",
            BraviaClient.ResolveSessionId("current-session", responseSessionId));
    }

    [Fact]
    public async Task DisposingTheChannelDisposesItsConnectionPool()
    {
        // Each reconnect creates a client; a pool left alive keeps its HTTP/2 keepalive pings going.
        var options = BraviaClient.CreateChannelOptions();
        var handler = Assert.IsType<SocketsHttpHandler>(options.HttpHandler);

        GrpcChannel.ForAddress("http://127.0.0.1:55051", options).Dispose();

        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:55051/");
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => invoker.SendAsync(request, TestContext.Current.CancellationToken));
    }
}
