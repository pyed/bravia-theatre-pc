using System;
using System.Collections.Generic;
using System.Threading;
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
        "playback_control.audio_format",
        "playback_control.audio_channel"
    };

    private readonly string? _configuredHost;
    private readonly int _configuredPort;
    private readonly SonyCredentials _credentials;
    private readonly CancellationTokenSource _cts = new();

    private BraviaClient? _client;
    private Task? _workerTask;

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
                            Log($"[Notify] Push event ({msgBytes.Length} B): Path='{path}', Value='{value}'");

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

                // Keepalive polling loop in parallel
                _ = Task.Run(() => KeepAlivePollLoopAsync(_cts.Token), _cts.Token);

                await notifyTask;
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

    private async Task KeepAlivePollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client != null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                if (_client == null || ct.IsCancellationRequested) break;

                var snapshot = await _client.GetInitialStatesAsync(new[] { "power", "volume", "playback_control.audio_format" }, ct);
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
            string? codec = _currentState.Codec;
            string? channel = _currentState.Channel;

            if (snapshot.TryGetValue("power", out var pObj) && pObj != null)
                power = pObj.ToString() == "on" || pObj.ToString() == "active" || pObj.ToString() == "1" || pObj.ToString() == "True";

            if (snapshot.TryGetValue("volume", out var vObj) && vObj != null && int.TryParse(vObj.ToString(), out int v))
                vol = v;

            if (snapshot.TryGetValue("mute", out var mObj) && mObj != null)
                mute = mObj.ToString() == "on" || mObj.ToString() == "1" || mObj.ToString() == "true" || mObj.ToString() == "True";

            if (snapshot.TryGetValue("sound_setting.night_mode", out var nmObj) && nmObj != null)
                nightMode = nmObj.ToString() == "on" || nmObj.ToString() == "1" || nmObj.ToString() == "true" || nmObj.ToString() == "True";

            if (snapshot.TryGetValue("sound_setting.sound_field", out var sfObj) && sfObj != null)
                soundField = sfObj.ToString() == "on" || sfObj.ToString() == "1" || sfObj.ToString() == "true" || sfObj.ToString() == "True";

            if (snapshot.TryGetValue("audio_output.stream_info.audio_format", out var cObj) && cObj != null)
                codec = cObj.ToString();
            else if (snapshot.TryGetValue("playback_control.audio_format", out var pcObj) && pcObj != null)
                codec = pcObj.ToString();

            if (snapshot.TryGetValue("audio_output.stream_info.channel_info", out var chObj) && chObj != null)
                channel = chObj.ToString();
            else if (snapshot.TryGetValue("playback_control.audio_channel", out var pchObj) && pchObj != null)
                channel = pchObj.ToString();

            CurrentState = new SoundbarState
            {
                Connected = true,
                DeviceName = deviceName,
                Power = power,
                Volume = vol,
                Mute = mute,
                SoundField = soundField,
                NightMode = nightMode,
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
            else if (path == "power")
            {
                bool p = value.ToString() == "on" || value.ToString() == "active" || value.ToString() == "1" || value.ToString() == "True";
                cur = cur with { Power = p };
                updated = true;
            }
            else if (path == "mute")
            {
                bool m = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True";
                cur = cur with { Mute = m };
                updated = true;
            }
            else if (path.Contains("night_mode"))
            {
                bool nm = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True";
                cur = cur with { NightMode = nm };
                updated = true;
            }
            else if (path.Contains("sound_field"))
            {
                bool sf = value.ToString() == "on" || value.ToString() == "1" || value.ToString() == "true" || value.ToString() == "True";
                cur = cur with { SoundField = sf };
                updated = true;
            }
            else if (path.Contains("audio_format"))
            {
                cur = cur with { Codec = value.ToString() ?? cur.Codec };
                updated = true;
            }
            else if (path.Contains("channel_info") || path.Contains("audio_channel"))
            {
                cur = cur with { Channel = value.ToString() ?? cur.Channel };
                updated = true;
            }

            if (updated)
            {
                CurrentState = cur;
            }
        }
    }

    public async Task<bool> SetVolumeAsync(int volume)
    {
        if (_client == null) return false;
        try
        {
            await _client.ExecCommandAsync("volume", intValue: volume, ct: _cts.Token);
            ApplyDelta("volume", volume);
            return true;
        }
        catch (Exception ex)
        {
            Log($"SetVolume error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ToggleMuteAsync()
    {
        if (_client == null) return false;
        try
        {
            bool target = !CurrentState.Mute;
            await _client.ExecCommandAsync("mute", boolValue: target, ct: _cts.Token);
            ApplyDelta("mute", target);
            return true;
        }
        catch (Exception ex)
        {
            Log($"ToggleMute error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ToggleNightModeAsync()
    {
        if (_client == null) return false;
        try
        {
            bool target = !CurrentState.NightMode;
            await _client.ExecCommandAsync("sound_setting.night_mode", boolValue: target, ct: _cts.Token);
            ApplyDelta("sound_setting.night_mode", target);
            return true;
        }
        catch (Exception ex)
        {
            Log($"ToggleNightMode error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ToggleSoundFieldAsync()
    {
        if (_client == null) return false;
        try
        {
            bool target = !CurrentState.SoundField;
            await _client.ExecCommandAsync("sound_setting.sound_field", boolValue: target, ct: _cts.Token);
            ApplyDelta("sound_setting.sound_field", target);
            return true;
        }
        catch (Exception ex)
        {
            Log($"ToggleSoundField error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TogglePowerAsync()
    {
        if (_client == null) return false;
        try
        {
            bool target = !CurrentState.Power;
            await _client.ExecCommandAsync("power", boolValue: target, ct: _cts.Token);
            ApplyDelta("power", target);
            return true;
        }
        catch (Exception ex)
        {
            Log($"TogglePower error: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
        _cts.Dispose();
    }
}
