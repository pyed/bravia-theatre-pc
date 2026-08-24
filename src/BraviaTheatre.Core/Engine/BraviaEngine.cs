using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Discovery;
using BraviaTheatre.Core.Models;
using BraviaTheatre.Core.Wire;
using Grpc.Core;

namespace BraviaTheatre.Core.Engine;

public sealed class BraviaEngine : IDisposable, IAsyncDisposable
{
    private readonly record struct QueuedCommand(long Generation, string Path, object Value);

    private static readonly string[] MonitoredPaths =
    {
        "power",
        "volume",
        "mute",
        "sound_setting.night_mode",
        "sound_setting.sound_field",
        "sound_setting.voice_mode",
        "sound_setting.volume.bass",
        "sound_setting.volume.rear",
        "playback_control.function",
        "playback_control.audio_format",
        "playback_control.audio_channel"
    };

    private readonly string? _configuredHost;
    private readonly int _configuredPort;
    private readonly SonyCredentials _credentials;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, int, SonyCredentials, IBraviaClient> _clientFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    private readonly object _lifecycleLock = new();
    private Task? _workerTask;
    private int _disposeRequested;
    private int _resourcesDisposed;
    private int _activeCommandDrainReaders;
    private int _maxConcurrentCommandDrainReaders;
    private long _nextConnectionGeneration;

    private readonly Channel<QueuedCommand> _cmdChannel = Channel.CreateUnbounded<QueuedCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly object _stateLock = new();
    private SoundbarState _currentState = SoundbarState.Disconnected;
    private long _activeConnectionGeneration;
    private long _stateRevision;
    private readonly Dictionary<string, long> _fieldRevisions = new();

    public event Action<SoundbarState>? StateChanged;
    public Action<string>? LogAction { get; set; }

    public SoundbarState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    public BraviaEngine(SonyCredentials credentials, string? host = null, int port = 55051)
        : this(
            credentials,
            host,
            port,
            static (clientHost, clientPort, clientCredentials) => new BraviaClient(clientHost, clientPort, clientCredentials),
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal BraviaEngine(
        SonyCredentials credentials,
        string? host,
        int port,
        Func<string, int, SonyCredentials, IBraviaClient> clientFactory,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _credentials = credentials;
        _configuredHost = host;
        _configuredPort = port;
        _clientFactory = clientFactory;
        _delayAsync = delayAsync;
    }

    private void Log(string msg)
    {
        try
        {
            LogAction?.Invoke($"[Engine] {msg}");
        }
        catch
        {
            // Logging must never affect engine lifecycle or cleanup.
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            _workerTask ??= Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await WorkerLoopCoreAsync();
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Engine worker stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            SetDisconnectedState();
        }
    }

    private async Task WorkerLoopCoreAsync()
    {
        int backoffSec = 5;

        // Sticky across retry attempts once a handshake proves auth is required;
        // cleared only by a successful authenticated handshake.
        bool authRequired = false;

        while (!_cts.Token.IsCancellationRequested)
        {
            string host = _configuredHost ?? string.Empty;
            int port = _configuredPort;
            string deviceName = "BRAVIA Theatre Bar 9";

            if (string.IsNullOrEmpty(host))
            {
                Log("Discovering soundbar on local network (mDNS + LAN probe)...");
                var discovered = await MdnsDiscovery.DiscoverAsync(TimeSpan.FromSeconds(6), _cts.Token);
                if (discovered != null)
                {
                    host = discovered.Host;
                    port = discovered.Port;
                    deviceName = discovered.Name;
                    Log($"Discovered soundbar at {host}:{port} ({deviceName})");
                }
                else
                {
                    Log("Discovery returned no devices. Retrying...");
                }
            }

            if (string.IsNullOrEmpty(host))
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSec), _cts.Token);
                backoffSec = Math.Min(backoffSec * 2, 60);
                continue;
            }

            IBraviaClient? client = null;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var connectionTasks = new List<Task>();
            var connectionGeneration = Interlocked.Increment(ref _nextConnectionGeneration);
            var connectionFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                Log($"Connecting gRPC channel to {host}:{port}...");
                client = _clientFactory(host, port, _credentials);
                client.LogAction = Log;

