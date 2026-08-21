using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Discovery;
using BraviaTheatre.Core.Models;
using BraviaTheatre.Core.Wire;

namespace BraviaTheatre.Core.Engine;

public sealed class BraviaEngine : IDisposable
{
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

    private Task? _workerTask;
    private int _activeCommandDrainReaders;
    private int _maxConcurrentCommandDrainReaders;

    private readonly Channel<(string path, object value)> _cmdChannel = Channel.CreateUnbounded<(string, object)>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly object _stateLock = new();
    private SoundbarState _currentState = SoundbarState.Disconnected;

    public event Action<SoundbarState>? StateChanged;
    public Action<string>? LogAction { get; set; }

    public SoundbarState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
        private set
        {
            lock (_stateLock) _currentState = value;
            StateChanged?.Invoke(value);
        }
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

    private void Log(string msg) => LogAction?.Invoke($"[Engine] {msg}");

    public void Start()
    {
        _workerTask = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync()
    {
        int backoffSec = 5;

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

            try
            {
                Log($"Connecting gRPC channel to {host}:{port}...");
                client = _clientFactory(host, port, _credentials);
                client.LogAction = Log;
                await client.InitializeSessionAsync(connectionCts.Token);
                Log("Full security handshake completed (ConfirmSignin + ConfirmKeys).");

                // Set initial connected state
                lock (_stateLock)
                {
                    _currentState = _currentState with { Connected = true, DeviceName = deviceName };
                    StateChanged?.Invoke(_currentState);
                }

                var notifyTask = NotifyLoopAsync(client, connectionCts.Token);
                connectionTasks.Add(notifyTask);
                Log("Live notify stream started.");

                var initialSnapshotTask = InitialSnapshotAsync(client, deviceName, connectionCts.Token);
                connectionTasks.Add(initialSnapshotTask);

                backoffSec = 5; // Reset backoff on successful connect

                // Start command drain loop and keepalive loop
                var cmdDrainTask = CommandDrainLoopAsync(client, connectionCts.Token);
                connectionTasks.Add(cmdDrainTask);

                var keepAliveTask = KeepAlivePollLoopAsync(client, deviceName, connectionCts.Token);
                connectionTasks.Add(keepAliveTask);

                await Task.WhenAny(notifyTask, cmdDrainTask);
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
                connectionCts.Cancel();
                SetDisconnectedState();
                await AwaitConnectionTasksAsync(connectionTasks, connectionCts.Token);
                SetDisconnectedState();

                try
                {
                    client?.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"Client dispose warning: {ex.Message}");
                }
            }

            if (!_cts.Token.IsCancellationRequested)
            {
                await _delayAsync(TimeSpan.FromSeconds(backoffSec), _cts.Token);
                backoffSec = Math.Min(backoffSec * 2, 60);
            }
        }
    }

    internal Task Completion => _workerTask ?? Task.CompletedTask;
    internal int ActiveCommandDrainReaders => Volatile.Read(ref _activeCommandDrainReaders);
    internal int MaxConcurrentCommandDrainReaders => Volatile.Read(ref _maxConcurrentCommandDrainReaders);

    private async Task NotifyLoopAsync(IBraviaClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var msgBytes in client.ReadNotificationsAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var (path, value) = NotifyParser.ParseNotifyMessage(msgBytes);

                if (!string.IsNullOrEmpty(path))
                {
                    ApplyDelta(path, value);
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

    private async Task InitialSnapshotAsync(IBraviaClient client, string deviceName, CancellationToken ct)
    {
        try
        {
            var initialStates = await client.GetInitialStatesAsync(MonitoredPaths, ct);
            ct.ThrowIfCancellationRequested();
            Log($"Snapshot received ({initialStates.Count} paths). Applying state...");
            ApplySnapshot(initialStates, deviceName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Initial snapshot warning: {ex.Message}");
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

    private void SetDisconnectedState()
    {
        SoundbarState? disconnectedState = null;

        lock (_stateLock)
        {
            if (_currentState != SoundbarState.Disconnected)
            {
                _currentState = SoundbarState.Disconnected;
                disconnectedState = _currentState;
            }
        }

        if (disconnectedState != null)
        {
            StateChanged?.Invoke(disconnectedState);
        }
    }

    private async Task CommandDrainLoopAsync(IBraviaClient client, CancellationToken ct)
    {
        var activeReaders = Interlocked.Increment(ref _activeCommandDrainReaders);
        UpdateMaximum(ref _maxConcurrentCommandDrainReaders, activeReaders);

        try
        {
            await CommandDrainCoreAsync(client, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCommandDrainReaders);
        }
    }

    private async Task CommandDrainCoreAsync(IBraviaClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await _cmdChannel.Reader.WaitToReadAsync(ct))
                {
                    while (_cmdChannel.Reader.TryRead(out var cmd))
                    {
                        var (path, value) = cmd;

                        // Coalesce rapid volume updates to the latest one
                        if (path == "volume")
                        {
                            while (_cmdChannel.Reader.TryPeek(out var next) && next.path == "volume")
                            {
                                _cmdChannel.Reader.TryRead(out cmd);
                                value = cmd.value;
                            }
                        }

                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            if (value is int intVal)
                            {
                                await client.ExecCommandAsync(path, intValue: intVal, ct: ct);
                            }
                            else if (value is bool boolVal)
                            {
                                await client.ExecCommandAsync(path, boolValue: boolVal, ct: ct);
                            }
                            else if (value is string strVal)
                            {
                                await client.ExecCommandAsync(path, stringValue: strVal, ct: ct);
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log($"Command '{path}' exec error: {ex.Message}");
                        }
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
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task KeepAlivePollLoopAsync(IBraviaClient client, string deviceName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delayAsync(TimeSpan.FromSeconds(25), ct);
                ct.ThrowIfCancellationRequested();

                var snapshot = await client.GetInitialStatesAsync(new[] { "power", "volume", "sound_setting.volume.bass", "sound_setting.voice_mode", "playback_control.function", "playback_control.audio_format" }, ct);
                ct.ThrowIfCancellationRequested();
                ApplySnapshot(snapshot, deviceName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Engine loop will handle reconnect if needed
            }
        }
    }

    internal void ApplySnapshot(Dictionary<string, object?> snapshot, string deviceName)
    {
        lock (_stateLock)
        {
            bool power = _currentState.Power;
            int vol = _currentState.Volume;
            bool mute = _currentState.Mute;
            bool soundField = _currentState.SoundField;
            bool nightMode = _currentState.NightMode;
            bool voiceMode = _currentState.VoiceMode;
            string bass = _currentState.Bass;
            int rearLevel = _currentState.RearLevel;
            string function = _currentState.Function;
            string? codec = _currentState.Codec;
            string? channel = _currentState.Channel;

            if (snapshot.TryGetValue("power", out var pObj) && pObj != null)
                power = pObj.ToString() == "on" || pObj.ToString() == "active" || pObj.ToString() == "1" || pObj.ToString() == "True" || (pObj is bool pb && pb);

            if (snapshot.TryGetValue("volume", out var vObj) && vObj != null && int.TryParse(vObj.ToString(), out int v))
                vol = v;

            if (snapshot.TryGetValue("mute", out var mObj) && mObj != null)
                mute = mObj.ToString() == "on" || mObj.ToString() == "1" || mObj.ToString() == "true" || mObj.ToString() == "True" || (mObj is bool mb && mb);

            if (snapshot.TryGetValue("sound_setting.night_mode", out var nmObj) && nmObj != null)
                nightMode = nmObj.ToString() == "on" || nmObj.ToString() == "1" || nmObj.ToString() == "true" || nmObj.ToString() == "True" || (nmObj is bool nmb && nmb);

            if (snapshot.TryGetValue("sound_setting.sound_field", out var sfObj) && sfObj != null)
                soundField = sfObj.ToString() == "on" || sfObj.ToString() == "1" || sfObj.ToString() == "true" || sfObj.ToString() == "True" || (sfObj is bool sfb && sfb);

            if (snapshot.TryGetValue("sound_setting.voice_mode", out var vmObj) && vmObj != null)
                voiceMode = vmObj.ToString() == "on" || vmObj.ToString() == "1" || vmObj.ToString() == "true" || vmObj.ToString() == "True" || (vmObj is bool vmb && vmb);

            if (snapshot.TryGetValue("sound_setting.volume.bass", out var bObj) && bObj != null)
            {
                var bStr = bObj.ToString()?.ToLowerInvariant();
                if (bStr == "min" || bStr == "mid" || bStr == "max")
                    bass = bStr;
            }

            if (snapshot.TryGetValue("sound_setting.volume.rear", out var rObj) && rObj != null && int.TryParse(rObj.ToString(), out int r))
                rearLevel = r;

            if (snapshot.TryGetValue("playback_control.function", out var fObj) && fObj != null)
            {
                var fn = fObj.ToString();
                if (!string.IsNullOrEmpty(fn) && fn != "unknown" && fn != "none")
                    function = fn;
            }

            if (snapshot.TryGetValue("audio_output.stream_info.audio_format", out var cObj))
            {
                codec = NormalizeAudioValue(cObj);
            }
            else if (snapshot.TryGetValue("playback_control.audio_format", out var pcObj))
            {
                codec = NormalizeAudioValue(pcObj);
            }

            if (snapshot.TryGetValue("audio_output.stream_info.channel_info", out var chObj))
            {
                channel = NormalizeAudioValue(chObj);
            }
            else if (snapshot.TryGetValue("playback_control.audio_channel", out var pchObj))
            {
                channel = NormalizeAudioValue(pchObj);
            }

            CurrentState = new SoundbarState
            {
                Connected = true,
                DeviceName = deviceName,
                Power = power,
                Volume = vol,
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
        }
    }

    internal void ApplyDelta(string path, object? value)
    {
        lock (_stateLock)
        {
            var cur = _currentState;
            bool updated = false;

            if (path == "playback_control.audio_format" || path == "audio_output.stream_info.audio_format")
            {
                var codec = NormalizeAudioValue(value);
                if (cur.Codec != codec)
                {
                    cur = cur with { Codec = codec };
                    updated = true;
                }
            }
            else if (path == "playback_control.audio_channel" || path == "audio_output.stream_info.channel_info")
            {
                var channel = NormalizeAudioValue(value);
                if (cur.Channel != channel)
                {
                    cur = cur with { Channel = channel };
                    updated = true;
                }
            }
            else if (value == null)
            {
                return;
            }
            else if (path == "volume" && int.TryParse(value.ToString(), out int v))
            {
                cur = cur with { Volume = v };
                updated = true;
            }
            else if (path == "power" || path == "playback_control.power")
            {
                bool p = value.ToString() == "on" || value.ToString() == "active" || value.ToString() == "1" || value.ToString() == "True" || (value is bool b && b);
                cur = cur with { Power = p };
                updated = true;
            }
            else if (path == "mute")
            {
                bool m = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True" || (value is bool b && b);
                cur = cur with { Mute = m };
                updated = true;
            }
            else if (path == "sound_setting.night_mode")
            {
                bool nm = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True" || (value is bool b && b);
                cur = cur with { NightMode = nm };
                updated = true;
            }
            else if (path == "sound_setting.sound_field")
            {
                bool sf = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True" || (value is bool b && b);
                cur = cur with { SoundField = sf };
                updated = true;
            }
            else if (path == "sound_setting.voice_mode")
            {
                bool vm = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True" || (value is bool b && b);
                cur = cur with { VoiceMode = vm };
                updated = true;
            }
            else if (path == "sound_setting.volume.bass")
            {
                var bStr = value.ToString()?.ToLowerInvariant();
                if (bStr == "min" || bStr == "mid" || bStr == "max")
                {
                    cur = cur with { Bass = bStr };
                    updated = true;
                }
            }
            else if (path == "sound_setting.volume.rear" && int.TryParse(value.ToString(), out int r))
            {
                cur = cur with { RearLevel = r };
                updated = true;
            }
            else if (path == "playback_control.function")
            {
                var fn = value.ToString();
                if (!string.IsNullOrEmpty(fn) && fn != "unknown" && fn != "none")
                {
                    cur = cur with { Function = fn };
                    updated = true;
                }
            }
            if (updated)
            {
                CurrentState = cur;
            }
        }
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
        lock (_stateLock)
        {
            if (!_currentState.Power || _currentState.Volume == volume) return Task.FromResult(false);
            _currentState = _currentState with { Volume = volume, Mute = false };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("volume", volume));
        return Task.FromResult(true);
    }

    public Task<bool> SetBassAsync(string level)
    {
        level = level.ToLowerInvariant();
        if (level != "min" && level != "mid" && level != "max") return Task.FromResult(false);

        lock (_stateLock)
        {
            if (!_currentState.Power || _currentState.Bass == level) return Task.FromResult(false);
            _currentState = _currentState with { Bass = level };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("sound_setting.volume.bass", level));
        return Task.FromResult(true);
    }

    public Task<bool> SetFunctionAsync(string function)
    {
        function = function.ToLowerInvariant();
        lock (_stateLock)
        {
            if (!_currentState.Power || _currentState.Function == function) return Task.FromResult(false);
            _currentState = _currentState with { Function = function };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("playback_control.function", function));
        return Task.FromResult(true);
    }

    public Task<bool> SetRearLevelAsync(int level)
    {
        level = Math.Clamp(level, -10, 10);
        lock (_stateLock)
        {
            if (!_currentState.Power || _currentState.RearLevel == level) return Task.FromResult(false);
            _currentState = _currentState with { RearLevel = level };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("sound_setting.volume.rear", level));
        return Task.FromResult(true);
    }

    public Task<bool> ToggleMuteAsync()
    {
        bool target;
        lock (_stateLock)
        {
            if (!_currentState.Power) return Task.FromResult(false);
            target = !_currentState.Mute;
            _currentState = _currentState with { Mute = target };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("mute", target));
        return Task.FromResult(true);
    }

    public Task<bool> ToggleNightModeAsync()
    {
        bool target;
        lock (_stateLock)
        {
            if (!_currentState.Power) return Task.FromResult(false);
            target = !_currentState.NightMode;
            _currentState = _currentState with { NightMode = target };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("sound_setting.night_mode", target));
        return Task.FromResult(true);
    }

    public Task<bool> ToggleSoundFieldAsync()
    {
        bool target;
        lock (_stateLock)
        {
            if (!_currentState.Power) return Task.FromResult(false);
            target = !_currentState.SoundField;
            _currentState = _currentState with { SoundField = target };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("sound_setting.sound_field", target));
        return Task.FromResult(true);
    }

    public Task<bool> ToggleVoiceModeAsync()
    {
        bool target;
        lock (_stateLock)
        {
            if (!_currentState.Power) return Task.FromResult(false);
            target = !_currentState.VoiceMode;
            _currentState = _currentState with { VoiceMode = target };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("sound_setting.voice_mode", target));
        return Task.FromResult(true);
    }

    public Task<bool> TogglePowerAsync()
    {
        bool target;
        lock (_stateLock)
        {
            target = !_currentState.Power;
            _currentState = _currentState with { Power = target };
            if (target) _currentState = _currentState with { Mute = false };
            StateChanged?.Invoke(_currentState);
        }
        _cmdChannel.Writer.TryWrite(("power", target));
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch { }
    }
}
