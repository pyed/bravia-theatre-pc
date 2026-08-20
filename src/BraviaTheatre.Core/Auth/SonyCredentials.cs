using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BraviaTheatre.Core.Auth;

public sealed class SonyCredentials
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("hmac_key")]
    public string HmacKey { get; set; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    public static SonyCredentials? LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SonyCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    public void SaveToFile(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
