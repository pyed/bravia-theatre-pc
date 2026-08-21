using System.Net;
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
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            await Task.Yield();
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"access_token\":\"transient-access\"}");
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK,
                    "{\"devices\":[{\"device_id\":\"one\",\"device_type\":\"Speaker\",\"device_name\":\"Living Room\"},{\"device_id\":\"two\",\"device_type\":\"Speaker\",\"device_name\":\"Cinema\"}]}");
            Assert.EndsWith("/devices/two/session_keys", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK,
                "{\"session_id\":\"session\",\"hmac_key\":\"hmac\",\"client_id\":\"client\"}");
        });
        using var client = new HttpClient(handler);

        var credentials = await SonyOAuth.CompleteOAuthFlowAsync(
            "ssh-app://signin?code=AUTH123&state=EXPECTED",
            "verifier",
            "EXPECTED",
            cancellationToken: TestContext.Current.CancellationToken,
            deviceSelector: (devices, _) => Task.FromResult<string?>(devices.Single(d => d.DisplayName == "Cinema").DeviceId),
            httpClient: client);

        Assert.True(credentials.IsValid);
        Assert.Equal("two", credentials.DeviceId);
        Assert.Null(credentials.AccessToken);
        Assert.Null(credentials.RefreshToken);
    }

    [Fact]
    public async Task RefreshSessionKeys_DoesNotReturnOrPersistCloudToken()
    {
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("grant_type=refresh_token", form, StringComparison.Ordinal);
                return Json(HttpStatusCode.OK, "{\"access_token\":\"temporary\",\"refresh_token\":\"replacement\"}");
            }
            return Json(HttpStatusCode.OK, "{\"session_id\":\"new-session\",\"hmac_key\":\"new-hmac\"}");
        });
        using var client = new HttpClient(handler);

        var credentials = await SonyOAuth.RefreshSessionKeysAsync(
            "old-refresh",
            "device",
            cancellationToken: TestContext.Current.CancellationToken,
            httpClient: client);
        var serialized = JsonSerializer.Serialize(credentials);

        Assert.True(credentials.IsValid);
        Assert.DoesNotContain("temporary", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsRequireSessionAndHmac()
    {
        Assert.False(new SonyCredentials { HmacKey = "hmac" }.IsValid);
        Assert.False(new SonyCredentials { SessionId = "session" }.IsValid);
        Assert.True(new SonyCredentials { SessionId = "session", HmacKey = "hmac" }.IsValid);
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
    public void CredentialStore_SaveThenLoad_RoundTripsProtectedLocalCredentialsOnly()
    {
        var tempDir = CreateTempDirectoryPath();
        var credentialPath = Path.Combine(tempDir, "credentials.dat");
        var store = new SonyCredentialStore(credentialPath);
        var credentials = new SonyCredentials
        {
            ClientId = "test-client",
            SessionId = "test-sid",
            HmacKey = "test-hmac",
            DeviceId = "test-device",
            AccessToken = "ephemeral",
            RefreshToken = "ephemeral"
        };

        try
        {
            Assert.True(store.TrySave(credentials, out var saveError), saveError);

            var result = store.Load();

            Assert.Equal(CredentialLoadStatus.Loaded, result.Status);
            Assert.NotNull(result.Credentials);
            Assert.Equal("test-client", result.Credentials.ClientId);
            Assert.Equal("test-sid", result.Credentials.SessionId);
            Assert.Equal("test-hmac", result.Credentials.HmacKey);
            Assert.Equal("test-device", result.Credentials.DeviceId);
            Assert.Null(result.Credentials.AccessToken);
            Assert.Null(result.Credentials.RefreshToken);
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
}
