using System.Text.Json.Serialization;

namespace BraviaTheatre.Core.Auth;

/// <summary>Immutable Sony cloud and local-control credential snapshot.</summary>
public sealed record SonyCredentials
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = SonyOAuth.ClientId;

    /// <summary>Legacy local session identifier retained for existing credential files.</summary>
    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySessionId { get; init; }

    [JsonPropertyName("key_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeyId { get; init; }

    /// <summary>The Sony key id, falling back to the legacy session_id field.</summary>
    [JsonIgnore]
    public string SessionId
    {
        get => !string.IsNullOrWhiteSpace(KeyId) ? KeyId : LegacySessionId ?? string.Empty;
        init => LegacySessionId = value;
    }

    [JsonPropertyName("session_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionKey { get; init; }

    [JsonPropertyName("hmac_key")]
    public string HmacKey { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("session_keys_expires_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SessionKeysExpiresAtUtc { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(DeviceId) &&
        !string.IsNullOrWhiteSpace(SessionId) &&
        !string.IsNullOrWhiteSpace(HmacKey);

    public override string ToString() => nameof(SonyCredentials);
}