                try
                {
                    await client.InitializeSessionAsync(connectionCts.Token);
                    authRequired = false;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
                {
                    if (_credentials.IsValid)
                    {
                        authRequired = true;
                        Log($"Authentication required: {ex.Message}");
                    }

                    throw;
                }

                Log("Full security handshake completed (ConfirmSignin + ConfirmKeys).");

                ActivateConnection(connectionGeneration, deviceName);

                var notifyTask = NotifyLoopAsync(client, connectionGeneration, connectionCts.Token);
                connectionTasks.Add(notifyTask);
                Log("Live notify stream started.");

                var initialSnapshotTask = InitialSnapshotAsync(
                    client,
                    deviceName,
                    connectionGeneration,
                    connectionFailure,
                    connectionCts.Token);
                connectionTasks.Add(initialSnapshotTask);

                backoffSec = 5; // Reset backoff on successful connect

                // Start command drain loop and keepalive loop
                var cmdDrainTask = CommandDrainLoopAsync(client, connectionGeneration, connectionCts.Token);
                connectionTasks.Add(cmdDrainTask);

                var keepAliveTask = KeepAlivePollLoopAsync(client, deviceName, connectionGeneration, connectionCts.Token);
                connectionTasks.Add(keepAliveTask);

                await Task.WhenAny(notifyTask, cmdDrainTask, keepAliveTask, connectionFailure.Task);
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Connection/Stream error: {ex.Message}");
            }
            finally
            {
                try
                {
                    connectionCts.Cancel();
                }
                catch (Exception ex)
                {
                    Log($"Connection cancellation warning: {ex.Message}");
                }

                SetDisconnectedState(connectionGeneration, authRequired);

                try
                {
                    await AwaitConnectionTasksAsync(connectionTasks, connectionCts.Token);
                }
                finally
                {
                    try
                    {
                        client?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log($"Client dispose warning: {ex.Message}");
                    }
                }
            }

