using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace BraviaTheatre.Core.Auth;

public sealed record SonyOAuthCallback(string Code, string State)
{
    public override string ToString() => nameof(SonyOAuthCallback);
}

public sealed record SonyDeviceInfo(
    string DeviceId, string DisplayName, string DeviceType,
    string? DeviceUniqueId = null, string? ModelName = null)
{
    public override string ToString() => nameof(SonyDeviceInfo);
}

public delegate Task<string?> SonyDeviceSelector(
    IReadOnlyList<SonyDeviceInfo> devices,
    CancellationToken cancellationToken);

public static class SonyOAuth
{
    private static readonly TimeSpan CloudResponseReadTimeout = TimeSpan.FromSeconds(30);

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
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
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
            var tokens = await ExchangeAuthorizationCodeAsync(
                client,
                callback.Code,
                codeVerifier,
                cancellationToken).ConfigureAwait(false);

            var devices = await GetDevicesAsync(client, tokens.AccessToken!, cancellationToken).ConfigureAwait(false);
            var deviceId = await SelectDeviceAsync(devices, deviceSelector, cancellationToken).ConfigureAwait(false);
            var credentials = await FetchSessionKeysAsync(
                client,
                tokens.AccessToken!,
                deviceId,
                tokens.RefreshToken,
                ClientId,
                timeProvider ?? TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
            return credentials with { DeviceUniqueId = devices.Single(device => device.DeviceId == deviceId).DeviceUniqueId };
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    /// <summary>
    /// Exchanges the snapshot's refresh token and returns replacement local session keys.
    /// </summary>
    public static async Task<SonyCredentials> RefreshSessionKeysAsync(
        SonyCredentials currentCredentials,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        Func<string, Task>? checkpointRotatedRefreshTokenAsync = null,
        Action<string>? diagnosticLog = null,
        SonyDeviceSelector? reassociationSelector = null)
    {
        ArgumentNullException.ThrowIfNull(currentCredentials);
        if (string.IsNullOrWhiteSpace(currentCredentials.RefreshToken))
            throw new ArgumentException("A refresh token is required.", nameof(currentCredentials));
        if (string.IsNullOrWhiteSpace(currentCredentials.DeviceId))
            throw new ArgumentException("A Sony device ID is required.", nameof(currentCredentials));

        var ownsClient = httpClient == null;
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var phase = "OAuthRefresh";
        try
        {
            LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; started.");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBaseUrl}/token");
            request.Headers.Add("User-Agent", TokenUserAgent);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = currentCredentials.RefreshToken,
                ["client_id"] = ClientId
            });

