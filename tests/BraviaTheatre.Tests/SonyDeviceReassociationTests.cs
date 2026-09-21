using System.Net;
using System.Text;
using System.Text.Json;
using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.Tests;

public sealed class SonyDeviceReassociationTests
{
    private const string OldDevice = "sensitive-old-device";
    private const string NewDevice = "sensitive-new-device";
    private const string UniqueDevice = "sensitive-physical-device";
    private const string TokenBody = "{\"access_token\":\"sensitive-access\",\"refresh_token\":\"sensitive-rotated-refresh\"}";
    private const string KeysBody = "{\"key_id\":\"new-key\",\"session_key\":\"sensitive-new-session\",\"hmac_key\":\"sensitive-new-hmac\",\"expires_in\":86400}";

    [Fact]
    public async Task NormalRenewal_PreservesPhysicalIdentityWithoutDiscoveryOrSelection()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(Json(
            HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/token" ? TokenBody : KeysBody)));
        using var client = new HttpClient(handler);

        var renewed = await SonyOAuth.RefreshSessionKeysAsync(
            Credentials(), TestContext.Current.CancellationToken, httpClient: client,
            reassociationSelector: (_, _) => throw new Xunit.Sdk.XunitException("Selection must not run."));

        Assert.Equal(OldDevice, renewed.DeviceId);
        Assert.Equal(UniqueDevice, renewed.DeviceUniqueId);
        Assert.Equal(["POST /token", $"POST /devices/{OldDevice}/session_keys"], handler.Requests);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NotFound, SonyOAuthFailureKind.Protocol)]
    [InlineData(true, HttpStatusCode.BadRequest, SonyOAuthFailureKind.Protocol)]
    [InlineData(true, HttpStatusCode.Unauthorized, SonyOAuthFailureKind.Protocol)]
    [InlineData(true, HttpStatusCode.Forbidden, SonyOAuthFailureKind.Protocol)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, SonyOAuthFailureKind.Transient)]
    public async Task OtherFailures_DoNotTriggerDiscovery(
        bool sessionFailure, HttpStatusCode status, SonyOAuthFailureKind expectedKind)
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(
            sessionFailure && request.RequestUri!.AbsolutePath == "/token"
                ? Json(HttpStatusCode.OK, TokenBody)
                : Json(status, "{\"error\":\"sensitive-remote-error\"}")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() => SonyOAuth.RefreshSessionKeysAsync(
            Credentials(), TestContext.Current.CancellationToken, httpClient: client));

        Assert.Equal(expectedKind, error.Kind);
        Assert.Equal(sessionFailure ? 2 : 1, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.StartsWith("GET", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("HT-A9000")]
    [InlineData("HT-A8000")]
    [InlineData("HT-A9M2")]
    [InlineData("BRAVIA Theatre Bar 9")]
    [InlineData("BRAVIA Theatre Bar 8")]
    [InlineData("BRAVIA Theatre Quad")]
    public async Task SingleCompatiblePhysicalMatch_ReassociatesWithOneAccessToken(string model)
    {
        var messages = new List<string>();
        using var handler = ReassociationHandler(Devices(Device(NewDevice, UniqueDevice.ToUpperInvariant(), model)));
        using var client = new HttpClient(handler);

        var renewed = await SonyOAuth.RefreshSessionKeysAsync(
            Credentials(), TestContext.Current.CancellationToken, httpClient: client,
            diagnosticLog: messages.Add);

        Assert.Equal(NewDevice, renewed.DeviceId);
        Assert.Equal(UniqueDevice.ToUpperInvariant(), renewed.DeviceUniqueId);
        Assert.Equal("new-key", renewed.KeyId);
        Assert.Equal("sensitive-new-session", renewed.SessionKey);
        Assert.Equal("sensitive-new-hmac", renewed.HmacKey);
        Assert.Equal("sensitive-rotated-refresh", renewed.RefreshToken);
        Assert.Equal([
            "POST /token", $"POST /devices/{OldDevice}/session_keys", "GET /devices",
            $"POST /devices/{NewDevice}/session_keys"], handler.Requests);
        AssertSafe(messages);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("unknown-model")]
    [InlineData("model-prefix-only")]
    [InlineData("wrong-type")]
    [InlineData("multiple")]
    [InlineData("changed-identity")]
    [InlineData("missing-identity")]
    [InlineData("legacy")]
    [InlineData("old-still-present")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-identity")]
    public async Task UntrustedOrAmbiguousDiscovery_DoesNotSwitchAutomatically(string scenario)
    {
        var initial = scenario == "legacy" ? Credentials() with { DeviceUniqueId = null } : Credentials();
        var devices = scenario switch
        {
            "zero" => Devices(),
            "unknown-model" => Devices(Device(model: "Unknown soundbar")),
            "model-prefix-only" => Devices(Device(model: "HT-A9000-other-model")),
            "wrong-type" => Devices(Device(type: "TV")),
            "multiple" => Devices(Device(), Device("sensitive-second-device", "sensitive-second-physical")),
            "changed-identity" => Devices(Device(unique: "sensitive-different-physical")),
            "missing-identity" => Devices(Device(unique: null)),
            "old-still-present" => Devices(Device(OldDevice)),
            "duplicate-id" => Devices(Device(), Device(unique: "sensitive-second-physical")),
            "duplicate-identity" => Devices(Device(), Device("sensitive-second-device")),
            _ => Devices(Device())
        };
        var messages = new List<string>();
        using var handler = ReassociationHandler(devices);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() => SonyOAuth.RefreshSessionKeysAsync(
            initial, TestContext.Current.CancellationToken, httpClient: client, diagnosticLog: messages.Add));

        Assert.Equal(scenario.StartsWith("duplicate-", StringComparison.Ordinal)
            ? SonyOAuthFailureKind.Protocol : SonyOAuthFailureKind.DeviceAssociationRequired, error.Kind);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(OldDevice, initial.DeviceId);
        AssertSafe(messages.Append(error.Message));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("old-still-present")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-identity")]
    public async Task UnusableDiscovery_DoesNotInvokeEvenExplicitSelector(string scenario)
    {
        var devices = scenario switch
        {
            "zero" => Devices(),
            "old-still-present" => Devices(Device(OldDevice), Device(unique: "sensitive-second-physical")),
            "duplicate-id" => Devices(Device(), Device(unique: "sensitive-second-physical")),
            _ => Devices(Device(), Device("sensitive-second-device", UniqueDevice.ToUpperInvariant()))
        };
        using var handler = ReassociationHandler(devices);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() => SonyOAuth.RefreshSessionKeysAsync(
            Credentials(), TestContext.Current.CancellationToken, httpClient: client,
            reassociationSelector: (_, _) => throw new Xunit.Sdk.XunitException("Unsafe list must not be selected.")));

        Assert.Equal(scenario.StartsWith("duplicate-", StringComparison.Ordinal)
            ? SonyOAuthFailureKind.Protocol : SonyOAuthFailureKind.DeviceAssociationRequired, error.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task LegacyCredentials_RequireExplicitSelectionEvenForOneCompatibleDevice()
    {
        var selectorCalls = 0;
        using var handler = ReassociationHandler(Devices(Device()));
        using var client = new HttpClient(handler);

        var renewed = await SonyOAuth.RefreshSessionKeysAsync(
            Credentials() with { DeviceUniqueId = null }, TestContext.Current.CancellationToken, httpClient: client,
            reassociationSelector: (devices, _) =>
            {
                selectorCalls++;
                return Task.FromResult<string?>(Assert.Single(devices).DeviceId);
            });

        Assert.Equal(1, selectorCalls);
        Assert.Equal(NewDevice, renewed.DeviceId);
        Assert.Equal(UniqueDevice, renewed.DeviceUniqueId);
    }

    [Fact]
    public async Task ExplicitSelection_ReceivesOnlyCompatibleDevicesAndInstallsSelectedIdentity()
    {
        using var handler = ReassociationHandler(Devices(
            Device("sensitive-first-device", "sensitive-first-physical"),
            Device(NewDevice, "sensitive-selected-physical"),
            Device("sensitive-unknown-device", "sensitive-unknown-physical", "Unknown"),
            Device("sensitive-tv-device", "sensitive-tv-physical", type: "TV")));
        using var client = new HttpClient(handler);

        var renewed = await SonyOAuth.RefreshSessionKeysAsync(
            Credentials(), TestContext.Current.CancellationToken, httpClient: client,
            reassociationSelector: (devices, _) =>
            {
                Assert.Equal(2, devices.Count);
                Assert.All(devices, device => Assert.Equal("HT-A9000", device.ModelName));
                return Task.FromResult<string?>(devices[1].DeviceId);
            });

        Assert.Equal(NewDevice, renewed.DeviceId);
        Assert.Equal("sensitive-selected-physical", renewed.DeviceUniqueId);
        Assert.Equal($"POST /devices/{NewDevice}/session_keys", handler.Requests[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SENSITIVE-NEW-DEVICE")]
    [InlineData("sensitive-not-offered")]
    public async Task CancelledOrInvalidSelection_DoesNotRequestReplacementKeys(string? selection)
    {
        using var handler = ReassociationHandler(Devices(Device()));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SonyOAuthException>(() => SonyOAuth.RefreshSessionKeysAsync(
            Credentials() with { DeviceUniqueId = null }, TestContext.Current.CancellationToken, httpClient: client,
            reassociationSelector: (_, _) => Task.FromResult(selection)));

        Assert.Equal(SonyOAuthFailureKind.DeviceAssociationRequired, error.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationDuringSelection_PropagatesWithoutReplacementRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = ReassociationHandler(Devices(Device()));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SonyOAuth.RefreshSessionKeysAsync(
            Credentials() with { DeviceUniqueId = null }, cancellation.Token, httpClient: client,
            reassociationSelector: (_, ct) =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<string?>(NewDevice);
            }));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryOrReplacement404_IsBoundedAndDoesNotRestartRecovery(bool discoveryFailure)
    {
        using var handler = ReassociationHandler(Devices(Device()),
            discoveryStatus: discoveryFailure ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            replacementStatus: HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<SonyOAuthException>(() => SonyOAuth.RefreshSessionKeysAsync(
            Credentials(), TestContext.Current.CancellationToken, httpClient: client));

        Assert.Equal(discoveryFailure ? 3 : 4, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request == "GET /devices");
        Assert.Single(handler.Requests, request => request == "POST /token");
    }

    [Fact]
    public async Task InteractiveLogin_StoresSelectedNestedPhysicalIdentity()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(Json(HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath == "/token" ? TokenBody
            : request.Method == HttpMethod.Get ? Devices(Device()) : KeysBody)));
        using var client = new HttpClient(handler);

        var credentials = await SonyOAuth.CompleteOAuthFlowAsync(
            "ssh-app://signin?code=synthetic-code&state=synthetic-state", "synthetic-verifier", "synthetic-state",
            TestContext.Current.CancellationToken, httpClient: client);

        Assert.Equal(NewDevice, credentials.DeviceId);
        Assert.Equal(UniqueDevice, credentials.DeviceUniqueId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reassociation_PersistsCoherentSnapshotBeforePublishing(bool finalPersistenceFails)
    {
        var initial = Credentials();
        var persisted = new List<SonyCredentials>();
        using var handler = ReassociationHandler(Devices(Device()));
        using var client = new HttpClient(handler);
        SonyCredentialLifecycle? lifecycle = null;
        lifecycle = new SonyCredentialLifecycle(initial,
            (credentials, checkpoint, ct) => SonyOAuth.RefreshSessionKeysAsync(
                credentials, ct, httpClient: client, checkpointRotatedRefreshTokenAsync: checkpoint),
            (credentials, _) =>
            {
                Assert.Equal(OldDevice, lifecycle!.CurrentCredentials!.DeviceId);
                persisted.Add(credentials);
                if (finalPersistenceFails && credentials.DeviceId == NewDevice)
                    throw new IOException("sensitive-persistence-error");
                return Task.CompletedTask;
            });

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(2, persisted.Count);
        Assert.Equal(OldDevice, persisted[0].DeviceId);
        Assert.Equal(initial.KeyId, persisted[0].KeyId);
        Assert.Equal("sensitive-rotated-refresh", persisted[0].RefreshToken);
        Assert.Equal(NewDevice, persisted[1].DeviceId);
        Assert.Equal(UniqueDevice, persisted[1].DeviceUniqueId);
        Assert.Equal("new-key", persisted[1].KeyId);
        Assert.Equal("sensitive-rotated-refresh", persisted[1].RefreshToken);
        Assert.Equal(finalPersistenceFails ? CredentialRenewalStatus.Failed : CredentialRenewalStatus.Succeeded, result.Status);
        Assert.Same(persisted[finalPersistenceFails ? 0 : 1], lifecycle.CurrentCredentials);
        Assert.Equal(finalPersistenceFails, lifecycle.IsLocalKeyRefreshPending(lifecycle.CurrentCredentials!));
    }

    [Fact]
    public async Task AmbiguousReassociation_PreservesRotatedTokenWithoutInstallingNewIdentity()
    {
        var initial = Credentials() with { DeviceUniqueId = null };
        var persisted = new List<SonyCredentials>();
        using var handler = ReassociationHandler(Devices(Device()));
        using var client = new HttpClient(handler);
        var lifecycle = new SonyCredentialLifecycle(initial,
            (credentials, checkpoint, ct) => SonyOAuth.RefreshSessionKeysAsync(
                credentials, ct, httpClient: client, checkpointRotatedRefreshTokenAsync: checkpoint),
            (credentials, _) => { persisted.Add(credentials); return Task.CompletedTask; });

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.DeviceAssociationRequired, result.Status);
        var checkpoint = Assert.Single(persisted);
        Assert.Equal(OldDevice, checkpoint.DeviceId);
        Assert.Null(checkpoint.DeviceUniqueId);
        Assert.Equal(initial.KeyId, checkpoint.KeyId);
        Assert.Equal("sensitive-rotated-refresh", checkpoint.RefreshToken);
        Assert.Same(checkpoint, lifecycle.CurrentCredentials);
        Assert.True(lifecycle.IsLocalKeyRefreshPending(checkpoint));
    }

    [Fact]
    public async Task ConcurrentReassociation_SharesOneDiscoveryAndOneFinalSnapshot()
    {
        var initial = Credentials() with { DeviceUniqueId = null };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var select = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        using var handler = ReassociationHandler(Devices(Device()));
        using var client = new HttpClient(handler);
        var lifecycle = new SonyCredentialLifecycle(initial,
            (credentials, checkpoint, ct) => SonyOAuth.RefreshSessionKeysAsync(
                credentials, ct, httpClient: client, checkpointRotatedRefreshTokenAsync: checkpoint,
                reassociationSelector: async (_, token) =>
                {
                    entered.TrySetResult(true);
                    return await select.Task.WaitAsync(token);
                }),
            (_, _) => { writes++; return Task.CompletedTask; });

        var first = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        select.TrySetResult(NewDevice);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(CredentialRenewalStatus.Succeeded, result.Status));
        Assert.Same(results[0].Credentials, results[1].Credentials);
        Assert.Equal(2, writes);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(NewDevice, lifecycle.CurrentCredentials!.DeviceId);
    }

    private static SonyCredentials Credentials() => new()
    {
        DeviceId = OldDevice,
        DeviceUniqueId = UniqueDevice,
        KeyId = "old-key",
        HmacKey = "sensitive-old-hmac",
        RefreshToken = "sensitive-old-refresh"
    };

    private static object Device(string id = NewDevice, string? unique = UniqueDevice,
        string model = "HT-A9000", string type = "Speaker") => new
        {
            device_id = id,
            device_type = type,
            attributes = new { device_unique_id = unique, identified_model_name = model },
            device_infos = new { model_name = model, name = "sensitive-friendly-name" }
        };

    private static string Devices(params object[] devices) => JsonSerializer.Serialize(new { devices });

    private static StubHandler ReassociationHandler(string devices,
        HttpStatusCode discoveryStatus = HttpStatusCode.OK,
        HttpStatusCode replacementStatus = HttpStatusCode.OK) => new((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/token") return Task.FromResult(Json(HttpStatusCode.OK, TokenBody));
        Assert.Equal("Bearer sensitive-access", request.Headers.Authorization?.ToString());
        return Task.FromResult(path switch
        {
            $"/devices/{OldDevice}/session_keys" => Json(HttpStatusCode.NotFound, "{\"error\":\"sensitive-remote-error\"}"),
            "/devices" => Json(discoveryStatus, devices),
            $"/devices/{NewDevice}/session_keys" => Json(replacementStatus, KeysBody),
            _ => throw new Xunit.Sdk.XunitException("Unexpected cloud request.")
        });
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static void AssertSafe(IEnumerable<string> messages) => Assert.All(messages, message =>
    {
        Assert.DoesNotContain("sensitive-", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new-key", message, StringComparison.Ordinal);
        Assert.DoesNotContain("old-key", message, StringComparison.Ordinal);
    });

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return respond(request, cancellationToken);
        }
    }
}
