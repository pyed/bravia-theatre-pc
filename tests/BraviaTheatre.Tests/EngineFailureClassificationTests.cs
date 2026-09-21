using System.Collections.Concurrent;
using System.IO;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
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
        var attempts = 0;
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new BraviaEngine(
            ValidCredentials(),
            "test-host",
            55051,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return new EngineRegressionTests.FakeBraviaClient
                {
                    InitFailure = (Exception)Activator.CreateInstance(failureType)!
                };
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

    // Subscribers are invoked outside the state lock, so two threads mutating different
    // fields could deliver an older snapshot after a newer one.
    [Fact]
    public async Task ConcurrentUpdatesNeverPublishAStaleSnapshot()
    {
        const int Updates = 20000;

        using var engine = new BraviaEngine(ValidCredentials(), "test-host", 55051);
        engine.ApplySnapshot(
            new Dictionary<string, object?>
            {
                ["power"] = true,
                ["volume"] = 0,
                ["sound_setting.volume.rear"] = 0
            },
            "test-device");

        var gate = new object();
        var highestSeen = 0;
        var inversions = 0;
        SoundbarState? lastPublished = null;

        engine.StateChanged += state =>
        {
            lock (gate)
            {
                if (state.Volume < highestSeen) inversions++;
                else highestSeen = state.Volume;
                lastPublished = state;
            }
        };

        // Volume is written by exactly one writer and only ever increases, so any
        // published volume below one already published proves out-of-order delivery.
        var volumeWriter = Task.Run(
            () =>
            {
                for (var i = 1; i <= Updates; i++) engine.ApplyDelta("volume", i);
            },
            TestContext.Current.CancellationToken);
        var rearWriter = Task.Run(
            () =>
            {
                for (var i = 1; i <= Updates; i++) engine.ApplyDelta("sound_setting.volume.rear", i % 10);
            },
            TestContext.Current.CancellationToken);

        await Task.WhenAll(volumeWriter, rearWriter);

        lock (gate)
        {
            Assert.Equal(0, inversions);
            Assert.Equal(engine.CurrentState, lastPublished);
        }
    }
}