            var tokens = await SendForTokenAsync(client, request, "refresh", cancellationToken).ConfigureAwait(false);
            var refreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken)
                ? currentCredentials.RefreshToken
                : tokens.RefreshToken;
            LogRenewalDiagnostic(diagnosticLog,
                $"Phase={phase}; succeeded; RefreshTokenRotated={!string.Equals(refreshToken, currentCredentials.RefreshToken, StringComparison.Ordinal)}.");
            if (checkpointRotatedRefreshTokenAsync != null
                && !string.Equals(refreshToken, currentCredentials.RefreshToken, StringComparison.Ordinal))
            {
                phase = "RotatedTokenCheckpoint";
                await checkpointRotatedRefreshTokenAsync(refreshToken).ConfigureAwait(false);
            }
            phase = "SessionKeys";
            LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; started.");
            SonyCredentials renewed;
            try
            {
                renewed = await FetchSessionKeysAsync(
                    client, tokens.AccessToken!, currentCredentials.DeviceId, refreshToken,
                    currentCredentials.ClientId, timeProvider ?? TimeProvider.System,
                    cancellationToken).ConfigureAwait(false);
                renewed = renewed with { DeviceUniqueId = currentCredentials.DeviceUniqueId };
            }
            catch (SonyOAuthException error) when (error.Kind == SonyOAuthFailureKind.Protocol && error.HttpStatusCode == 404)
            {
                // Only this stored association's 404 enters recovery, once per transaction.
                LogRenewalDiagnostic(diagnosticLog, "Phase=SessionKeys; failed; Classification=Protocol; HTTP=404.");
                phase = "DeviceDiscovery";
                LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; started.");
                var devices = await GetDevicesAsync(client, tokens.AccessToken!, cancellationToken).ConfigureAwait(false);
                var storedPresent = devices.Any(device => device.DeviceId == currentCredentials.DeviceId);
                var candidates = devices.Where(IsCompatibleReplacement).ToArray();
                LogRenewalDiagnostic(diagnosticLog,
                    $"Phase={phase}; succeeded; StoredDevicePresent={storedPresent}; AccessibleDevices={devices.Count}; CompatibleDevices={candidates.Length}.");
                if (storedPresent || candidates.Length == 0)
                    throw AssociationRequired();

                SonyDeviceInfo replacement;
                if (candidates.Length == 1
                    && !string.IsNullOrWhiteSpace(currentCredentials.DeviceUniqueId)
                    && string.Equals(candidates[0].DeviceUniqueId, currentCredentials.DeviceUniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    replacement = candidates[0];
                    LogRenewalDiagnostic(diagnosticLog, "ReplacementSelection=StoredUniqueIdentity.");
                }
                else
                {
                    if (reassociationSelector == null) throw AssociationRequired();
                    phase = "DeviceSelection";
                    var selectedId = await reassociationSelector(candidates, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    replacement = candidates.SingleOrDefault(device => device.DeviceId == selectedId)
                        ?? throw AssociationRequired();
                    LogRenewalDiagnostic(diagnosticLog, "ReplacementSelection=ExplicitUserChoice.");
                }

                phase = "ReplacementSessionKeys";
                LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; started.");
                renewed = await FetchSessionKeysAsync(
                    client, tokens.AccessToken!, replacement.DeviceId, refreshToken,
                    currentCredentials.ClientId, timeProvider ?? TimeProvider.System,
                    cancellationToken).ConfigureAwait(false);
                renewed = renewed with { DeviceUniqueId = replacement.DeviceUniqueId };
            }
            cancellationToken.ThrowIfCancellationRequested();
            LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; succeeded; ExpiresAtUtc={renewed.SessionKeysExpiresAtUtc:O}.");
            return renewed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; cancelled.");
            throw;
        }
        catch (SonyOAuthException error)
        {
            LogRenewalDiagnostic(diagnosticLog,
                $"Phase={phase}; failed; Classification={error.Kind}; HTTP={error.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}.");
            throw;
        }
        catch (Exception error)
        {
            LogRenewalDiagnostic(diagnosticLog, $"Phase={phase}; failed; ExceptionType={error.GetType().Name}.");
            throw;
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    internal static void LogRenewalDiagnostic(Action<string>? log, string message)
    {
        try { log?.Invoke($"[Credential renewal] {message}"); }
        catch { /* Diagnostics must not affect credential renewal or persistence. */ }
    }

    private static async Task<SonyTokenResponse> ExchangeAuthorizationCodeAsync(
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

        return await SendForTokenAsync(client, request, "authorization", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SonyTokenResponse> SendForTokenAsync(
        HttpClient client,
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, request, $"Sony token {operation}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpFailureAsync($"Sony token {operation}", response, tokenEndpoint: true, cancellationToken).ConfigureAwait(false);

        try
        {
            using var readCancellation = CreateResponseReadCancellation(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(readCancellation.Token).ConfigureAwait(false);
            var tokens = await JsonSerializer.DeserializeAsync<SonyTokenResponse>(stream, cancellationToken: readCancellation.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tokens?.AccessToken))
                return tokens;
        }
        catch (JsonException)
        {
            // Converted to a sanitized protocol failure below.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Transient($"Sony token {operation} timed out.", httpStatusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException error)
        {
            throw Transient($"Sony token {operation} was interrupted.", error, (int)response.StatusCode);
        }
        catch (IOException error)
        {
            throw Transient($"Sony token {operation} was interrupted.", error, (int)response.StatusCode);
        }

        throw Protocol("Sony returned an invalid token response.", (int)response.StatusCode);
    }

    private static async Task<IReadOnlyList<SonyDeviceInfo>> GetDevicesAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateIotRequest(HttpMethod.Get, $"{IotBaseUrl}/devices", accessToken);
        using var response = await SendAsync(client, request, "Sony device query", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpFailureAsync("Sony device query", response, tokenEndpoint: false, cancellationToken).ConfigureAwait(false);

        try
        {
            using var readCancellation = CreateResponseReadCancellation(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(readCancellation.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: readCancellation.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("devices", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new JsonException();

            var result = new List<SonyDeviceInfo>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException();
                var id = GetString(item, "device_id");
                // A partial or ambiguous list cannot prove that the stored association disappeared.
                if (string.IsNullOrWhiteSpace(id) || result.Any(device => device.DeviceId == id)) throw new JsonException();
                var type = GetString(item, "device_type") ?? "Sony device";
                var attributes = GetOptionalObject(item, "attributes");
                var infos = GetOptionalObject(item, "device_infos");
                var uniqueId = GetString(attributes, "device_unique_id");
                if (!string.IsNullOrWhiteSpace(uniqueId)
                    && result.Any(device => string.Equals(device.DeviceUniqueId, uniqueId, StringComparison.OrdinalIgnoreCase)))
                    throw new JsonException();
                var identifiedModel = GetString(attributes, "identified_model_name");
                var reportedModel = GetString(infos, "model_name") ?? GetString(infos, "name");
                if (SupportedModel(identifiedModel) is { } identified
                    && SupportedModel(reportedModel) is { } reported && identified != reported)
                    throw new JsonException();
                var model = identifiedModel ?? reportedModel;
                var name = GetString(infos, "model_name") ?? GetString(item, "device_name")
                    ?? GetString(item, "name") ?? GetString(item, "model_name") ?? model ?? type;
                result.Add(new SonyDeviceInfo(id, name, type, uniqueId, model));
            }
            return result;
        }
        catch (JsonException)
        {
            throw Protocol("Sony returned an invalid device list.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Transient("Sony device query timed out.", httpStatusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException error)
        {
            throw Transient("Sony device query was interrupted.", error, (int)response.StatusCode);
        }
        catch (IOException error)
        {
            throw Transient("Sony device query was interrupted.", error, (int)response.StatusCode);
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
        string? refreshToken,
        string fallbackClientId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var request = CreateIotRequest(
            HttpMethod.Post,
            $"{IotBaseUrl}/devices/{Uri.EscapeDataString(deviceId)}/session_keys",
            accessToken);
        using var response = await SendAsync(client, request, "Sony session-key request", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpFailureAsync("Sony session-key request", response, tokenEndpoint: false, cancellationToken).ConfigureAwait(false);

        try
        {
            using var readCancellation = CreateResponseReadCancellation(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(readCancellation.Token).ConfigureAwait(false);
            var keys = await JsonSerializer.DeserializeAsync<SonySessionKeysResponse>(stream, cancellationToken: readCancellation.Token).ConfigureAwait(false);
            if (keys is null ||
                (string.IsNullOrWhiteSpace(keys.KeyId) && string.IsNullOrWhiteSpace(keys.SessionId)) ||
                string.IsNullOrWhiteSpace(keys.HmacKey))
                throw new JsonException();

            return new SonyCredentials
            {
                ClientId = string.IsNullOrWhiteSpace(keys.ClientId) ? fallbackClientId : keys.ClientId,
                LegacySessionId = string.IsNullOrWhiteSpace(keys.KeyId) ? keys.SessionId : null,
                KeyId = keys.KeyId,
                SessionKey = keys.SessionKey,
                HmacKey = keys.HmacKey,
                DeviceId = deviceId,
                RefreshToken = refreshToken,
                SessionKeysExpiresAtUtc = ParseSessionKeyExpiry(keys.ExpiresIn, timeProvider)
            };
        }
        catch (JsonException)
        {
            throw Protocol("Sony returned an invalid session-key response.", (int)response.StatusCode);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Protocol("Sony returned an invalid session-key expiry.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Transient("Sony session-key request timed out.", httpStatusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException error)
        {
            throw Transient("Sony session-key request was interrupted.", error, (int)response.StatusCode);
        }
        catch (IOException error)
        {
            throw Transient("Sony session-key request was interrupted.", error, (int)response.StatusCode);
        }
    }

    private static bool IsCompatibleReplacement(SonyDeviceInfo device) =>
        device.DeviceType.Equals("Speaker", StringComparison.OrdinalIgnoreCase)
        && SupportedModel(device.ModelName) != null;

    private static string? SupportedModel(string? model) => model?.ToUpperInvariant() switch
    {
        "HT-A9000" or "BRAVIA THEATRE BAR 9" => "HT-A9000",
        "HT-A8000" or "BRAVIA THEATRE BAR 8" => "HT-A8000",
        "HT-A9M2" or "BRAVIA THEATRE QUAD" => "HT-A9M2",
        _ => null
    };

    private static SonyOAuthException AssociationRequired() =>
        new(SonyOAuthFailureKind.DeviceAssociationRequired, "The Sony soundbar association needs attention.");

    private static JsonElement GetOptionalObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return default;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        return value;
    }

    private static HttpRequestMessage CreateIotRequest(HttpMethod method, string uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("User-Agent", IotUserAgent);
        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Transient($"{operation} timed out.");
        }
        catch (HttpRequestException error)
        {
            throw Transient($"{operation} was interrupted.", error);
        }
    }

    private static async Task<SonyOAuthException> CreateHttpFailureAsync(
        string operation,
        HttpResponseMessage response,
        bool tokenEndpoint,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status is 408 or 429 or >= 500)
            return Transient($"{operation} is temporarily unavailable (HTTP {status}).", httpStatusCode: status);
        if (tokenEndpoint && await HasInvalidGrantAsync(response, cancellationToken).ConfigureAwait(false))
            return new(SonyOAuthFailureKind.ReauthenticationRequired, "Sony authorization is no longer valid.", httpStatusCode: status);
        return Protocol($"{operation} returned an unexpected response (HTTP {status}).", status);
    }

    private static async Task<bool> HasInvalidGrantAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var readCancellation = CreateResponseReadCancellation(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(readCancellation.Token).ConfigureAwait(false);
            var error = await JsonSerializer.DeserializeAsync<SonyOAuthErrorResponse>(stream, cancellationToken: readCancellation.Token).ConfigureAwait(false);
            return string.Equals(error?.Error, "invalid_grant", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Transient("Sony token error response timed out.", httpStatusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException error)
        {
            throw Transient("Sony token error response was interrupted.", error, (int)response.StatusCode);
        }
        catch (IOException error)
        {
            throw Transient("Sony token error response was interrupted.", error, (int)response.StatusCode);
        }
    }

    private static DateTimeOffset? ParseSessionKeyExpiry(JsonElement expiresIn, TimeProvider timeProvider)
    {
        if (expiresIn.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        long seconds;
        if (expiresIn.ValueKind == JsonValueKind.Number)
        {
            if (!expiresIn.TryGetInt64(out seconds))
                throw new JsonException();
        }
        else if (expiresIn.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(expiresIn.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                throw new JsonException();
        }
        else
        {
            throw new JsonException();
        }

        if (seconds <= 0)
            throw new JsonException();
        return timeProvider.GetUtcNow().AddSeconds(seconds);
    }

    private static CancellationTokenSource CreateResponseReadCancellation(CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(CloudResponseReadTimeout);
        return cancellation;
    }

    private static SonyOAuthException Transient(string message, Exception? innerException = null, int? httpStatusCode = null) =>
        new(SonyOAuthFailureKind.Transient, message, innerException, httpStatusCode);

    private static SonyOAuthException Protocol(string message, int? httpStatusCode = null) =>
        new(SonyOAuthFailureKind.Protocol, message, httpStatusCode: httpStatusCode);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record SonyTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken)
    {
        public override string ToString() => nameof(SonyTokenResponse);
    }

    private sealed record SonyOAuthErrorResponse(
        [property: JsonPropertyName("error")] string? Error)
    {
        public override string ToString() => nameof(SonyOAuthErrorResponse);
    }

    private sealed record SonySessionKeysResponse(
        [property: JsonPropertyName("session_id")] string? SessionId,
        [property: JsonPropertyName("key_id")] string? KeyId,
        [property: JsonPropertyName("session_key")] string? SessionKey,
        [property: JsonPropertyName("hmac_key")] string? HmacKey,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("expires_in")] JsonElement ExpiresIn)
    {
        public override string ToString() => nameof(SonySessionKeysResponse);
    }
}
