using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace BraviaTheatre.Core.Auth;

public static class SonyOAuth
{
    public const string ClientId = "4f97b8e2-0bb3-45ef-be91-b68e85ca7ee1";
    public const string RedirectUri = "ssh-app://signin";
    public const string ApiKey = "4wTuTmXg3p41yIqIa1TdfMtyejb6s2Mz83Dxv";
    public const string AuthBaseUrl = "https://v1.api.auth.seeds.services";
    public const string IotBaseUrl = "https://v1.api.iot.seeds.services";

    public const string TokenUserAgent = "Dalvik/2.1.0 (Linux; U; Android 13; Pixel 3a Build/TQ3A.230901.001)";
    public const string IotUserAgent = "Phone (Android 13; Pixel 3a) jp.co.sony.hes.home/3.6.3 (18194e34-ed54-4eb8-b488-4ac3bb6b8a8e)";

    public static (string codeVerifier, string codeChallenge) GeneratePkcePair()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var codeVerifier = Base64UrlEncode(bytes);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(hash);

        return (codeVerifier, codeChallenge);
    }

    public static string GenerateOAuthState(int length = 32)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    public static (string authUrl, string codeVerifier, string state) StartOAuthLogin()
    {
        var (codeVerifier, codeChallenge) = GeneratePkcePair();
        var state = GenerateOAuthState();
        var nonce = GenerateOAuthState();

        var claims = "{\"id_token\":{\"idp_identifier\":null},\"userinfo\":{\"idp_identifier\":null}}";

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = ClientId;
        query["redirect_uri"] = RedirectUri;
        query["response_mode"] = "query";
        query["response_type"] = "code";
        query["scope"] = "openid";
        query["claims"] = claims;
        query["country"] = "US";
        query["prompt"] = "login";
        query["state"] = state;
        query["nonce"] = nonce;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";

        var authUrl = $"{AuthBaseUrl}/user/authorize?{query}";
        return (authUrl, codeVerifier, state);
    }

    public static string ParseAuthorizationCode(string redirectOrCode)
    {
        var value = redirectOrCode.Trim();

        // 1. Full URI like ssh-app://signin?code=XYZ&state=... or https://...
        if (value.StartsWith("ssh-app://", StringComparison.OrdinalIgnoreCase) || value.Contains("://"))
        {
            try
            {
                var fixedUri = value.StartsWith("ssh-app://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + value["ssh-app://".Length..]
                    : value;

                var uri = new Uri(fixedUri);
                var query = HttpUtility.ParseQueryString(uri.Query);
                var code = query.Get("code");
                if (!string.IsNullOrEmpty(code))
                    return code.Trim();
            }
            catch
            {
                // Fallback to substring extraction
            }
        }

        // 2. Query string chunk with code=
        if (value.Contains("code="))
        {
            var idx = value.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
            var sub = value[(idx + 5)..];
            var ampIdx = sub.IndexOf('&');
            if (ampIdx >= 0)
                sub = sub[..ampIdx];
            return sub.Trim();
        }

        // 3. User pasted "code&state=..."
        if (value.Contains("&state="))
        {
            var ampIdx = value.IndexOf("&state=", StringComparison.OrdinalIgnoreCase);
            return value[..ampIdx].Trim();
        }

        return value;
    }

    public static async Task<SonyCredentials> CompleteOAuthFlowAsync(
        string redirectOrCode,
        string codeVerifier,
        string? expectedState = null)
    {
        var authCode = ParseAuthorizationCode(redirectOrCode);
        if (string.IsNullOrWhiteSpace(authCode))
            throw new InvalidOperationException("Could not extract a valid authorization code.");

        using var client = new HttpClient();

        // 1. Exchange authorization code for tokens
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{AuthBaseUrl}/token");
        tokenReq.Headers.Add("User-Agent", TokenUserAgent);
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authCode,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = codeVerifier
        });

        var tokenResp = await client.SendAsync(tokenReq);
        var tokenJson = await tokenResp.Content.ReadAsStringAsync();

        if (!tokenResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Sony Token Exchange Failed (HTTP {(int)tokenResp.StatusCode}):\n{tokenJson}\n\n" +
                "Please ensure you copied the fresh 'code' right after signing in."
            );
        }

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var atProp) || string.IsNullOrEmpty(atProp.GetString()))
        {
            throw new InvalidOperationException($"Invalid token response from Sony: {tokenJson}");
        }

        var accessToken = atProp.GetString()!;
        var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        // 2. Fetch IoT devices
        var devReq = new HttpRequestMessage(HttpMethod.Get, $"{IotBaseUrl}/devices");
        devReq.Headers.Add("User-Agent", IotUserAgent);
        devReq.Headers.Add("x-api-key", ApiKey);
        devReq.Headers.Add("Authorization", $"Bearer {accessToken}");

        var devResp = await client.SendAsync(devReq);
        var devJson = await devResp.Content.ReadAsStringAsync();

        if (!devResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Sony Device Query Failed (HTTP {(int)devResp.StatusCode}):\n{devJson}");
        }

        using var devDoc = JsonDocument.Parse(devJson);
        if (!devDoc.RootElement.TryGetProperty("devices", out var devices))
        {
            throw new InvalidOperationException($"Unexpected device list response: {devJson}");
        }

        string? deviceId = null;
        foreach (var dev in devices.EnumerateArray())
        {
            var type = dev.TryGetProperty("device_type", out var dt) ? dt.GetString() : null;
            if (type == "Speaker" || type == "TV" || deviceId == null)
            {
                if (dev.TryGetProperty("device_id", out var dIdProp))
                {
                    deviceId = dIdProp.GetString();
                    if (type == "Speaker") break; // Prefer soundbar
                }
            }
        }

        if (string.IsNullOrEmpty(deviceId))
            throw new InvalidOperationException("No Sony soundbar/speaker or TV found associated with this account.");

        // 3. Fetch gRPC session keys
        var keysReq = new HttpRequestMessage(HttpMethod.Post, $"{IotBaseUrl}/devices/{deviceId}/session_keys");
        keysReq.Headers.Add("User-Agent", IotUserAgent);
        keysReq.Headers.Add("x-api-key", ApiKey);
        keysReq.Headers.Add("Authorization", $"Bearer {accessToken}");

        var keysResp = await client.SendAsync(keysReq);
        var keysJson = await keysResp.Content.ReadAsStringAsync();

        if (!keysResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Sony Session Key Fetch Failed (HTTP {(int)keysResp.StatusCode}):\n{keysJson}");
        }

        using var keysDoc = JsonDocument.Parse(keysJson);

        var sessionId = keysDoc.RootElement.TryGetProperty("session_id", out var sIdProp) ? sIdProp.GetString() : null;
        var hmacKey = keysDoc.RootElement.TryGetProperty("hmac_key", out var hProp) ? hProp.GetString() : null;
        var cId = keysDoc.RootElement.TryGetProperty("client_id", out var cProp) ? cProp.GetString() : ClientId;

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(hmacKey))
        {
            throw new InvalidOperationException($"Invalid session keys response: {keysJson}");
        }

        return new SonyCredentials
        {
            ClientId = cId ?? ClientId,
            SessionId = sessionId,
            HmacKey = hmacKey,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            DeviceId = deviceId
        };
    }
}
