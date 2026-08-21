using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace BraviaTheatre.Core.Auth;

public sealed record SonyOAuthCallback(string Code, string State);

public sealed record SonyDeviceInfo(string DeviceId, string DisplayName, string DeviceType);

public delegate Task<string?> SonyDeviceSelector(
    IReadOnlyList<SonyDeviceInfo> devices,
    CancellationToken cancellationToken);

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

    public static SonyOAuthCallback ParseAuthorizationCallback(string callbackUri)
    {
        var normalizedCallback = callbackUri?.Trim() ?? string.Empty;
        if (!normalizedCallback.StartsWith("ssh-app://signin?", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(normalizedCallback, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ssh-app", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("signin", StringComparison.OrdinalIgnoreCase) ||
            (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Paste the complete Sony callback URL beginning with ssh-app://signin?.");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var codeValues = query.GetValues("code");
        var stateValues = query.GetValues("state");
        if (codeValues is not { Length: 1 } || string.IsNullOrWhiteSpace(codeValues[0]))
            throw new InvalidOperationException("The Sony callback does not contain one valid authorization code.");
        if (stateValues is not { Length: 1 } || string.IsNullOrWhiteSpace(stateValues[0]))
            throw new InvalidOperationException("The Sony callback does not contain the required security state.");

        return new SonyOAuthCallback(codeValues[0].Trim(), stateValues[0].Trim());
    }

    public static async Task<SonyCredentials> CompleteOAuthFlowAsync(
        string callbackUri,
        string codeVerifier,
        string? expectedState,
        CancellationToken cancellationToken = default,
        SonyDeviceSelector? deviceSelector = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(codeVerifier))
            throw new InvalidOperationException("The OAuth login session is not initialized.");
        if (string.IsNullOrWhiteSpace(expectedState))
            throw new InvalidOperationException("The OAuth security state is missing. Start sign-in again.");

        var callback = ParseAuthorizationCallback(callbackUri);
        if (!FixedTimeEquals(callback.State, expectedState))
            throw new InvalidOperationException("The OAuth callback security state does not match. Start sign-in again.");

        var ownsClient = httpClient == null;
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var accessToken = await ExchangeAuthorizationCodeAsync(
                client,
                callback.Code,
                codeVerifier,
                cancellationToken).ConfigureAwait(false);

            var devices = await GetDevicesAsync(client, accessToken, cancellationToken).ConfigureAwait(false);
            var deviceId = await SelectDeviceAsync(devices, deviceSelector, cancellationToken).ConfigureAwait(false);
            return await FetchSessionKeysAsync(client, accessToken, deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    /// <summary>
    /// Exchanges a refresh token supplied by the caller and rotates the local session keys.
    /// The refresh token is never returned or persisted by this method.
    /// </summary>
    public static async Task<SonyCredentials> RefreshSessionKeysAsync(
        string refreshToken,
        string deviceId,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("A refresh token is required.", nameof(refreshToken));
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("A Sony device ID is required.", nameof(deviceId));

        var ownsClient = httpClient == null;
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBaseUrl}/token");
            request.Headers.Add("User-Agent", TokenUserAgent);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId
            });

            var accessToken = await SendForAccessTokenAsync(client, request, "refresh", cancellationToken).ConfigureAwait(false);
            return await FetchSessionKeysAsync(client, accessToken, deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    private static async Task<string> ExchangeAuthorizationCodeAsync(
        HttpClient client,
        string authorizationCode,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBaseUrl}/token");
        request.Headers.Add("User-Agent", TokenUserAgent);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = codeVerifier
        });

        return await SendForAccessTokenAsync(client, request, "authorization", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> SendForAccessTokenAsync(
        HttpClient client,
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateHttpFailure($"Sony token {operation}", response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("access_token", out var value) &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Converted to a sanitized protocol error below.
        }

        throw new InvalidOperationException("Sony returned an invalid token response. No credentials were stored.");
    }

    private static async Task<IReadOnlyList<SonyDeviceInfo>> GetDevicesAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateIotRequest(HttpMethod.Get, $"{IotBaseUrl}/devices", accessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateHttpFailure("Sony device query", response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("devices", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new JsonException();

            var result = new List<SonyDeviceInfo>();
            foreach (var item in array.EnumerateArray())
            {
                var id = GetString(item, "device_id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var type = GetString(item, "device_type") ?? "Sony device";
                var name = GetString(item, "device_name") ?? GetString(item, "name") ?? GetString(item, "model_name") ?? type;
                result.Add(new SonyDeviceInfo(id, name, type));
            }
            return result;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Sony returned an invalid device list. No credentials were stored.");
        }
    }

    private static async Task<string> SelectDeviceAsync(
        IReadOnlyList<SonyDeviceInfo> devices,
        SonyDeviceSelector? selector,
        CancellationToken cancellationToken)
    {
        var preferred = devices
            .Where(device => device.DeviceType.Equals("Speaker", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var candidates = preferred.Length > 0
            ? preferred
            : devices.Where(device => device.DeviceType.Equals("TV", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) candidates = devices.ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("No Sony soundbar, speaker, or TV was found on this account.");
        if (candidates.Length == 1) return candidates[0].DeviceId;
        if (selector == null)
            throw new InvalidOperationException("Multiple Sony devices were found; select a device to continue.");

        var selection = await selector(candidates, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selection) || candidates.All(device => device.DeviceId != selection))
            throw new OperationCanceledException("Sony device selection was cancelled.", cancellationToken);
        return selection;
    }

    private static async Task<SonyCredentials> FetchSessionKeysAsync(
        HttpClient client,
        string accessToken,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var request = CreateIotRequest(
            HttpMethod.Post,
            $"{IotBaseUrl}/devices/{Uri.EscapeDataString(deviceId)}/session_keys",
            accessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateHttpFailure("Sony session-key request", response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var sessionId = GetString(root, "session_id") ?? GetString(root, "key_id");
            var hmacKey = GetString(root, "hmac_key") ?? GetString(root, "session_key");
            var clientId = GetString(root, "client_id") ?? ClientId;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(hmacKey))
                throw new JsonException();

            return new SonyCredentials
            {
                ClientId = clientId,
                SessionId = sessionId,
                HmacKey = hmacKey,
                DeviceId = deviceId
            };
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Sony returned an invalid session-key response. No credentials were stored.");
        }
    }

    private static HttpRequestMessage CreateIotRequest(HttpMethod method, string uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("User-Agent", IotUserAgent);
        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        return request;
    }

    private static InvalidOperationException CreateHttpFailure(string operation, HttpResponseMessage response)
    {
        var retry = (int)response.StatusCode is 408 or 429 or >= 500;
        var guidance = retry ? "Try again in a moment." : "Start sign-in again and verify the selected Sony account.";
        return new InvalidOperationException($"{operation} failed (HTTP {(int)response.StatusCode}). {guidance}");
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
