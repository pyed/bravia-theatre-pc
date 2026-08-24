using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.Core.Wire;

namespace BraviaTheatre.Tests;

public class EngineRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ShortSoundFieldNotificationUpdatesCanonicalEngineState()
    {
        var pathField = ProtobufWireCodec.LengthDelimited(1, "sound_field"u8.ToArray());
        var entry = pathField.Concat(new byte[] { 0x10, 0x01 }).ToArray();
        var notification = ProtobufWireCodec.LengthDelimited(
            2,
            ProtobufWireCodec.LengthDelimited(
                1,
                ProtobufWireCodec.LengthDelimited(1, entry)));

        var (path, value) = NotifyParser.ParseNotifyMessage(notification);

        Assert.Equal("sound_setting.sound_field", path);
        Assert.True(Assert.IsType<bool>(value));

        using var engine = new BraviaEngine(new SonyCredentials());
        engine.ApplyDelta(path!, value);

        Assert.True(engine.CurrentState.SoundField);
    }

    [Theory]
    [InlineData("playback_control.audio_format", "NoAudio", false)]
    [InlineData("audio_output.stream_info.audio_format", "NoAudio", false)]
    [InlineData("playback_control.audio_format", "NoAudio", true)]
    [InlineData("audio_output.stream_info.audio_format", "NoAudio", true)]
    [InlineData("playback_control.audio_format", " NONE ", false)]
    [InlineData("audio_output.stream_info.audio_format", "Unknown", true)]
    [InlineData("playback_control.audio_format", "", true)]
    [InlineData("audio_output.stream_info.audio_format", null, false)]
    public void NoAudioAndOtherSentinelsClearPreviouslySetCodec(
        string path,
        string? sentinel,
        bool useSnapshot)
    {
        using var engine = new BraviaEngine(new SonyCredentials());

        ApplyAudioValue(engine, path, "dolby_atmos_truehd", useSnapshot);
        Assert.Equal("dolby_atmos_truehd", engine.CurrentState.Codec);

        ApplyAudioValue(engine, path, sentinel, useSnapshot);
        Assert.Null(engine.CurrentState.Codec);
    }

    [Theory]
    [InlineData("playback_control.audio_channel", "unknown", false)]
    [InlineData("playback_control.audio_channel", "none", false)]
    [InlineData("playback_control.audio_channel", null, false)]
    [InlineData("playback_control.audio_channel", " UNKNOWN ", false)]
    [InlineData("audio_output.stream_info.channel_info", "unknown", false)]
    [InlineData("audio_output.stream_info.channel_info", "none", true)]
    [InlineData("audio_output.stream_info.channel_info", "None", true)]
    [InlineData("playback_control.audio_channel", "NoChannel", false)]
    [InlineData("playback_control.audio_channel", "INVALID", true)]
    [InlineData("audio_output.stream_info.channel_info", "", true)]
    [InlineData("playback_control.audio_channel", null, true)]
    public void UnknownOrNoneClearsPreviouslySetChannel(string path, string? sentinel, bool useSnapshot)
    {
        using var engine = new BraviaEngine(new SonyCredentials());

        ApplyAudioValue(engine, path, "7.1.4", useSnapshot);
        Assert.Equal("7.1.4", engine.CurrentState.Channel);

        ApplyAudioValue(engine, path, sentinel, useSnapshot);
        Assert.Null(engine.CurrentState.Channel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoAudioClearsCodecAndChannelTogether(bool useSnapshot)
    {
        using var engine = new BraviaEngine(new SonyCredentials());
        engine.ApplySnapshot(
            new Dictionary<string, object?>
            {
                ["playback_control.audio_format"] = "dolby_atmos_truehd",
                ["playback_control.audio_channel"] = "7.1.4"
            },
            "Test Bar");

        if (useSnapshot)
        {
            engine.ApplySnapshot(
                new Dictionary<string, object?> { ["playback_control.audio_format"] = "NoAudio" },
                "Test Bar");
        }
        else
        {
            engine.ApplyDelta("playback_control.audio_format", "NoAudio");
        }

        Assert.Null(engine.CurrentState.Codec);
        Assert.Null(engine.CurrentState.Channel);
    }

    [Fact]
    public void MissingSnapshotAudioKeysPreserveCurrentValues()
    {
        using var engine = new BraviaEngine(new SonyCredentials());
        engine.ApplyDelta("playback_control.audio_format", "dts_unknown");
        engine.ApplyDelta("playback_control.audio_channel", "5.1.2");

        engine.ApplySnapshot(new Dictionary<string, object?> { ["power"] = true }, "Test Bar");

        Assert.Equal("dts_unknown", engine.CurrentState.Codec);
        Assert.Equal("dts", engine.CurrentState.CodecBadgeKind);
        Assert.Equal("5.1.2", engine.CurrentState.Channel);
    }

    [Fact]
    public void PresentPrimaryAudioSentinelDoesNotFallBackToSecondaryValue()
    {
        using var engine = new BraviaEngine(new SonyCredentials());

        engine.ApplySnapshot(
            new Dictionary<string, object?>
            {
                ["audio_output.stream_info.audio_format"] = null,
                ["playback_control.audio_format"] = "dolby_atmos_truehd",
                ["audio_output.stream_info.channel_info"] = "unknown",
                ["playback_control.audio_channel"] = "7.1.4"
            },
            "Test Bar");

        Assert.Null(engine.CurrentState.Codec);
        Assert.Null(engine.CurrentState.Channel);
    }

    [Fact]
    public async Task ConnectionTeardownPublishesDisconnectedBeforeClientDisposal()
    {
        var client = new FakeBraviaClient();
        var connected = NewSignal();
        var disconnected = NewSignal();
        var sawConnected = 0;
        var disconnectedNotifications = 0;
        SoundbarState? stateAtDispose = null;

        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        client.OnDispose = () => stateAtDispose = engine.CurrentState;
        engine.StateChanged += state =>
        {
            if (state.Connected)
            {
                Interlocked.Exchange(ref sawConnected, 1);
                connected.TrySetResult(true);
            }
            else if (Volatile.Read(ref sawConnected) == 1)
            {
                Interlocked.Increment(ref disconnectedNotifications);
                disconnected.TrySetResult(true);
            }
        };

        try
        {
            engine.Start();
            await connected.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            client.EndNotifications.TrySetResult(true);

            await disconnected.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(engine.CurrentState.Connected);
            Assert.NotNull(stateAtDispose);
            Assert.False(stateAtDispose!.Connected);
            Assert.Equal(1, Volatile.Read(ref disconnectedNotifications));
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task ReconnectWaitsForOldCommandReaderAndIsolatesReplacementClient()
    {
        var first = new FakeBraviaClient();
        var second = new FakeBraviaClient();
        var secondCreated = NewSignal();
        var firstCommandCanceled = NewSignal();
        var firstCommandCleanupWaiting = NewSignal();
        var allowFirstCommandCleanup = NewSignal();
        var firstPowered = NewSignal();
        var secondPowered = NewSignal();
        var connectedStates = new ConcurrentQueue<bool>();
        var createdClientCount = 0;
        var activeReadersAtFirstDispose = -1;
        var activeReadersAtSecondCreation = -1;
        SoundbarState? stateAtFirstDispose = null;
        BraviaEngine engine = null!;

        first.CommandHandler = async ct =>
        {
            using var registration = ct.Register(() => firstCommandCanceled.TrySetResult(true));
            await firstCommandCanceled.Task;
            firstCommandCleanupWaiting.TrySetResult(true);
            await allowFirstCommandCleanup.Task;
        };

        IBraviaClient CreateClient(string host, int port, SonyCredentials credentials)
        {
            var clientNumber = Interlocked.Increment(ref createdClientCount);
            if (clientNumber == 1) return first;
            if (clientNumber == 2)
            {
                activeReadersAtSecondCreation = engine.ActiveCommandDrainReaders;
                secondCreated.TrySetResult(true);
                return second;
            }

            throw new InvalidOperationException("Unexpected extra reconnect.");
        }

        engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            CreateClient,
            static (delay, ct) => delay == TimeSpan.FromSeconds(25)
                ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                : Task.CompletedTask);

        first.OnDispose = () =>
        {
            stateAtFirstDispose = engine.CurrentState;
            activeReadersAtFirstDispose = engine.ActiveCommandDrainReaders;
        };
        engine.StateChanged += state =>
        {
            connectedStates.Enqueue(state.Connected);
            if (!state.Connected || !state.Power) return;

            if (secondCreated.Task.IsCompleted)
            {
                secondPowered.TrySetResult(true);
            }
            else
            {
                firstPowered.TrySetResult(true);
            }
        };

        try
        {
            engine.Start();
            await firstPowered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(await engine.SetVolumeAsync(20));
            await first.CommandReceived.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(await engine.SetBassAsync("max"));

            first.EndNotifications.TrySetResult(true);
            await firstCommandCleanupWaiting.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(secondCreated.Task.IsCompleted);
            Assert.False(first.Disposed.Task.IsCompleted);

            allowFirstCommandCleanup.TrySetResult(true);

            await first.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await secondPowered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(first.NotificationsStopped.Task.IsCompleted);
            Assert.NotNull(stateAtFirstDispose);
            Assert.False(stateAtFirstDispose!.Connected);
            Assert.Equal(0, activeReadersAtFirstDispose);
            Assert.Equal(0, activeReadersAtSecondCreation);

            Assert.True(await engine.SetVolumeAsync(30));
            await second.CommandReceived.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { "volume" }, first.Commands.ToArray());
            Assert.Equal(new[] { "volume" }, second.Commands.ToArray());
            Assert.Equal(1, engine.MaxConcurrentCommandDrainReaders);
            Assert.Equal(new[] { true, false, true }, Compress(connectedStates));
        }
        finally
        {
            allowFirstCommandCleanup.TrySetResult(true);
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task StartIsIdempotentAndCannotRestartAfterStop()
    {
        var client = new FakeBraviaClient();
        var createdClients = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) =>
            {
                Interlocked.Increment(ref createdClients);
                return client;
            },
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        engine.Start();
        var originalCompletion = engine.Completion;
        await client.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        engine.Start();

        Assert.Same(originalCompletion, engine.Completion);
        Assert.Equal(1, Volatile.Read(ref createdClients));

        await engine.StopAsync();

        Assert.Equal(0, engine.ActiveCommandDrainReaders);
        Assert.Equal(1, engine.MaxConcurrentCommandDrainReaders);
        Assert.Throws<ObjectDisposedException>(() => engine.Start());
    }

    [Fact]
    public async Task ThrowingStateAndLogSubscribersCannotInterruptTeardown()
    {
        var client = new FakeBraviaClient();
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct))
        {
            LogAction = _ => throw new InvalidOperationException("logger failed")
        };

        engine.StateChanged += _ => throw new InvalidOperationException("subscriber failed");

        try
        {
            engine.Start();
            await client.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            client.EndNotifications.TrySetResult(true);

            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(engine.CurrentState.Connected);
            Assert.True(client.NotificationsStopped.Task.IsCompleted);
            Assert.Equal(0, engine.ActiveCommandDrainReaders);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task ReconnectWaitsForSnapshotAndKeepaliveCleanup()
    {
        var first = new FakeBraviaClient();
        var second = new FakeBraviaClient();
        var stateCallsStarted = new[] { NewSignal(), NewSignal() };
        var stateCallsCanceled = new[] { NewSignal(), NewSignal() };
        var cleanupWaiting = new[] { NewSignal(), NewSignal() };
        var allowCleanup = NewSignal();
        var secondCreated = NewSignal();
        var stateCallCount = 0;
        var keepaliveDelayCount = 0;
        var clientCount = 0;

        first.StateHandler = async (_, ct) =>
        {
            var index = Interlocked.Increment(ref stateCallCount) - 1;
            Assert.InRange(index, 0, 1);
            stateCallsStarted[index].TrySetResult(true);
            using var registration = ct.Register(() => stateCallsCanceled[index].TrySetResult(true));
            await stateCallsCanceled[index].Task;
            cleanupWaiting[index].TrySetResult(true);
            await allowCleanup.Task;
            ct.ThrowIfCancellationRequested();
            return new Dictionary<string, object?>();
        };

        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) =>
            {
                var index = Interlocked.Increment(ref clientCount);
                if (index == 1) return first;
                if (index == 2)
                {
                    secondCreated.TrySetResult(true);
                    return second;
                }

                throw new InvalidOperationException("Unexpected extra reconnect.");
            },
            (delay, ct) =>
            {
                if (delay == TimeSpan.FromSeconds(25)
                    && Interlocked.Increment(ref keepaliveDelayCount) == 1)
                    return Task.CompletedTask;

                return delay == TimeSpan.FromSeconds(25)
                    ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                    : Task.CompletedTask;
            });

        try
        {
            engine.Start();
            await Task.WhenAll(stateCallsStarted.Select(signal => signal.Task)).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            first.EndNotifications.TrySetResult(true);
            await Task.WhenAll(cleanupWaiting.Select(signal => signal.Task)).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(first.Disposed.Task.IsCompleted);
            Assert.False(secondCreated.Task.IsCompleted);

            allowCleanup.TrySetResult(true);

            await first.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await secondCreated.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(first.NotificationsStopped.Task.IsCompleted);
            Assert.Equal(2, Volatile.Read(ref stateCallCount));
        }
        finally
        {
            allowCleanup.TrySetResult(true);
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task KeepaliveNoAudioClearsCodecAndChannelAndQueriesChannel()
    {
        var client = new FakeBraviaClient();
        var keepaliveApplied = NewSignal();
        var keepaliveDelayCount = 0;
        var stateCallCount = 0;
        string[]? keepalivePaths = null;

        client.StateHandler = (paths, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref stateCallCount);
            if (call == 1)
            {
                return Task.FromResult(new Dictionary<string, object?>
                {
                    ["power"] = true,
                    ["playback_control.audio_format"] = "dolby_atmos_truehd",
                    ["playback_control.audio_channel"] = "7.1.4"
                });
            }

            keepalivePaths = paths.ToArray();
            return Task.FromResult(new Dictionary<string, object?>
            {
                ["power"] = true,
                ["playback_control.audio_format"] = "NoAudio"
            });
        };

        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            (delay, ct) => delay == TimeSpan.FromSeconds(25)
                && Interlocked.Increment(ref keepaliveDelayCount) == 1
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.InfiniteTimeSpan, ct));

        engine.StateChanged += state =>
        {
            if (state.Connected && state.Power && state.Codec == null && state.Channel == null
                && Volatile.Read(ref stateCallCount) >= 2)
                keepaliveApplied.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await keepaliveApplied.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.NotNull(keepalivePaths);
            Assert.Contains("playback_control.audio_channel", keepalivePaths!);
            Assert.Null(engine.CurrentState.Codec);
            Assert.Null(engine.CurrentState.Channel);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task EmptyKeepaliveSignalsConnectionFailureAndReconnects()
    {
        var first = new FakeBraviaClient();
        var second = new FakeBraviaClient();
        var firstStateCall = 0;
        var keepaliveDelayCount = 0;
        var clientCount = 0;
        var secondCreatedAfterFirstDisposed = false;

        first.StateHandler = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref firstStateCall) == 1
                ? new Dictionary<string, object?> { ["power"] = true }
                : new Dictionary<string, object?>());
        };

        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) =>
            {
                var call = Interlocked.Increment(ref clientCount);
                if (call == 1) return first;
                if (call == 2)
                {
                    secondCreatedAfterFirstDisposed = first.Disposed.Task.IsCompleted;
                    return second;
                }

                throw new InvalidOperationException("Unexpected extra reconnect.");
            },
            (delay, ct) =>
            {
                if (delay == TimeSpan.FromSeconds(25)
                    && Interlocked.Increment(ref keepaliveDelayCount) == 1)
                    return Task.CompletedTask;

                return delay == TimeSpan.FromSeconds(25)
                    ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                    : Task.CompletedTask;
            });

        try
        {
            engine.Start();
            await second.Initialized.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(first.Disposed.Task.IsCompleted);
            Assert.True(secondCreatedAfterFirstDisposed);
            Assert.Equal(2, Volatile.Read(ref clientCount));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SnapshotCannotOverwriteNewerDelta()
    {
        var client = new FakeBraviaClient();
        var snapshotStarted = NewSignal();
        var releaseSnapshot = NewSignal();
        var snapshotApplied = NewSignal();

        client.StateHandler = async (_, ct) =>
        {
            snapshotStarted.TrySetResult(true);
            await releaseSnapshot.Task.WaitAsync(ct);
            return new Dictionary<string, object?>
            {
                ["power"] = true,
                ["volume"] = 10
            };
        };

        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        engine.StateChanged += state =>
        {
            if (state.Power) snapshotApplied.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await snapshotStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            engine.ApplyDelta("volume", 20);
            releaseSnapshot.TrySetResult(true);

            await snapshotApplied.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(20, engine.CurrentState.Volume);
        }
        finally
        {
            releaseSnapshot.TrySetResult(true);
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task StaleConnectionGenerationCannotMutateReplacementState()
    {
        var first = new FakeBraviaClient();
        var second = new FakeBraviaClient();
        var firstPowered = NewSignal();
        var secondPowered = NewSignal();
        var clientCount = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => Interlocked.Increment(ref clientCount) == 1 ? first : second,
            static (delay, ct) => delay == TimeSpan.FromSeconds(25)
                ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                : Task.CompletedTask);

        engine.StateChanged += state =>
        {
            if (!state.Connected || !state.Power) return;
            if (Volatile.Read(ref clientCount) == 1) firstPowered.TrySetResult(true);
            else secondPowered.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await firstPowered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            var staleGeneration = engine.ActiveConnectionGeneration;

            first.EndNotifications.TrySetResult(true);
            await secondPowered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            engine.ApplyDeltaForConnection(staleGeneration, "volume", 99);
            engine.ApplySnapshotForConnection(
                new Dictionary<string, object?> { ["volume"] = 88 },
                "Old Bar",
                staleGeneration,
                long.MaxValue);

            Assert.Equal(10, engine.CurrentState.Volume);
            Assert.NotEqual(staleGeneration, engine.ActiveConnectionGeneration);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task OfflineAndPostDisposeCommandsAreRejected()
    {
        var engine = new BraviaEngine(new SonyCredentials());

        Assert.False(await engine.TogglePowerAsync());
        Assert.False(await engine.SetVolumeAsync(25));
        Assert.Equal(SoundbarState.Disconnected, engine.CurrentState);

        await engine.StopAsync();

        Assert.False(await engine.TogglePowerAsync());
        Assert.False(await engine.SetBassAsync("max"));
        Assert.Throws<ObjectDisposedException>(() => engine.Start());
    }

    [Fact]
    public async Task DisposeRequestsAndEventuallyCompletesConnectionCleanup()
    {
        var client = new FakeBraviaClient();
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        engine.Start();
        await client.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        engine.Dispose();

        await engine.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(engine.CurrentState.Connected);
        Assert.Equal(0, engine.ActiveCommandDrainReaders);
        Assert.False(await engine.TogglePowerAsync());
    }

    [Fact]
    public async Task RejectedCommandTearsDownConnectionAndClearsOptimisticState()
    {
        var client = new FakeBraviaClient { CommandResult = false };
        var powered = NewSignal();
        var disconnected = NewSignal();
        var sawConnected = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        engine.StateChanged += state =>
        {
            if (state.Connected)
            {
                Interlocked.Exchange(ref sawConnected, 1);
                if (state.Power) powered.TrySetResult(true);
            }
            else if (Volatile.Read(ref sawConnected) == 1)
            {
                disconnected.TrySetResult(true);
            }
        };

        try
        {
            engine.Start();
            await powered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(await engine.SetVolumeAsync(42));
            await client.CommandReceived.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await disconnected.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(engine.CurrentState.Connected);
            Assert.Equal(0, engine.CurrentState.Volume);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public void DisconnectedAndStandbyUseDifferentPresentationTaxonomy()
    {
        Assert.Equal("idle", SoundbarState.Disconnected.CodecBadgeKind);
        Assert.Equal("Offline", SoundbarState.Disconnected.HumanCodec);

        var standby = SoundbarState.Disconnected with { Connected = true };
        Assert.Equal("standby", standby.CodecBadgeKind);
        Assert.Equal("Standby", standby.HumanCodec);
    }

    private static void ApplyAudioValue(BraviaEngine engine, string path, object? value, bool useSnapshot)
    {
        if (useSnapshot)
        {
            engine.ApplySnapshot(new Dictionary<string, object?> { [path] = value }, "Test Bar");
        }
        else
        {
            engine.ApplyDelta(path, value);
        }
    }

    private static bool[] Compress(IEnumerable<bool> values)
    {
        var result = new List<bool>();
        foreach (var value in values)
        {
            if (result.Count == 0 || result[^1] != value)
            {
                result.Add(value);
            }
        }

        return result.ToArray();
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task StopEngineAsync(BraviaEngine engine)
    {
        try
        {
            await engine.StopAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal sealed class FakeBraviaClient : IBraviaClient
    {
        public Action<string>? LogAction { get; set; }
        public Action? OnDispose { get; set; }
        public Func<CancellationToken, Task>? CommandHandler { get; set; }
        public Func<IEnumerable<string>, CancellationToken, Task<Dictionary<string, object?>>>? StateHandler { get; set; }
        public bool CommandResult { get; set; } = true;

        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource<bool> Initialized { get; } = NewSignal();
        public TaskCompletionSource<bool> CommandReceived { get; } = NewSignal();
        public TaskCompletionSource<bool> EndNotifications { get; } = NewSignal();
        public TaskCompletionSource<bool> NotificationsStarted { get; } = NewSignal();
        public TaskCompletionSource<bool> NotificationsStopped { get; } = NewSignal();
        public TaskCompletionSource<bool> Disposed { get; } = NewSignal();

        /// <summary>When set, InitializeSessionAsync throws this instead of succeeding.</summary>
        public Exception? InitFailure { get; set; }

        public Task InitializeSessionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (InitFailure is not null)
                throw InitFailure;

            Initialized.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, object?>> GetInitialStatesAsync(
            IEnumerable<string> paths,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (StateHandler != null)
                return StateHandler(paths, ct);

            return Task.FromResult(
                new Dictionary<string, object?>
                {
                    ["power"] = true,
                    ["volume"] = 10
                });
        }

        public async Task<bool> ExecCommandAsync(
            string path,
            int? intValue = null,
            bool? boolValue = null,
            string? stringValue = null,
            CancellationToken ct = default)
        {
            Commands.Enqueue(path);
            CommandReceived.TrySetResult(true);

            if (CommandHandler != null)
            {
                await CommandHandler(ct);
            }

            return CommandResult;
        }

        public async IAsyncEnumerable<byte[]> ReadNotificationsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            NotificationsStarted.TrySetResult(true);
            try
            {
                await EndNotifications.Task.WaitAsync(ct);
            }
            finally
            {
                NotificationsStopped.TrySetResult(true);
            }

            yield break;
        }

        public void Dispose()
        {
            OnDispose?.Invoke();
            Disposed.TrySetResult(true);
        }
    }
}
