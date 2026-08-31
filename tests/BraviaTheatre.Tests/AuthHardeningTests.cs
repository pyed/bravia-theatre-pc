using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

public sealed class AuthHardeningTests
{
    [Fact]
    public void StrictCallbackParser_AcceptsOnlyExpectedCustomUri()
    {
        var callback = SonyOAuth.ParseAuthorizationCallback("ssh-app://signin?code=AUTH123&state=STATE456");

        Assert.Equal("AUTH123", callback.Code);
        Assert.Equal("STATE456", callback.State);
        Assert.Equal(nameof(SonyOAuthCallback), callback.ToString());
        Assert.Throws<InvalidOperationException>(() =>
            SonyOAuth.ParseAuthorizationCallback("https://example.com/signin?code=AUTH123&state=STATE456"));
        Assert.Throws<InvalidOperationException>(() =>
            SonyOAuth.ParseAuthorizationCallback("ssh-app://signin.evil?code=AUTH123&state=STATE456"));
        Assert.Throws<InvalidOperationException>(() =>
            SonyOAuth.ParseAuthorizationCallback("ssh-app://signin/?code=AUTH123&state=STATE456"));
        Assert.Throws<InvalidOperationException>(() =>
            SonyOAuth.ParseAuthorizationCallback("ssh-app://signin?code=AUTH123&state=STATE456#fragment"));
        Assert.Throws<InvalidOperationException>(() =>
            SonyOAuth.ParseAuthorizationCallback("ssh-app://signin?code=AUTH123"));
    }

