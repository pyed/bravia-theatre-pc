using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using Grpc.Core;
using FakeBraviaClient = BraviaTheatre.Tests.EngineRegressionTests.FakeBraviaClient;

namespace BraviaTheatre.Tests;

/// <summary>
/// The soundbar ends a local session about an hour after the handshake and then rejects every
/// state query. The engine used to treat that as a lost connection, so the tray dropped offline
/// and rediscovered the soundbar every hour.
/// </summary>
public class DeviceSessionRenewalTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepaliveDelay = TimeSpan.FromSeconds(25);

    [Fact]
    public async Task RejectedKeepaliveRenewsTheSessionWithoutReconnecting()
    {
        var client = new FakeBraviaClient();
        var stateCalls = 0;
        client.StateHandler = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref stateCalls) switch
            {
                1 => new Dictionary<string, object?> { ["power"] = true, ["volume"] = 10 },
                2 => new Dictionary<string, object?>(),
                _ => new Dictionary<string, object?> { ["power"] = true, ["volume"] = 12 }
            });
        };

        var clients = 0;
        var keepaliveDelays = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => Interlocked.Increment(ref clients) == 1
                ? client
                : throw new InvalidOperationException("Unexpected reconnect."),
            (delay, ct) => delay == KeepaliveDelay && Interlocked.Increment(ref keepaliveDelays) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, ct));
        var states = RecordStates(engine);
        var restored = NewSignal();
        engine.StateChanged += state =>
        {
            if (state.Connected && state.Volume == 12) restored.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await restored.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await client.NotificationsReopened.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(2, client.SessionInitializations);
            Assert.Equal(2, client.NotificationStreams);
            Assert.Equal(1, Volatile.Read(ref clients));
            Assert.False(client.Disposed.Task.IsCompleted);
            AssertNeverDisconnectedAfterConnecting(states);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SessionIsRenewedOnTheOpenConnectionBeforeTheDeviceEndsIt()
    {
        var client = new FakeBraviaClient();
        var polledAfterRenewal = NewSignal();
        client.StateHandler = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (client.SessionInitializations == 2) polledAfterRenewal.TrySetResult(true);
            return Task.FromResult(new Dictionary<string, object?> { ["power"] = true, ["volume"] = 10 });
        };

        var clock = new ManualTimeProvider();
        var clients = 0;
        var keepaliveDelays = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => Interlocked.Increment(ref clients) == 1
                ? client
                : throw new InvalidOperationException("Unexpected reconnect."),
            (delay, ct) =>
            {
                if (delay != KeepaliveDelay || Interlocked.Increment(ref keepaliveDelays) > 1)
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);

                clock.Advance(BraviaEngine.DeviceSessionRenewalInterval);
                return Task.CompletedTask;
            },
            clock);
        var states = RecordStates(engine);

        try
        {
            engine.Start();
            await polledAfterRenewal.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await client.NotificationsReopened.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(2, client.SessionInitializations);
            Assert.Equal(2, client.NotificationStreams);
            Assert.Equal(1, Volatile.Read(ref clients));
            Assert.True(engine.CurrentState.Connected);
            AssertNeverDisconnectedAfterConnecting(states);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SessionIsNotRenewedBeforeItIsDue()
    {
        var client = new FakeBraviaClient();
        var keepalivePolled = NewSignal();
        var stateCalls = 0;
        client.StateHandler = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref stateCalls) == 2) keepalivePolled.TrySetResult(true);
            return Task.FromResult(new Dictionary<string, object?> { ["power"] = true });
        };

        var clock = new ManualTimeProvider();
        var keepaliveDelays = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            (delay, ct) =>
            {
                if (delay != KeepaliveDelay || Interlocked.Increment(ref keepaliveDelays) > 1)
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);

                clock.Advance(BraviaEngine.DeviceSessionRenewalInterval - TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            },
            clock);

        try
        {
            engine.Start();
            await keepalivePolled.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, client.SessionInitializations);
            Assert.Equal(1, client.NotificationStreams);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task FailedRenewalFallsBackToReconnecting()
    {
        var first = new FakeBraviaClient();
        var second = new FakeBraviaClient();
        var firstStateCalls = 0;
        first.StateHandler = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref firstStateCalls) == 1)
                return Task.FromResult(new Dictionary<string, object?> { ["power"] = true });

            first.InitFailure = new RpcException(new Status(StatusCode.InvalidArgument, "Session rejected."));
            return Task.FromResult(new Dictionary<string, object?>());
        };

        var clients = 0;
        var keepaliveDelays = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => Interlocked.Increment(ref clients) switch
            {
                1 => first,
                2 => second,
                _ => throw new InvalidOperationException("Unexpected extra reconnect.")
            },
            (delay, ct) => delay != KeepaliveDelay
                ? Task.CompletedTask
                : Interlocked.Increment(ref keepaliveDelays) == 1
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.InfiniteTimeSpan, ct));

        try
        {
            engine.Start();
            await second.Initialized.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(2, first.SessionInitializations);
            Assert.True(first.Disposed.Task.IsCompleted);
            Assert.Equal(2, Volatile.Read(ref clients));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task RenewalClosesTheOpenStreamAndHoldsTheNextUntilItEnds()
    {
        var gate = new NotifyStreamGate();
        var ct = TestContext.Current.CancellationToken;

        var first = await gate.OpenStreamAsync(ct);
        gate.BeginRenewal();
        Assert.True(first.IsCancellationRequested);
        gate.CloseStream(first);

        // A stream opened during the handshake would still be bound to the old session.
        var next = gate.OpenStreamAsync(ct);
        Assert.False(next.IsCompleted);

        gate.EndRenewal();
        var second = await next.WaitAsync(TestTimeout, ct);
        Assert.False(second.IsCancellationRequested);
        gate.CloseStream(second);
    }

    [Fact]
    public async Task RenewalAfterTheStreamEndedOnItsOwnIsHarmless()
    {
        var gate = new NotifyStreamGate();
        var ct = TestContext.Current.CancellationToken;

        gate.CloseStream(await gate.OpenStreamAsync(ct));
        gate.BeginRenewal();
        gate.EndRenewal();

        var stream = await gate.OpenStreamAsync(ct).WaitAsync(TestTimeout, ct);
        Assert.False(stream.IsCancellationRequested);
        gate.CloseStream(stream);
    }

    [Fact]
    public async Task ClosingTheConnectionReleasesAStreamWaitingOnARenewal()
    {
        var gate = new NotifyStreamGate();
        using var connection = new CancellationTokenSource();

        gate.BeginRenewal();
        var waiting = gate.OpenStreamAsync(connection.Token);
        connection.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
    }

    private static List<SoundbarState> RecordStates(BraviaEngine engine)
    {
        var states = new List<SoundbarState>();
        engine.StateChanged += state =>
        {
            lock (states) states.Add(state);
        };
        return states;
    }

    private static void AssertNeverDisconnectedAfterConnecting(List<SoundbarState> states)
    {
        lock (states)
        {
            var firstConnected = states.FindIndex(state => state.Connected);
            Assert.True(firstConnected >= 0, "The engine never connected.");
            Assert.All(states.Skip(firstConnected), state => Assert.True(state.Connected));
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
