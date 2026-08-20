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
        "audio_output.stream_info.audio_format",
        "audio_output.stream_info.channel_info",
        "system_setting.application_list"
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
            string deviceName = "Sony BRAVIA Device";

            if (string.IsNullOrEmpty(host))
            {
                var discovered = await MdnsDiscovery.DiscoverAsync(TimeSpan.FromSeconds(6), _cts.Token);
                if (discovered != null)
                {
                    host = discovered.Host;
                    port = discovered.Port;
                    deviceName = discovered.Name;
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
                _client = new BraviaClient(host, port, _credentials);
                await _client.InitializeSessionAsync(_cts.Token);

                var initialStates = await _client.GetInitialStatesAsync(MonitoredPaths, _cts.Token);
                ApplySnapshot(initialStates, deviceName);

                backoffSec = 5; // Reset backoff on successful connect

                using var stream = _client.StartNotifyStream(_cts.Token);

                // Keepalive polling loop in parallel
                _ = Task.Run(() => KeepAlivePollLoopAsync(_cts.Token), _cts.Token);

                while (await stream.ResponseStream.MoveNext(_cts.Token))
                {
                    var msgBytes = stream.ResponseStream.Current;
                    var (path, value) = NotifyParser.ParseNotifyMessage(msgBytes);

                    if (!string.IsNullOrEmpty(path))
                    {
                        ApplyDelta(path, value);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
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
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (!CurrentState.Connected || !CurrentState.Power || _client == null)
                continue;

            try
            {
                var pollPaths = new[]
                {
                    "audio_output.stream_info.audio_format",
                    "audio_output.stream_info.channel_info"
                };
                var pollRes = await _client.GetInitialStatesAsync(pollPaths, ct);
                ApplySnapshot(pollRes, CurrentState.DeviceName);
            }
            catch
            {
                // Silently continue polling
            }
        }
    }

    private void ApplySnapshot(Dictionary<string, object?> snapshot, string? deviceName = null)
    {
        lock (_stateLock)
        {
            bool power = _currentState.Power;
            int volume = _currentState.Volume;
            bool mute = _currentState.Mute;
            bool night = _currentState.NightMode;
            bool sf = _currentState.SoundField;
            string? codec = _currentState.Codec;
            string? channel = _currentState.Channel;

            if (snapshot.TryGetValue("power", out var pVal)) power = ToBool(pVal);
            if (snapshot.TryGetValue("volume", out var vVal)) volume = ToInt(vVal);
            if (snapshot.TryGetValue("mute", out var mVal)) mute = ToBool(mVal);
            if (snapshot.TryGetValue("sound_setting.night_mode", out var nVal)) night = ToBool(nVal);
            if (snapshot.TryGetValue("sound_setting.sound_field", out var sVal)) sf = ToBool(sVal);
            if (snapshot.TryGetValue("audio_output.stream_info.audio_format", out var cVal)) codec = cVal?.ToString();
            if (snapshot.TryGetValue("audio_output.stream_info.channel_info", out var chVal)) channel = chVal?.ToString();

            CurrentState = new SoundbarState
            {
                Connected = true,
                Power = power,
                Volume = volume,
                Mute = mute,
                NightMode = night,
                SoundField = sf,
                Codec = codec,
                Channel = channel,
                DeviceName = deviceName ?? _currentState.DeviceName
            };
        }
    }

    private void ApplyDelta(string path, object? value)
    {
        lock (_stateLock)
        {
            var cur = _currentState;
            switch (path)
            {
                case "power":
                    CurrentState = cur with { Power = ToBool(value) };
                    break;
                case "volume":
                    CurrentState = cur with { Volume = ToInt(value) };
                    break;
                case "mute":
                    CurrentState = cur with { Mute = ToBool(value) };
                    break;
                case "sound_setting.night_mode":
                    CurrentState = cur with { NightMode = ToBool(value) };
                    break;
                case "sound_setting.sound_field":
                case "sound_field":
                    CurrentState = cur with { SoundField = ToBool(value) };
                    break;
                case "audio_output.stream_info.audio_format":
                    CurrentState = cur with { Codec = value?.ToString() };
                    break;
                case "audio_output.stream_info.channel_info":
                    CurrentState = cur with { Channel = value?.ToString() };
                    break;
            }
        }
    }

    public async Task SetVolumeAsync(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (_client != null)
        {
            await _client.ExecCommandAsync("volume", intValue: volume);
            ApplyDelta("volume", volume);
        }
    }

    public async Task VolumeStepAsync(int delta)
    {
        int newVol = Math.Clamp(CurrentState.Volume + delta, 0, 100);
        await SetVolumeAsync(newVol);
    }

    public async Task ToggleMuteAsync()
    {
        if (_client != null)
        {
            bool newMute = !CurrentState.Mute;
            await _client.ExecCommandAsync("mute", boolValue: newMute);
            ApplyDelta("mute", newMute);
        }
    }

    public async Task ToggleNightModeAsync()
    {
        if (_client != null)
        {
            bool newNight = !CurrentState.NightMode;
            await _client.ExecCommandAsync("sound_setting.night_mode", boolValue: newNight);
            ApplyDelta("sound_setting.night_mode", newNight);
        }
    }

    public async Task ToggleSoundFieldAsync()
    {
        if (_client != null)
        {
            bool newSf = !CurrentState.SoundField;
            await _client.ExecCommandAsync("sound_setting.sound_field", boolValue: newSf);
            ApplyDelta("sound_setting.sound_field", newSf);
        }
    }

    public async Task TogglePowerAsync()
    {
        if (_client != null)
        {
            bool newPower = !CurrentState.Power;
            await _client.ExecCommandAsync("power", boolValue: newPower);
            ApplyDelta("power", newPower);
        }
    }

    private static bool ToBool(object? val) => val switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => bool.TryParse(s, out var b) ? b : s == "1",
        _ => false
    };

    private static int ToInt(object? val) => val switch
    {
        int i => i,
        long l => (int)l,
        string s => int.TryParse(s, out var i) ? i : 0,
        _ => 0
    };

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
    }
}
