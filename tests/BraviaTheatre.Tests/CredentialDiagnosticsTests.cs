using System.Net;
using System.Text;
using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.Tests;

public sealed class CredentialDiagnosticsTests
{
    private const string TokenResponse = "{\"access_token\":\"sensitive-access\"}";
    private const string KeysResponse = "{\"key_id\":\"secret-new\",\"session_key\":\"sensitive-new-session\",\"hmac_key\":\"sensitive-new-hmac\",\"expires_in\":7200}";

    [Theory]
    [InlineData(false, HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"sensitive-body\"}", SonyOAuthFailureKind.ReauthenticationRequired, CredentialRenewalStatus.AuthenticationRequired)]
    [InlineData(false, HttpStatusCode.BadRequest, "{\"error\":\"sensitive-body\"}", SonyOAuthFailureKind.Protocol, CredentialRenewalStatus.Failed)]
    [InlineData(false, HttpStatusCode.OK, "{\"access_token\":{\"secret\":\"sensitive-body\"}}", SonyOAuthFailureKind.Protocol, CredentialRenewalStatus.Failed)]
    [InlineData(true, HttpStatusCode.Forbidden, "{\"error\":\"sensitive-body\"}", SonyOAuthFailureKind.Protocol, CredentialRenewalStatus.Failed)]
    [InlineData(true, HttpStatusCode.OK, "{\"key_id\":\"secret-new\",\"hmac_key\":{\"secret\":\"sensitive-body\"}}", SonyOAuthFailureKind.Protocol, CredentialRenewalStatus.Failed)]
    public async Task CloudFailure_IdentifiesPhaseAndClassificationWithoutSecrets(
        bool sessionPhase,
        HttpStatusCode status,
        string body,
        SonyOAuthFailureKind classification,
        CredentialRenewalStatus expectedStatus)
    {
        var messages = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
            sessionPhase && request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, TokenResponse)
                : Json(status, body)));
        var initial = Credentials();
        SonyOAuthException? cloudError = null;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            async (credentials, checkpoint, cancellationToken) =>
            {
                try
                {
                    return await SonyOAuth.RefreshSessionKeysAsync(
                        credentials,
                        cancellationToken,
                        httpClient: client,
                        checkpointRotatedRefreshTokenAsync: checkpoint,
                        diagnosticLog: messages.Add);
                }
                catch (SonyOAuthException error)
                {
                    cloudError = error;
                    throw;
                }
            },
            (_, _) => throw new Xunit.Sdk.XunitException("Persistence must not run."))
        {
            DiagnosticLog = messages.Add
        };

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        Assert.NotNull(cloudError);
        Assert.Equal(classification, cloudError.Kind);
        if (status != HttpStatusCode.OK)
            Assert.Equal((int)status, cloudError.HttpStatusCode);
        var phase = sessionPhase ? "SessionKeys" : "OAuthRefresh";
        Assert.Contains(messages, line => line.Contains($"Phase={phase}", StringComparison.Ordinal)
            && line.Contains($"Classification={classification}", StringComparison.Ordinal)
            && line.Contains($"HTTP={cloudError.HttpStatusCode?.ToString() ?? "unavailable"}", StringComparison.Ordinal));
        Assert.Contains(messages, line => line.Contains($"Status={expectedStatus}", StringComparison.Ordinal));
        Assert.Same(initial, lifecycle.CurrentCredentials);
        AssertSafe(messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistenceFailure_IdentifiesCheckpointOrFinalWriteWithoutExceptionMessage(bool checkpointFailure)
    {
        var messages = new List<string>();
        var initial = Credentials();
        var writes = 0;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            async (credentials, checkpoint, _) =>
            {
                await checkpoint("sensitive-rotated-refresh");
                return credentials with { RefreshToken = "sensitive-rotated-refresh", KeyId = "secret-new" };
            },
            (_, _) => ++writes == (checkpointFailure ? 1 : 2)
                ? throw new IOException("sensitive-storage-message")
                : Task.CompletedTask)
        {
            DiagnosticLog = messages.Add
        };

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Failed, result.Status);
        var phase = checkpointFailure ? "RotatedTokenCheckpoint" : "FinalPersistence";
        Assert.Contains(messages, line => line.Contains($"Phase={phase}", StringComparison.Ordinal)
            && line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            && line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.Equal(checkpointFailure ? 1 : 2, writes);
        Assert.Equal(!checkpointFailure, lifecycle.IsLocalKeyRefreshPending(lifecycle.CurrentCredentials!));
        AssertSafe(messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticCallbacks_DoNotChangeSuccessfulRenewalOrPersistence(bool throwFromLog)
    {
        var initial = Credentials();
        var writes = 0;
        var messages = new List<string>();
        using var client = new HttpClient(new StubHandler(request => Json(
            HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"sensitive-access\",\"refresh_token\":\"sensitive-rotated-refresh\"}"
                : KeysResponse)));
        Action<string> log = line =>
        {
            if (throwFromLog) throw new InvalidOperationException("sensitive-logger-message");
            messages.Add(line);
        };
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (credentials, checkpoint, cancellationToken) => SonyOAuth.RefreshSessionKeysAsync(
                credentials,
                cancellationToken,
                httpClient: client,
                checkpointRotatedRefreshTokenAsync: checkpoint,
                diagnosticLog: log),
            (_, _) =>
            {
                writes++;
                return Task.CompletedTask;
            })
        {
            DiagnosticLog = log
        };

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Succeeded, result.Status);
        Assert.Equal(2, writes);
        Assert.Same(result.Credentials, lifecycle.CurrentCredentials);
        Assert.False(lifecycle.IsLocalKeyRefreshPending(result.Credentials!));
        if (!throwFromLog)
        {
            Assert.Contains(messages, line => line.Contains("RefreshTokenRotated=True", StringComparison.Ordinal));
            foreach (var phase in new[] { "OAuthRefresh", "RotatedTokenCheckpoint", "SessionKeys", "FinalPersistence" })
                Assert.Contains(messages, line => line.Contains($"Phase={phase}", StringComparison.Ordinal)
                    && line.Contains("succeeded", StringComparison.Ordinal));
            AssertSafe(messages);
        }
    }

    private static void AssertSafe(IEnumerable<string> messages)
    {
        Assert.All(messages, line =>
        {
            Assert.Contains("[Credential renewal]", line, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive-", line, StringComparison.Ordinal);
        });
    }

    private static SonyCredentials Credentials() => new()
    {
        DeviceId = "sensitive-device",
        KeyId = "secret-key",
        SessionKey = "sensitive-session",
        HmacKey = "sensitive-hmac",
        RefreshToken = "sensitive-refresh",
        SessionKeysExpiresAtUtc = new DateTimeOffset(2026, 9, 20, 5, 0, 0, TimeSpan.Zero)
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
