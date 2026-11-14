using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BraviaTheatre.Core.Engine;

namespace BraviaTheatre.Core.Tv;

/// <summary>What one look at the TV said.</summary>
public enum TvPowerReading
{
    /// <summary>The TV answered and reported <c>PowerState</c> "on".</summary>
    On,

    /// <summary>The TV answered with anything else, or did not answer at all.</summary>
    NotOn
}

/// <summary>
/// Reads a Samsung Tizen TV's power state from its unauthenticated device-info endpoint
/// (<c>http://host:8001/api/v2/</c>, field <c>device.PowerState</c>).
/// </summary>
public sealed class SamsungTvProbe : IDisposable
{
    public const int Port = 8001;

    private readonly HttpClient _http;
    private readonly Uri _address;

    public SamsungTvProbe(string host)
    {
        if (!ControlEndpoint.TryNormalizeHost(host, out var normalized))
            throw new ArgumentException("The TV host must be a host name or IP address without a scheme, port, or path.", nameof(host));

        var authority = normalized.Contains(':') ? $"[{normalized}]" : normalized;
        _address = new Uri($"http://{authority}:{Port.ToString(CultureInfo.InvariantCulture)}/api/v2/");

        // A TV that is off with network standby disabled drops off the LAN; a short connect
        // timeout keeps the poll interval honest instead of waiting on the OS default.
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1.5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task<TvPowerReading> ReadAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(_address, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return TvPowerReading.NotOn;
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParsePowerState(json);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return TvPowerReading.NotOn;
        }
    }

    /// <summary>
    /// Only an explicit "on" counts. A set in standby keeps answering, reports "standby", and
    /// after a while an empty string; reading anything but "on" as on wakes the soundbar for a
    /// TV that is off. Sets too old to report the field are treated as not on for the same reason.
    /// </summary>
    internal static TvPowerReading ParsePowerState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("device", out var device)
                && device.ValueKind == JsonValueKind.Object
                && device.TryGetProperty("PowerState", out var power)
                && power.ValueKind == JsonValueKind.String
                && string.Equals(power.GetString()?.Trim(), "on", StringComparison.OrdinalIgnoreCase)
                    ? TvPowerReading.On
                    : TvPowerReading.NotOn;
        }
        catch (JsonException)
        {
            return TvPowerReading.NotOn;
        }
    }

    public void Dispose() => _http.Dispose();
}