            if (!_cts.Token.IsCancellationRequested)
            {
                await _delayAsync(TimeSpan.FromSeconds(backoffSec), _cts.Token);
                backoffSec = Math.Min(backoffSec * 2, 60);
            }
        }
    }

    internal Task Completion
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _workerTask ?? Task.CompletedTask;
            }
        }
    }

    internal int ActiveCommandDrainReaders => Volatile.Read(ref _activeCommandDrainReaders);
    internal int MaxConcurrentCommandDrainReaders => Volatile.Read(ref _maxConcurrentCommandDrainReaders);
    internal long ActiveConnectionGeneration
    {
        get { lock (_stateLock) return _activeConnectionGeneration; }
    }

    private async Task NotifyLoopAsync(IBraviaClient client, long connectionGeneration, CancellationToken ct)
    {
        try
        {
            await foreach (var msgBytes in client.ReadNotificationsAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var (path, value) = NotifyParser.ParseNotifyMessage(msgBytes);

                if (!string.IsNullOrEmpty(path))
                {
                    ApplyDeltaForConnection(connectionGeneration, path, value);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Notify stream ended: {ex.Message}");
        }
    }

    private async Task InitialSnapshotAsync(
        IBraviaClient client,
        string deviceName,
        long connectionGeneration,
        TaskCompletionSource<bool> connectionFailure,
        CancellationToken ct)
    {
        try
        {
            var baselineRevision = CaptureSnapshotRevision(connectionGeneration);
            if (baselineRevision == null) return;

            var initialStates = await client.GetInitialStatesAsync(MonitoredPaths, ct);
            ct.ThrowIfCancellationRequested();

            if (initialStates.Count == 0)
            {
                Log("Initial snapshot returned no monitored state.");
                connectionFailure.TrySetResult(true);
                return;
            }

            Log($"Snapshot received ({initialStates.Count} paths). Applying state...");
            ApplySnapshotForConnection(initialStates, deviceName, connectionGeneration, baselineRevision.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Initial snapshot warning: {ex.Message}");
            connectionFailure.TrySetResult(true);
        }
    }

    private async Task AwaitConnectionTasksAsync(IReadOnlyCollection<Task> tasks, CancellationToken connectionToken)
    {
        if (tasks.Count == 0) return;

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Connection worker cleanup warning: {ex.Message}");
        }
    }

    private void ActivateConnection(long connectionGeneration, string deviceName)
    {
        SoundbarState? connectedState = null;

        lock (_stateLock)
        {
            _activeConnectionGeneration = connectionGeneration;
            _fieldRevisions.Clear();
            _stateRevision++;

            var next = _currentState with { Connected = true, DeviceName = deviceName, AuthRequired = false };
            if (next != _currentState)
            {
                _currentState = next;
                connectedState = next;
            }
        }

        if (connectedState != null)
        {
            PublishState(connectedState);
        }
    }

    private void SetDisconnectedState(long? connectionGeneration = null, bool authRequired = false)
    {
        SoundbarState? disconnectedState = null;

        lock (_stateLock)
        {
            // Never clobber a state owned by a different, still-active connection,
            // but a connection that never activated (e.g. handshake rejected)
            // must still be able to publish the auth-required flag.
            if (connectionGeneration.HasValue
                && _activeConnectionGeneration != 0
                && _activeConnectionGeneration != connectionGeneration.Value)
                return;

            _activeConnectionGeneration = 0;
            _fieldRevisions.Clear();
            _stateRevision++;

            var disconnected = SoundbarState.Disconnected with { AuthRequired = authRequired };
            if (_currentState != disconnected)
            {
                _currentState = disconnected;
                disconnectedState = _currentState;
            }
        }

        if (disconnectedState != null)
        {
            PublishState(disconnectedState);
        }
    }

    private long? CaptureSnapshotRevision(long connectionGeneration)
    {
        lock (_stateLock)
        {
            return _activeConnectionGeneration == connectionGeneration
                ? _stateRevision
                : null;
        }
    }

    private void PublishState(SoundbarState state)
    {
        lock (_stateLock)
        {
            if (_currentState != state)
                return;
        }

        var handlers = StateChanged;
        if (handlers == null) return;

        foreach (Action<SoundbarState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                Log($"StateChanged subscriber warning: {ex.Message}");
            }
        }
    }

    private async Task CommandDrainLoopAsync(IBraviaClient client, long connectionGeneration, CancellationToken ct)
    {
        var activeReaders = Interlocked.Increment(ref _activeCommandDrainReaders);
        UpdateMaximum(ref _maxConcurrentCommandDrainReaders, activeReaders);

        try
        {
            await CommandDrainCoreAsync(client, connectionGeneration, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCommandDrainReaders);
        }
    }

    private async Task CommandDrainCoreAsync(IBraviaClient client, long connectionGeneration, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _cmdChannel.Reader.WaitToReadAsync(ct))
                    break;

                while (_cmdChannel.Reader.TryRead(out var cmd))
                {
                    if (cmd.Generation != connectionGeneration)
                        continue;

                    var path = cmd.Path;
                    var value = cmd.Value;

                    // Coalesce rapid volume updates from this connection to the latest one.
                    if (path == "volume")
                    {
                        while (_cmdChannel.Reader.TryPeek(out var next)
                            && next.Generation == connectionGeneration
                            && next.Path == "volume")
                        {
                            _cmdChannel.Reader.TryRead(out cmd);
                            value = cmd.Value;
                        }
                    }

                    if (ct.IsCancellationRequested || !IsConnectionActive(connectionGeneration))
                        break;

                    try
                    {
                        bool succeeded;
                        if (value is int intVal)
                        {
                            succeeded = await client.ExecCommandAsync(path, intValue: intVal, ct: ct);
                        }
                        else if (value is bool boolVal)
                        {
                            succeeded = await client.ExecCommandAsync(path, boolValue: boolVal, ct: ct);
                        }
                        else if (value is string strVal)
                        {
                            succeeded = await client.ExecCommandAsync(path, stringValue: strVal, ct: ct);
                        }
                        else
                        {
                            Log($"Command '{path}' has an unsupported value type.");
                            return;
                        }

                        if (!succeeded)
                        {
                            Log($"Command '{path}' was rejected by the device.");
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Command '{path}' exec error: {ex.Message}");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Command drain error: {ex.Message}");
                return;
            }
        }
    }

    private bool IsConnectionActive(long connectionGeneration)
    {
        lock (_stateLock)
        {
            return _activeConnectionGeneration == connectionGeneration;
        }
    }

    private async Task KeepAlivePollLoopAsync(
        IBraviaClient client,
        string deviceName,
        long connectionGeneration,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delayAsync(TimeSpan.FromSeconds(25), ct);
                ct.ThrowIfCancellationRequested();

                var baselineRevision = CaptureSnapshotRevision(connectionGeneration);
                if (baselineRevision == null) return;

                var snapshot = await client.GetInitialStatesAsync(MonitoredPaths, ct);
                ct.ThrowIfCancellationRequested();

                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Keepalive returned no monitored state.");

                ApplySnapshotForConnection(snapshot, deviceName, connectionGeneration, baselineRevision.Value);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Keepalive poll failed: {ex.Message}");
                throw;
            }
        }
    }

    internal void ApplySnapshot(Dictionary<string, object?> snapshot, string deviceName) =>
        ApplySnapshotCore(snapshot, deviceName, null, long.MaxValue);

    internal void ApplySnapshotForConnection(
        Dictionary<string, object?> snapshot,
        string deviceName,
        long connectionGeneration,
        long baselineRevision) =>
        ApplySnapshotCore(snapshot, deviceName, connectionGeneration, baselineRevision);

    private void ApplySnapshotCore(
        Dictionary<string, object?> snapshot,
        string deviceName,
        long? connectionGeneration,
        long baselineRevision)
    {
        SoundbarState? stateToPublish = null;

        lock (_stateLock)
        {
            if (connectionGeneration.HasValue
                && _activeConnectionGeneration != connectionGeneration.Value)
                return;

            var cur = _currentState;
            bool power = cur.Power;
            int volume = cur.Volume;
            bool mute = cur.Mute;
            bool soundField = cur.SoundField;
            bool nightMode = cur.NightMode;
            bool voiceMode = cur.VoiceMode;
            string bass = cur.Bass;
            int rearLevel = cur.RearLevel;
            string function = cur.Function;
            string? codec = cur.Codec;
            string? channel = cur.Channel;
            var appliedFields = new List<string>();

            bool CanApply(string field) => baselineRevision == long.MaxValue
                || !_fieldRevisions.TryGetValue(field, out var revision)
                || revision <= baselineRevision;

            if (snapshot.TryGetValue("power", out var powerValue)
                && powerValue != null
                && CanApply(nameof(SoundbarState.Power)))
            {
                power = ParseBoolean(powerValue);
                appliedFields.Add(nameof(SoundbarState.Power));
            }

            if (snapshot.TryGetValue("volume", out var volumeValue)
                && volumeValue != null
                && int.TryParse(volumeValue.ToString(), out var parsedVolume)
                && CanApply(nameof(SoundbarState.Volume)))
            {
                volume = parsedVolume;
                appliedFields.Add(nameof(SoundbarState.Volume));
            }

            if (snapshot.TryGetValue("mute", out var muteValue)
                && muteValue != null
                && CanApply(nameof(SoundbarState.Mute)))
            {
                mute = ParseBoolean(muteValue);
                appliedFields.Add(nameof(SoundbarState.Mute));
            }

            if (snapshot.TryGetValue("sound_setting.night_mode", out var nightValue)
                && nightValue != null
                && CanApply(nameof(SoundbarState.NightMode)))
            {
                nightMode = ParseBoolean(nightValue);
                appliedFields.Add(nameof(SoundbarState.NightMode));
            }

            if (snapshot.TryGetValue("sound_setting.sound_field", out var fieldValue)
                && fieldValue != null
                && CanApply(nameof(SoundbarState.SoundField)))
            {
                soundField = ParseBoolean(fieldValue);
                appliedFields.Add(nameof(SoundbarState.SoundField));
            }

            if (snapshot.TryGetValue("sound_setting.voice_mode", out var voiceValue)
                && voiceValue != null
                && CanApply(nameof(SoundbarState.VoiceMode)))
            {
                voiceMode = ParseBoolean(voiceValue);
                appliedFields.Add(nameof(SoundbarState.VoiceMode));
            }

            if (snapshot.TryGetValue("sound_setting.volume.bass", out var bassValue)
                && bassValue != null
                && CanApply(nameof(SoundbarState.Bass)))
            {
                var normalizedBass = bassValue.ToString()?.Trim().ToLowerInvariant();
                if (normalizedBass is "min" or "mid" or "max")
                {
                    bass = normalizedBass;
                    appliedFields.Add(nameof(SoundbarState.Bass));
                }
            }

            if (snapshot.TryGetValue("sound_setting.volume.rear", out var rearValue)
                && rearValue != null
                && int.TryParse(rearValue.ToString(), out var parsedRear)
                && CanApply(nameof(SoundbarState.RearLevel)))
            {
                rearLevel = parsedRear;
                appliedFields.Add(nameof(SoundbarState.RearLevel));
            }

            if (snapshot.TryGetValue("playback_control.function", out var functionValue)
                && functionValue != null
                && CanApply(nameof(SoundbarState.Function)))
            {
                var normalizedFunction = functionValue.ToString()?.Trim();
                if (!string.IsNullOrEmpty(normalizedFunction)
                    && !normalizedFunction.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    && !normalizedFunction.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    function = normalizedFunction;
                    appliedFields.Add(nameof(SoundbarState.Function));
                }
            }

            bool codecPresent = snapshot.TryGetValue("audio_output.stream_info.audio_format", out var codecValue)
                || snapshot.TryGetValue("playback_control.audio_format", out codecValue);
            bool noAudioApplied = false;

            if (codecPresent && CanApply(nameof(SoundbarState.Codec)))
            {
                var normalizedCodec = NormalizeAudioValue(codecValue);
                if (normalizedCodec != null)
                {
                    codec = normalizedCodec;
                    appliedFields.Add(nameof(SoundbarState.Codec));
                }
                else if (CanApply(nameof(SoundbarState.Channel)))
                {
                    codec = null;
                    channel = null;
                    noAudioApplied = true;
                    appliedFields.Add(nameof(SoundbarState.Codec));
                    appliedFields.Add(nameof(SoundbarState.Channel));
                }
            }

            bool channelPresent = snapshot.TryGetValue("audio_output.stream_info.channel_info", out var channelValue)
                || snapshot.TryGetValue("playback_control.audio_channel", out channelValue);

            if (channelPresent && !noAudioApplied && CanApply(nameof(SoundbarState.Channel)))
            {
                channel = NormalizeAudioValue(channelValue);
                appliedFields.Add(nameof(SoundbarState.Channel));
            }

            MarkFieldsLocked(appliedFields);

            var next = cur with
            {
                Connected = true,
                DeviceName = deviceName,
                Power = power,
                Volume = volume,
                Mute = mute,
                SoundField = soundField,
                NightMode = nightMode,
                VoiceMode = voiceMode,
                Bass = bass,
                RearLevel = rearLevel,
                Function = function,
                Codec = codec,
                Channel = channel
            };

            if (next != cur)
            {
                _currentState = next;
                stateToPublish = next;
            }
        }

        if (stateToPublish != null)
            PublishState(stateToPublish);
    }

    internal void ApplyDelta(string path, object? value) => ApplyDeltaCore(null, path, value);

    internal void ApplyDeltaForConnection(long connectionGeneration, string path, object? value) =>
        ApplyDeltaCore(connectionGeneration, path, value);

    private void ApplyDeltaCore(long? connectionGeneration, string path, object? value)
    {
        SoundbarState? stateToPublish = null;

        lock (_stateLock)
        {
            if (connectionGeneration.HasValue
                && _activeConnectionGeneration != connectionGeneration.Value)
                return;

            var cur = _currentState;
            var next = cur;
            var updatedFields = new List<string>();

            if (path is "playback_control.audio_format" or "audio_output.stream_info.audio_format")
            {
                var codec = NormalizeAudioValue(value);
                next = codec == null
                    ? cur with { Codec = null, Channel = null }
                    : cur with { Codec = codec };
                updatedFields.Add(nameof(SoundbarState.Codec));
                if (codec == null) updatedFields.Add(nameof(SoundbarState.Channel));
            }
            else if (path is "playback_control.audio_channel" or "audio_output.stream_info.channel_info")
            {
                next = cur with { Channel = NormalizeAudioValue(value) };
                updatedFields.Add(nameof(SoundbarState.Channel));
            }
            else if (value == null)
            {
                return;
            }
            else if (path == "volume" && int.TryParse(value.ToString(), out var volume))
            {
                next = cur with { Volume = volume };
                updatedFields.Add(nameof(SoundbarState.Volume));
            }
            else if (path is "power" or "playback_control.power")
            {
                next = cur with { Power = ParseBoolean(value) };
                updatedFields.Add(nameof(SoundbarState.Power));
            }
            else if (path == "mute")
            {
                next = cur with { Mute = ParseBoolean(value) };
                updatedFields.Add(nameof(SoundbarState.Mute));
            }
            else if (path == "sound_setting.night_mode")
            {
                next = cur with { NightMode = ParseBoolean(value) };
                updatedFields.Add(nameof(SoundbarState.NightMode));
            }
            else if (path == "sound_setting.sound_field")
            {
                next = cur with { SoundField = ParseBoolean(value) };
                updatedFields.Add(nameof(SoundbarState.SoundField));
            }
            else if (path == "sound_setting.voice_mode")
            {
                next = cur with { VoiceMode = ParseBoolean(value) };
                updatedFields.Add(nameof(SoundbarState.VoiceMode));
            }
            else if (path == "sound_setting.volume.bass")
            {
                var bass = value.ToString()?.Trim().ToLowerInvariant();
                if (bass is not ("min" or "mid" or "max")) return;
                next = cur with { Bass = bass };
                updatedFields.Add(nameof(SoundbarState.Bass));
            }
            else if (path == "sound_setting.volume.rear" && int.TryParse(value.ToString(), out var rear))
            {
                next = cur with { RearLevel = rear };
                updatedFields.Add(nameof(SoundbarState.RearLevel));
            }
            else if (path == "playback_control.function")
            {
                var function = value.ToString()?.Trim();
                if (string.IsNullOrEmpty(function)
                    || function.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    || function.Equals("none", StringComparison.OrdinalIgnoreCase))
                    return;

                next = cur with { Function = function };
                updatedFields.Add(nameof(SoundbarState.Function));
            }
            else
            {
                return;
            }

            MarkFieldsLocked(updatedFields);
            if (next != cur)
            {
                _currentState = next;
                stateToPublish = next;
            }
        }

        if (stateToPublish != null)
            PublishState(stateToPublish);
    }

    private void MarkFieldsLocked(IEnumerable<string> fields)
    {
        long? revision = null;
        foreach (var field in fields)
        {
            revision ??= ++_stateRevision;
            _fieldRevisions[field] = revision.Value;
        }
    }

    private static bool ParseBoolean(object value)
    {
        if (value is bool boolean) return boolean;
        var text = value.ToString()?.Trim();
        return text is not null
            && (text.Equals("on", StringComparison.OrdinalIgnoreCase)
                || text.Equals("active", StringComparison.OrdinalIgnoreCase)
                || text == "1"
                || text.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static string? NormalizeAudioValue(object? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        return text.Equals("NoAudio", StringComparison.OrdinalIgnoreCase)
            || text.Equals("NoChannel", StringComparison.OrdinalIgnoreCase)
            || text.Equals("none", StringComparison.OrdinalIgnoreCase)
            || text.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || text.Equals("invalid", StringComparison.OrdinalIgnoreCase)
                ? null
                : text;
    }

    public Task<bool> SetVolumeAsync(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        return Task.FromResult(TryQueueCommand(
            "volume",
            state => !state.Power || state.Volume == volume
                ? null
                : (state with { Volume = volume, Mute = false }, volume),
            nameof(SoundbarState.Volume),
            nameof(SoundbarState.Mute)));
    }

    public Task<bool> SetBassAsync(string level)
    {
        if (string.IsNullOrWhiteSpace(level)) return Task.FromResult(false);
        level = level.Trim().ToLowerInvariant();
        if (level != "min" && level != "mid" && level != "max") return Task.FromResult(false);

        return Task.FromResult(TryQueueCommand(
            "sound_setting.volume.bass",
            state => !state.Power || state.Bass == level
                ? null
                : (state with { Bass = level }, level),
            nameof(SoundbarState.Bass)));
    }

    public Task<bool> SetFunctionAsync(string function)
    {
        if (string.IsNullOrWhiteSpace(function)) return Task.FromResult(false);
        function = function.Trim().ToLowerInvariant();
        return Task.FromResult(TryQueueCommand(
            "playback_control.function",
            state => !state.Power || state.Function == function
                ? null
                : (state with { Function = function }, function),
            nameof(SoundbarState.Function)));
    }

    public Task<bool> SetRearLevelAsync(int level)
    {
        level = Math.Clamp(level, -10, 10);
        return Task.FromResult(TryQueueCommand(
            "sound_setting.volume.rear",
            state => !state.Power || state.RearLevel == level
                ? null
                : (state with { RearLevel = level }, level),
            nameof(SoundbarState.RearLevel)));
    }

    public Task<bool> ToggleMuteAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "mute",
            state => !state.Power
                ? null
                : (state with { Mute = !state.Mute }, (object)!state.Mute),
            nameof(SoundbarState.Mute)));
    }

    public Task<bool> ToggleNightModeAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "sound_setting.night_mode",
            state => !state.Power
                ? null
                : (state with { NightMode = !state.NightMode }, (object)!state.NightMode),
            nameof(SoundbarState.NightMode)));
    }

    public Task<bool> ToggleSoundFieldAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "sound_setting.sound_field",
            state => !state.Power
                ? null
                : (state with { SoundField = !state.SoundField }, (object)!state.SoundField),
            nameof(SoundbarState.SoundField)));
    }

    public Task<bool> ToggleVoiceModeAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "sound_setting.voice_mode",
            state => !state.Power
                ? null
                : (state with { VoiceMode = !state.VoiceMode }, (object)!state.VoiceMode),
            nameof(SoundbarState.VoiceMode)));
    }

    public Task<bool> TogglePowerAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "power",
            state =>
            {
                var target = !state.Power;
                return (state with { Power = target, Mute = target ? false : state.Mute }, (object)target);
            },
            nameof(SoundbarState.Power),
            nameof(SoundbarState.Mute)));
    }

    private bool TryQueueCommand(
        string path,
        Func<SoundbarState, (SoundbarState State, object Value)?> transition,
        params string[] updatedFields)
    {
        SoundbarState? stateToPublish;

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposeRequested) != 0
                || _activeConnectionGeneration == 0
                || !_currentState.Connected)
                return false;

            var transitionResult = transition(_currentState);
            if (transitionResult == null || transitionResult.Value.State == _currentState)
                return false;

            var command = new QueuedCommand(
                _activeConnectionGeneration,
                path,
                transitionResult.Value.Value);

            if (!_cmdChannel.Writer.TryWrite(command))
                return false;

            _currentState = transitionResult.Value.State;
            MarkFieldsLocked(updatedFields);
            stateToPublish = _currentState;
        }

        PublishState(stateToPublish);
        return true;
    }

    private void BeginStop()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        _cmdChannel.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (Exception ex)
        {
            Log($"Engine cancellation warning: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        BeginStop();

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            FinalizeResources();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        BeginStop();

        var completion = Completion;
        if (completion.IsCompleted)
        {
            _ = completion.Exception;
            FinalizeResources();
        }
        else
        {
            _ = completion.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((BraviaEngine)state!).FinalizeResources();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        GC.SuppressFinalize(this);
    }

    private void FinalizeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        _cts.Dispose();
    }
}
