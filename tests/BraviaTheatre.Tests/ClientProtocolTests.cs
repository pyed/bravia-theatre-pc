using BraviaTheatre.Core.Engine;
using Grpc.Core;
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
}
