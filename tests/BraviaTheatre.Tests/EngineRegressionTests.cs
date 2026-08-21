using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;

namespace BraviaTheatre.Tests;

public class EngineRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

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
            await connected.Task.WaitAsync(TestTimeout);

            client.EndNotifications.TrySetResult(true);

            await disconnected.Task.WaitAsync(TestTimeout);
            await client.Disposed.Task.WaitAsync(TestTimeout);

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
            await firstPowered.Task.WaitAsync(TestTimeout);

            Assert.True(await engine.SetVolumeAsync(20));
            await first.CommandReceived.Task.WaitAsync(TestTimeout);

            first.EndNotifications.TrySetResult(true);
            await firstCommandCleanupWaiting.Task.WaitAsync(TestTimeout);

            Assert.False(secondCreated.Task.IsCompleted);
            Assert.False(first.Disposed.Task.IsCompleted);

            allowFirstCommandCleanup.TrySetResult(true);

            await first.Disposed.Task.WaitAsync(TestTimeout);
            await secondPowered.Task.WaitAsync(TestTimeout);

            Assert.True(first.NotificationsStopped.Task.IsCompleted);
            Assert.NotNull(stateAtFirstDispose);
            Assert.False(stateAtFirstDispose!.Connected);
            Assert.Equal(0, activeReadersAtFirstDispose);
            Assert.Equal(0, activeReadersAtSecondCreation);

            Assert.True(await engine.SetVolumeAsync(30));
            await second.CommandReceived.Task.WaitAsync(TestTimeout);

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
        engine.Dispose();

        try
        {
            await engine.Completion.WaitAsync(TestTimeout);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakeBraviaClient : IBraviaClient
    {
        public Action<string>? LogAction { get; set; }
        public Action? OnDispose { get; set; }
        public Func<CancellationToken, Task>? CommandHandler { get; set; }

        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource<bool> Initialized { get; } = NewSignal();
        public TaskCompletionSource<bool> CommandReceived { get; } = NewSignal();
        public TaskCompletionSource<bool> EndNotifications { get; } = NewSignal();
        public TaskCompletionSource<bool> NotificationsStarted { get; } = NewSignal();
        public TaskCompletionSource<bool> NotificationsStopped { get; } = NewSignal();
        public TaskCompletionSource<bool> Disposed { get; } = NewSignal();

        public Task InitializeSessionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Initialized.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, object?>> GetInitialStatesAsync(
            IEnumerable<string> paths,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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

            return true;
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
