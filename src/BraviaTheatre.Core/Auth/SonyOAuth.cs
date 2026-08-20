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
        if (value.StartsWith("ssh-app://") || (value.Contains("code=") && value.Contains("://")))
        {
            var uri = new Uri(value.Replace("ssh-app://", "https://"));
            var parsed = HttpUtility.ParseQueryString(uri.Query);
            var code = parsed.Get("code");
            if (!string.IsNullOrEmpty(code))
                return code;
        }

        if (value.Contains("code="))
        {
            var parts = value.Split('&');
            foreach (var p in parts)
            {
                if (p.StartsWith("code="))
                    return p["code=".Length..];
            }
        }

        return value;
    }

    public static async Task<SonyCredentials> CompleteOAuthFlowAsync(
        string redirectOrCode,
        string codeVerifier,
        string? expectedState = null)
    {
        var authCode = ParseAuthorizationCode(redirectOrCode);

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
        tokenResp.EnsureSuccessStatusCode();

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Missing access_token");
        var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        // 2. Fetch IoT devices
        var devReq = new HttpRequestMessage(HttpMethod.Get, $"{IotBaseUrl}/devices");
        devReq.Headers.Add("User-Agent", IotUserAgent);
        devReq.Headers.Add("x-api-key", ApiKey);
        devReq.Headers.Add("Authorization", $"Bearer {accessToken}");

        var devResp = await client.SendAsync(devReq);
        devResp.EnsureSuccessStatusCode();

        var devJson = await devResp.Content.ReadAsStringAsync();
        using var devDoc = JsonDocument.Parse(devJson);
        var devices = devDoc.RootElement.GetProperty("devices");

        string? deviceId = null;
        foreach (var dev in devices.EnumerateArray())
        {
            var type = dev.TryGetProperty("device_type", out var dt) ? dt.GetString() : null;
            if (type == "Speaker" || type == "TV" || deviceId == null)
            {
                deviceId = dev.GetProperty("device_id").GetString();
                if (type == "Speaker") break; // Prefer soundbar/speaker
            }
        }

        if (string.IsNullOrEmpty(deviceId))
            throw new InvalidOperationException("No compatible Sony audio devices found on account");

        // 3. Fetch gRPC session keys
        var keysReq = new HttpRequestMessage(HttpMethod.Post, $"{IotBaseUrl}/devices/{deviceId}/session_keys");
        keysReq.Headers.Add("User-Agent", IotUserAgent);
        keysReq.Headers.Add("x-api-key", ApiKey);
        keysReq.Headers.Add("Authorization", $"Bearer {accessToken}");

        var keysResp = await client.SendAsync(keysReq);
        keysResp.EnsureSuccessStatusCode();

        var keysJson = await keysResp.Content.ReadAsStringAsync();
        using var keysDoc = JsonDocument.Parse(keysJson);

        return new SonyCredentials
        {
            ClientId = keysDoc.RootElement.GetProperty("client_id").GetString() ?? ClientId,
            SessionId = keysDoc.RootElement.GetProperty("session_id").GetString() ?? string.Empty,
            HmacKey = keysDoc.RootElement.GetProperty("hmac_key").GetString() ?? string.Empty,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            DeviceId = deviceId
        };
    }
}
