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

public sealed class BraviaEngine : IDisposable
{
    private static readonly string[] MonitoredPaths =
    {
        "power",
        "volume",
        "mute",
        "sound_setting.night_mode",
        "sound_setting.sound_field",
        "sound_setting.volume.bass",
        "playback_control.audio_format",
        "playback_control.audio_channel"
    };

    private readonly string? _configuredHost;
    private readonly int _configuredPort;
    private readonly SonyCredentials _credentials;
    private readonly CancellationTokenSource _cts = new();

    private BraviaClient? _client;
    private Task? _workerTask;

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
    {
        _credentials = credentials;
        _configuredHost = host;
        _configuredPort = port;
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

            try
            {
                Log($"Connecting gRPC channel to {host}:{port}...");
                _client = new BraviaClient(host, port, _credentials)
                {
                    LogAction = Log
                };
                await _client.InitializeSessionAsync(_cts.Token);
                Log("Full security handshake completed (ConfirmSignin + ConfirmKeys).");

                // Start notify stream immediately
                using var notifyStream = _client.StartNotifyStream(_cts.Token);
                Log("Live notify stream started.");

                // Set initial connected state
                lock (_stateLock)
                {
                    _currentState = _currentState with { Connected = true, DeviceName = deviceName };
                    StateChanged?.Invoke(_currentState);
                }

                // Read notify stream on dedicated background loop
                var notifyTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await notifyStream.ResponseStream.MoveNext(_cts.Token))
                        {
                            var msgBytes = notifyStream.ResponseStream.Current;
                            var (path, value) = NotifyParser.ParseNotifyMessage(msgBytes);

                            if (!string.IsNullOrEmpty(path))
                            {
                                ApplyDelta(path, value);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Notify stream ended: {ex.Message}");
                    }
                }, _cts.Token);

                // Fetch initial states in parallel
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var initialStates = await _client.GetInitialStatesAsync(MonitoredPaths, _cts.Token);
                        Log($"Snapshot received ({initialStates.Count} paths). Applying state...");
                        ApplySnapshot(initialStates, deviceName);
                    }
                    catch (Exception ex)
                    {
                        Log($"Initial snapshot warning: {ex.Message}");
                    }
                }, _cts.Token);

                backoffSec = 5; // Reset backoff on successful connect

                // Start command drain loop and keepalive loop
                var cmdDrainTask = Task.Run(() => CommandDrainLoopAsync(_cts.Token), _cts.Token);
                _ = Task.Run(() => KeepAlivePollLoopAsync(_cts.Token), _cts.Token);

                await Task.WhenAny(notifyTask, cmdDrainTask);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Connection/Stream error: {ex.Message}");
                CurrentState = SoundbarState.Disconnected;
            }
            finally
            {
                _client?.Dispose();
                _client = null;
            }

            if (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSec), _cts.Token);
                backoffSec = Math.Min(backoffSec * 2, 60);
            }
        }
    }

    private async Task CommandDrainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client != null)
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

                        if (_client == null || ct.IsCancellationRequested) break;

                        try
                        {
                            if (value is int intVal)
                            {
                                await _client.ExecCommandAsync(path, intValue: intVal, ct: ct);
                            }
                            else if (value is bool boolVal)
                            {
                                await _client.ExecCommandAsync(path, boolValue: boolVal, ct: ct);
                            }
                            else if (value is string strVal)
                            {
                                await _client.ExecCommandAsync(path, stringValue: strVal, ct: ct);
                            }
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

    private async Task KeepAlivePollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client != null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                if (_client == null || ct.IsCancellationRequested) break;

                var snapshot = await _client.GetInitialStatesAsync(new[] { "power", "volume", "sound_setting.volume.bass", "playback_control.audio_format" }, ct);
                ApplySnapshot(snapshot, CurrentState.DeviceName ?? "BRAVIA Theatre");
            }
            catch
            {
                // Engine loop will handle reconnect if needed
            }
        }
    }

    private void ApplySnapshot(Dictionary<string, object?> snapshot, string deviceName)
    {
        lock (_stateLock)
        {
            bool power = _currentState.Power;
            int vol = _currentState.Volume;
            bool mute = _currentState.Mute;
            bool soundField = _currentState.SoundField;
            bool nightMode = _currentState.NightMode;
            string bass = _currentState.Bass;
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

            if (snapshot.TryGetValue("sound_setting.volume.bass", out var bObj) && bObj != null)
            {
                var bStr = bObj.ToString()?.ToLowerInvariant();
                if (bStr == "min" || bStr == "mid" || bStr == "max")
                    bass = bStr;
            }

            if (snapshot.TryGetValue("audio_output.stream_info.audio_format", out var cObj) && cObj != null)
            {
                var format = cObj.ToString();
                if (!string.IsNullOrEmpty(format) && format != "unknown" && format != "none" && format != "NoAudio")
                    codec = format;
            }
            else if (snapshot.TryGetValue("playback_control.audio_format", out var pcObj) && pcObj != null)
            {
                var format = pcObj.ToString();
                if (!string.IsNullOrEmpty(format) && format != "unknown" && format != "none" && format != "NoAudio")
                    codec = format;
            }

            if (snapshot.TryGetValue("audio_output.stream_info.channel_info", out var chObj) && chObj != null)
            {
                var ch = chObj.ToString();
                if (!string.IsNullOrEmpty(ch) && ch != "unknown" && ch != "none")
                    channel = ch;
            }
            else if (snapshot.TryGetValue("playback_control.audio_channel", out var pchObj) && pchObj != null)
            {
                var ch = pchObj.ToString();
                if (!string.IsNullOrEmpty(ch) && ch != "unknown" && ch != "none")
                    channel = ch;
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
                Bass = bass,
                Codec = codec,
                Channel = channel
            };
        }
    }

    private void ApplyDelta(string path, object? value)
    {
        if (value == null) return;
        lock (_stateLock)
        {
            var cur = _currentState;
            bool updated = false;

            if (path == "volume" && int.TryParse(value.ToString(), out int v))
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
            else if (path == "sound_setting.volume.bass")
            {
                var bStr = value.ToString()?.ToLowerInvariant();
                if (bStr == "min" || bStr == "mid" || bStr == "max")
                {
                    cur = cur with { Bass = bStr };
                    updated = true;
                }
            }
            else if (path == "playback_control.audio_format" || path == "audio_output.stream_info.audio_format")
            {
                var format = value.ToString();
                if (!string.IsNullOrEmpty(format) && format != "unknown" && format != "none" && format != "NoAudio")
                {
                    cur = cur with { Codec = format };
                    updated = true;
                }
            }
            else if (path == "playback_control.audio_channel" || path == "audio_output.stream_info.channel_info")
            {
                var ch = value.ToString();
                if (!string.IsNullOrEmpty(ch) && ch != "unknown" && ch != "none")
                {
                    cur = cur with { Channel = ch };
                    updated = true;
                }
            }

            if (updated)
            {
                CurrentState = cur;
            }
        }
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
        _cts.Cancel();
        _client?.Dispose();
        _cts.Dispose();
    }
}
