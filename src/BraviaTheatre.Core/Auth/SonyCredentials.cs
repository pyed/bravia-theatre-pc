using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BraviaTheatre.Core.Auth;

public sealed class SonyCredentials
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "4f97b8e2-0bb3-45ef-be91-b68e85ca7ee1";

    [JsonPropertyName("session_id")]
    public string? SessionIdRaw { get; set; }

    [JsonPropertyName("key_id")]
    public string? KeyIdRaw { get; set; }

    [JsonIgnore]
    public string SessionId
    {
        get => !string.IsNullOrEmpty(SessionIdRaw) ? SessionIdRaw : (KeyIdRaw ?? string.Empty);
        set => SessionIdRaw = value;
    }

    [JsonPropertyName("hmac_key")]
    public string? HmacKeyRaw { get; set; }

    [JsonPropertyName("session_key")]
    public string? SessionKeyRaw { get; set; }

    [JsonIgnore]
    public string HmacKey
    {
        get => !string.IsNullOrEmpty(HmacKeyRaw) ? HmacKeyRaw : (SessionKeyRaw ?? string.Empty);
        set => HmacKeyRaw = value;
    }

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
            var creds = JsonSerializer.Deserialize<SonyCredentials>(json);
            if (creds != null && (!string.IsNullOrEmpty(creds.SessionId)) && (!string.IsNullOrEmpty(creds.HmacKey)))
                return creds;
            return creds;
        }
        catch
        {
            return null;
        }
    }

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }

        // Ensure canonical fields are set
        SessionIdRaw ??= SessionId;
        KeyIdRaw ??= SessionId;
        HmacKeyRaw ??= HmacKey;
        SessionKeyRaw ??= HmacKey;

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