    [Fact]
    public async Task CompleteOAuthFlow_RejectsMismatchedStateBeforeNetworkCall()
    {
        var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Network must not be called."));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SonyOAuth.CompleteOAuthFlowAsync(
                "ssh-app://signin?code=AUTH123&state=WRONG",
                "verifier",
                "EXPECTED",
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client));

        Assert.Contains("state does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CompleteOAuthFlow_UsesExplicitDeviceSelector()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            await Task.Yield();
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"access_token\":\"transient-access\",\"refresh_token\":\"renewal\"}");
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK,
                    "{\"devices\":[{\"device_id\":\"one\",\"device_type\":\"Speaker\",\"device_name\":\"Living Room\"},{\"device_id\":\"two\",\"device_type\":\"Speaker\",\"device_name\":\"Cinema\"}]}");
            Assert.EndsWith("/devices/two/session_keys", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK,
                "{\"session_id\":\"legacy\",\"key_id\":\"key\",\"session_key\":\"session-secret\",\"hmac_key\":\"hmac\",\"client_id\":\"client\",\"expires_in\":7200}");
        });
        using var client = new HttpClient(handler);

        var credentials = await SonyOAuth.CompleteOAuthFlowAsync(
            "ssh-app://signin?code=AUTH123&state=EXPECTED",
            "verifier",
            "EXPECTED",
            cancellationToken: TestContext.Current.CancellationToken,
            deviceSelector: (devices, _) => Task.FromResult<string?>(devices.Single(d => d.DisplayName == "Cinema").DeviceId),
            httpClient: client,
            timeProvider: new FixedTimeProvider(now));

        Assert.True(credentials.IsValid);
        Assert.Equal("two", credentials.DeviceId);
        Assert.Null(credentials.LegacySessionId);
        Assert.Equal("key", credentials.KeyId);
        Assert.Equal("key", credentials.SessionId);
        Assert.Equal("session-secret", credentials.SessionKey);
        Assert.Equal("hmac", credentials.HmacKey);
        Assert.Equal("renewal", credentials.RefreshToken);
        Assert.Equal(now.AddHours(2), credentials.SessionKeysExpiresAtUtc);
        var serialized = JsonSerializer.Serialize(credentials);
        Assert.DoesNotContain("transient-access", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSessionKeys_RotatesRenewalMaterialWithoutPersistingAccessToken()
    {
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("grant_type=refresh_token", form, StringComparison.Ordinal);
                Assert.Contains("refresh_token=old-refresh", form, StringComparison.Ordinal);
                return Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\",\"refresh_token\":\"replacement\"}");
            }
            Assert.Equal("/devices/device/session_keys", request.RequestUri.AbsolutePath);
            return Json(HttpStatusCode.OK, "{\"session_id\":\"new-session\",\"hmac_key\":\"new-hmac\"}");
        });
        using var client = new HttpClient(handler);

        var credentials = await SonyOAuth.RefreshSessionKeysAsync(
            ValidCredentials("old-refresh"),
            cancellationToken: TestContext.Current.CancellationToken,
            httpClient: client);
        var serialized = JsonSerializer.Serialize(credentials);

        Assert.True(credentials.IsValid);
        Assert.Equal("replacement", credentials.RefreshToken);
        Assert.DoesNotContain("temporary", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", serialized, StringComparison.Ordinal);
        Assert.Contains("refresh_token", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSessionKeys_PreservesRefreshTokenWhenSonyOmitsReplacement()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\"}")
                : Json(HttpStatusCode.OK, "{\"key_id\":\"new-key\",\"session_key\":\"new-session\",\"hmac_key\":\"new-hmac\"}")));
        using var client = new HttpClient(handler);

        var credentials = await SonyOAuth.RefreshSessionKeysAsync(
            ValidCredentials("keep-me"),
            cancellationToken: TestContext.Current.CancellationToken,
            httpClient: client);

        Assert.Equal("keep-me", credentials.RefreshToken);
        Assert.Equal("new-key", credentials.KeyId);
        Assert.Equal("new-session", credentials.SessionKey);
        Assert.Equal("new-hmac", credentials.HmacKey);
        Assert.Null(credentials.SessionKeysExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshSessionKeys_RotatedTokenCheckpointSurvivesSessionKeyFailure()
    {
        var initial = ValidCredentials("old-refresh");
        var tokenCalls = 0;
        var sessionCalls = 0;
        var persisted = new List<SonyCredentials>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (Interlocked.Increment(ref tokenCalls) == 1)
                {
                    Assert.Contains("refresh_token=old-refresh", form, StringComparison.Ordinal);
                    return Json(HttpStatusCode.OK, "{\"access_token\":\"temporary-one\",\"refresh_token\":\"rotated-refresh\"}");
                }

                Assert.Contains("refresh_token=rotated-refresh", form, StringComparison.Ordinal);
                return Json(HttpStatusCode.OK, "{\"access_token\":\"temporary-two\"}");
            }

            return Interlocked.Increment(ref sessionCalls) == 1
                ? Json(HttpStatusCode.ServiceUnavailable, "{}")
                : Json(HttpStatusCode.OK, "{\"key_id\":\"renewed-key\",\"session_key\":\"renewed-session\",\"hmac_key\":\"renewed-hmac\"}");
        });
        using var client = new HttpClient(handler);
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (credentials, checkpoint, cancellationToken) => SonyOAuth.RefreshSessionKeysAsync(
                credentials,
                cancellationToken,
                httpClient: client,
                checkpointRotatedRefreshTokenAsync: checkpoint),
            (credentials, _) =>
            {
                persisted.Add(credentials);
                return Task.CompletedTask;
            });

        var first = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.TransientFailure, first.Status);
        var checkpoint = Assert.IsType<SonyCredentials>(lifecycle.CurrentCredentials);
        Assert.Equal(initial.KeyId, checkpoint.KeyId);
        Assert.Equal("rotated-refresh", checkpoint.RefreshToken);
        Assert.True(lifecycle.IsLocalKeyRefreshPending(checkpoint));
        Assert.Single(persisted);
        Assert.Same(checkpoint, persisted[0]);

        var second = await lifecycle.RefreshAsync(checkpoint, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Succeeded, second.Status);
        Assert.Equal("renewed-key", second.Credentials?.KeyId);
        Assert.Equal("rotated-refresh", second.Credentials?.RefreshToken);
        Assert.False(lifecycle.IsLocalKeyRefreshPending(second.Credentials!));
        Assert.Equal(2, persisted.Count);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, sessionCalls);
    }

    [Fact]
    public async Task RefreshSessionKeys_CancellationAfterRotationKeepsDurableCheckpoint()
    {
        var initial = ValidCredentials("old-refresh");
        var sessionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<SonyCredentials>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\",\"refresh_token\":\"rotated-refresh\"}");

            sessionStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        using var client = new HttpClient(handler);
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (credentials, checkpoint, cancellationToken) => SonyOAuth.RefreshSessionKeysAsync(
                credentials,
                cancellationToken,
                httpClient: client,
                checkpointRotatedRefreshTokenAsync: checkpoint),
            (credentials, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                persisted.Add(credentials);
                return Task.CompletedTask;
            });

        var refreshing = lifecycle.RefreshAsync(initial, cancellation.Token);
        await sessionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        var checkpoint = Assert.IsType<SonyCredentials>(lifecycle.CurrentCredentials);
        Assert.Equal(initial.KeyId, checkpoint.KeyId);
        Assert.Equal("rotated-refresh", checkpoint.RefreshToken);
        Assert.True(lifecycle.IsLocalKeyRefreshPending(checkpoint));
        Assert.Single(persisted);
        Assert.Same(checkpoint, persisted[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"secret detail\"}", SonyOAuthFailureKind.ReauthenticationRequired)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", SonyOAuthFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":\"invalid_grant\"}", SonyOAuthFailureKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", SonyOAuthFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}", SonyOAuthFailureKind.Protocol)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\"}", SonyOAuthFailureKind.Protocol)]
    public async Task RefreshSessionKeys_ClassifiesHttpFailureNarrowly(
        HttpStatusCode status,
        string responseBody,
        SonyOAuthFailureKind expectedKind)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(status, responseBody)));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() =>
            SonyOAuth.RefreshSessionKeysAsync(
                ValidCredentials("refresh-secret"),
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client));

        Assert.Equal(expectedKind, error.Kind);
        Assert.DoesNotContain("refresh-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSessionKeys_ClassifiesNetworkFailureAsTransient()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() =>
            SonyOAuth.RefreshSessionKeysAsync(
                ValidCredentials("refresh-secret"),
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client));

        Assert.Equal(SonyOAuthFailureKind.Transient, error.Kind);
        Assert.DoesNotContain("refresh-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSessionKeys_DoesNotTreatSessionKeyAsHmacAlias()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\"}")
                : Json(HttpStatusCode.OK, "{\"key_id\":\"key\",\"session_key\":\"not-an-hmac-key\"}")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() =>
            SonyOAuth.RefreshSessionKeysAsync(
                ValidCredentials("refresh-secret"),
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client));

        Assert.Equal(SonyOAuthFailureKind.Protocol, error.Kind);
    }

    [Fact]
    public async Task RefreshSessionKeys_InvalidGrantTextOutsideOAuthEndpointIsProtocolFailure()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\"}")
                : Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() =>
            SonyOAuth.RefreshSessionKeysAsync(
                ValidCredentials("refresh-secret"),
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client));

        Assert.Equal(SonyOAuthFailureKind.Protocol, error.Kind);
    }

    [Theory]
    [InlineData("\"7200\"", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("\"9223372036854775808\"", false)]
    [InlineData("9223372036854775807", false)]
    public async Task RefreshSessionKeys_ValidatesExpiryRepresentation(string expiresIn, bool succeeds)
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\"}")
                : Json(HttpStatusCode.OK, $"{{\"key_id\":\"key\",\"hmac_key\":\"hmac\",\"expires_in\":{expiresIn}}}")));
        using var client = new HttpClient(handler);

        if (succeeds)
        {
            var credentials = await SonyOAuth.RefreshSessionKeysAsync(
                ValidCredentials("refresh"),
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client,
                timeProvider: new FixedTimeProvider(now));

            Assert.Equal(now.AddHours(2), credentials.SessionKeysExpiresAtUtc);
            return;
        }

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() =>
            SonyOAuth.RefreshSessionKeysAsync(
                ValidCredentials("refresh"),
                cancellationToken: TestContext.Current.CancellationToken,
                httpClient: client,
                timeProvider: new FixedTimeProvider(now)));
        Assert.Equal(SonyOAuthFailureKind.Protocol, error.Kind);
    }

    [Fact]
    public void CredentialsRequireSessionAndHmac()
    {
        Assert.False(new SonyCredentials { DeviceId = "device", HmacKey = "hmac" }.IsValid);
        Assert.False(new SonyCredentials { DeviceId = "device", SessionId = "session" }.IsValid);
        Assert.False(new SonyCredentials { SessionId = "session", HmacKey = "hmac" }.IsValid);
        Assert.False(new SonyCredentials { ClientId = "", DeviceId = "device", SessionId = "session", HmacKey = "hmac" }.IsValid);
        Assert.True(new SonyCredentials { DeviceId = "device", SessionId = "session", HmacKey = "hmac" }.IsValid);
    }

    [Fact]
    public void Credentials_LegacySessionIdLoadsWithoutCollapsingDistinctKeys()
    {
        const string legacyJson = "{\"client_id\":\"client\",\"session_id\":\"legacy\",\"hmac_key\":\"hmac\",\"device_id\":\"device\"}";

        var legacy = JsonSerializer.Deserialize<SonyCredentials>(legacyJson);
        var modern = new SonyCredentials
        {
            DeviceId = "device",
            KeyId = "key",
            SessionKey = "session-key",
            HmacKey = "hmac-key",
            RefreshToken = "refresh"
        };
        var modernJson = JsonSerializer.Serialize(modern);

        Assert.NotNull(legacy);
        Assert.True(legacy.IsValid);
        Assert.Equal("legacy", legacy.SessionId);
        Assert.Null(legacy.KeyId);
        Assert.Equal("key", modern.SessionId);
        Assert.DoesNotContain("session_id", modernJson, StringComparison.Ordinal);
        Assert.Contains("\"key_id\":\"key\"", modernJson, StringComparison.Ordinal);
        Assert.Contains("\"session_key\":\"session-key\"", modernJson, StringComparison.Ordinal);
        Assert.Contains("\"hmac_key\":\"hmac-key\"", modernJson, StringComparison.Ordinal);
        Assert.Equal(nameof(SonyCredentials), modern.ToString());
    }

    [Fact]
    public void CredentialStore_WhenProtectedFileIsMissing_ReturnsMissing()
    {
        var tempDir = CreateTempDirectoryPath();
        var credentialPath = Path.Combine(tempDir, "credentials.dat");

        try
        {
            var result = new SonyCredentialStore(credentialPath).Load();

            Assert.Equal(CredentialLoadStatus.Missing, result.Status);
            Assert.Null(result.Credentials);
            Assert.False(File.Exists(credentialPath));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void CredentialStore_SaveThenLoad_RoundTripsProtectedRenewableCredentials()
    {
        var tempDir = CreateTempDirectoryPath();
        var credentialPath = Path.Combine(tempDir, "credentials.dat");
        var store = new SonyCredentialStore(credentialPath);
        var expiresAt = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var credentials = new SonyCredentials
        {
            ClientId = "test-client",
            LegacySessionId = "legacy-sid",
            KeyId = "test-key-id",
            SessionKey = "test-session-key",
            HmacKey = "test-hmac",
            DeviceId = "test-device",
            RefreshToken = "test-refresh",
            SessionKeysExpiresAtUtc = expiresAt
        };

        try
        {
            Assert.True(store.TrySave(credentials, out var saveError), saveError);

            var result = store.Load();

            Assert.Equal(CredentialLoadStatus.Loaded, result.Status);
            Assert.NotNull(result.Credentials);
            Assert.Equal("test-client", result.Credentials.ClientId);
            Assert.Equal("test-key-id", result.Credentials.SessionId);
            Assert.Equal("legacy-sid", result.Credentials.LegacySessionId);
            Assert.Equal("test-key-id", result.Credentials.KeyId);
            Assert.Equal("test-session-key", result.Credentials.SessionKey);
            Assert.Equal("test-hmac", result.Credentials.HmacKey);
            Assert.Equal("test-device", result.Credentials.DeviceId);
            Assert.Equal("test-refresh", result.Credentials.RefreshToken);
            Assert.Equal(expiresAt, result.Credentials.SessionKeysExpiresAtUtc);
            Assert.Equal(-1, File.ReadAllBytes(credentialPath).AsSpan().IndexOf("test-refresh"u8));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void CredentialStore_OldProtectedSchemaLoadsWithoutRenewalMaterial()
    {
        var tempDir = CreateTempDirectoryPath();
        var credentialPath = Path.Combine(tempDir, "credentials.dat");

        try
        {
            WriteProtectedCredentialJson(
                credentialPath,
                "{\"client_id\":\"legacy-client\",\"session_id\":\"legacy-session\",\"hmac_key\":\"legacy-hmac\",\"device_id\":\"legacy-device\"}");

            var result = new SonyCredentialStore(credentialPath).Load();

            Assert.Equal(CredentialLoadStatus.Loaded, result.Status);
            Assert.NotNull(result.Credentials);
            Assert.Equal("legacy-session", result.Credentials.SessionId);
            Assert.Null(result.Credentials.RefreshToken);
            Assert.Null(result.Credentials.SessionKeysExpiresAtUtc);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void CredentialStore_FailedReplacementLeavesPriorProtectedFileIntact()
    {
        var tempDir = CreateTempDirectoryPath();
        var credentialPath = Path.Combine(tempDir, "credentials.dat");
        var store = new SonyCredentialStore(credentialPath);
        var initial = ValidCredentials("initial-refresh");
        var replacement = ValidCredentials("replacement-refresh") with { KeyId = "key2" };

        try
        {
            Assert.True(store.TrySave(initial, out var initialError), initialError);
            using (File.Open(credentialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(store.TrySave(replacement, out var replacementError));
                Assert.NotNull(replacementError);
            }

            var result = store.Load();
            Assert.Equal(CredentialLoadStatus.Loaded, result.Status);
            Assert.NotNull(result.Credentials);
            Assert.Equal(initial.KeyId, result.Credentials.KeyId);
            Assert.Equal(initial.RefreshToken, result.Credentials.RefreshToken);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void CredentialStore_WithUnsupportedProtectedFile_ReturnsError()
    {
        var tempDir = CreateTempDirectoryPath();
        var credentialPath = Path.Combine(tempDir, "credentials.dat");

        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(credentialPath, "not-a-protected-credential-payload");

            var result = new SonyCredentialStore(credentialPath).Load();

            Assert.Equal(CredentialLoadStatus.Error, result.Status);
            Assert.Null(result.Credentials);
            Assert.NotNull(result.Message);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static string CreateTempDirectoryPath() =>
        Path.Combine(Path.GetTempPath(), "BraviaTheatre.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void WriteProtectedCredentialJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clearBytes = Encoding.UTF8.GetBytes(json);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                clearBytes,
                Encoding.UTF8.GetBytes("BraviaTheatrePC.Credentials.v1"),
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, [.. "BTPC1"u8, .. protectedBytes]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private static SonyCredentials ValidCredentials(string refreshToken) => new()
    {
        DeviceId = "device",
        KeyId = "key",
        HmacKey = "hmac",
        RefreshToken = refreshToken
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
