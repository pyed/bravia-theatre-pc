using System.Collections.Concurrent;
using System.IO;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Wire;

namespace BraviaTheatre.Tests;

public sealed class EngineFailureClassificationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static SonyCredentials ValidCredentials() => new()
    {
        ClientId = "client",
        DeviceId = "device",
        KeyId = "key",
        HmacKey = new string('a', 64)
    };

    // A malformed device response reaches the handshake as a plain exception rather
    // than an RpcException. It used to fall through to the generic connection handler,
    // which logged only "Connection/Stream error: <TypeName>".
    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(FormatException))]
    public async Task NonRpcHandshakeFailureIsClassifiedAsProtocolFailure(Type failureType)
    {
        var logs = new ConcurrentQueue<string>();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new BraviaEngine(
            ValidCredentials(),
            "test-host",
            55051,
            (_, _, _) => new EngineRegressionTests.FakeBraviaClient
            {
                InitFailure = (Exception)Activator.CreateInstance(failureType)!
            },
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        engine.LogAction = message =>
        {
            logs.Enqueue(message);
            if (message.Contains("Classification=protocol_failure", StringComparison.Ordinal))
                observed.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await observed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var captured = logs.ToArray();
            Assert.Contains(captured, message =>
                message.Contains("Authenticated handshake failed", StringComparison.Ordinal)
                && message.Contains($"ExceptionType={failureType.Name}", StringComparison.Ordinal)
                && message.Contains("Classification=protocol_failure", StringComparison.Ordinal));

            // The vague generic handler must no longer be the only thing that reports it.
            Assert.DoesNotContain(captured, message =>
                message.Contains("Connection/Stream error", StringComparison.Ordinal));

            // The device is at fault, not the Sony authorization, so sign-in is not demanded.
            Assert.False(engine.CurrentState.AuthRequired);
            Assert.False(engine.CurrentState.DeviceAssociationRequired);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public void HmacKeyThatIsSixtyFourNonHexCharactersDoesNotThrow()
    {
        var key = new string('z', 64);

        var parsed = PacketSigner.ParseHmacKey(key);

        Assert.Equal(32, parsed.Length);
        Assert.Equal(PacketSigner.ComputeHmac(key, [1, 2, 3]), PacketSigner.ComputeHmac(parsed, [1, 2, 3]));
    }

    [Fact]
    public void HmacKeyThatIsSixtyFourHexCharactersStillDecodesAsHex()
    {
        var key = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));

        var parsed = PacketSigner.ParseHmacKey(key);

        Assert.Equal(Convert.FromHexString(key), parsed);
    }
}
