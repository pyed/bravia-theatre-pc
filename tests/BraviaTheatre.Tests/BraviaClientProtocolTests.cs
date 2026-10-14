using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BraviaTheatre.Core;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Wire;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Jp.Co.Sony.Hes.Ssh.Controldevice.V1;

namespace BraviaTheatre.Tests;

/// <summary>
/// Runs the real client against a handler that speaks gRPC framing the way the soundbar does,
/// so the handshake, the rolling session tokens, and credential replacement are checked on the
/// wire rather than through a fake client.
/// </summary>
public class BraviaClientProtocolTests
{
    private static readonly SonyCredentials Original = Credentials("original", "11");
    private static readonly SonyCredentials Renewed = Credentials("renewed", "22");
    private static readonly byte[] FirstRandom = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] RollingRandom = [9, 9, 9, 9, 9, 9, 9, 9];

    [Fact]
    public async Task HandshakeProvesTheDeviceAndTheKeys()
    {
        var device = new FakeDevice();
        using var client = device.CreateClient(Original);

        await client.InitializeSessionAsync(TestContext.Current.CancellationToken);

        var signin = ConfirmSigninRequest.Parser.ParseFrom(device.Single("ConfirmSignin"));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(Original.DeviceId!)), signin.AuthData.ToByteArray());

        var keys = ConfirmKeysRequest.Parser.ParseFrom(device.Single("ConfirmKeys"));
        Assert.Equal(Original.SessionId, keys.SessionId);
        Assert.Equal(
            PacketSigner.ComputeHmac(Original.HmacKey, Encoding.UTF8.GetBytes(Original.SessionId)),
            keys.KeyData.ToByteArray());

        Assert.Equal(Original.SessionId, GetSessionRandomRequest.Parser.ParseFrom(device.Single("GetSessionRandom")).SessionId);
    }

    [Fact]
    public async Task StateQueriesUseTheSessionRandomAndAdoptTheRollingOne()
    {
        var device = new FakeDevice
        {
            StatesResponse = _ => StatesResponse(("volume", 33), RollingRandom, "rolled-session")
        };
        using var client = device.CreateClient(Original);
        await client.InitializeSessionAsync(TestContext.Current.CancellationToken);

        var first = await client.GetSingleStateAsync("volume", TestContext.Current.CancellationToken);
        await client.GetSingleStateAsync("volume", TestContext.Current.CancellationToken);

        Assert.Equal(33, first["volume"]);
        var requests = device.Requests("GetStatesWithAuth");
        Assert.Equal(2, requests.Length);
        AssertSignedQuery(requests[0], Original, FirstRandom, Original.SessionId);
        AssertSignedQuery(requests[1], Original, RollingRandom, "rolled-session");
    }

    [Fact]
    public async Task UnsupportedPathIsSkippedAndReportedOnce()
    {
        var device = new FakeDevice
        {
            StatesResponse = request => Contains(request, "sound_setting.volume.rear")
                ? throw new RpcException(new Status(StatusCode.InvalidArgument, "unsupported"))
                : StatesResponse(("volume", 12), null, null)
        };
        var logs = new ConcurrentQueue<(string Message, DiagnosticLevel Level)>();
        using var client = device.CreateClient(Original);
        client.LogAction = (message, level) => logs.Enqueue((message, level));
        await client.InitializeSessionAsync(TestContext.Current.CancellationToken);

        string[] paths = ["volume", "sound_setting.volume.rear"];
        var first = await client.GetInitialStatesAsync(paths, TestContext.Current.CancellationToken);
        var second = await client.GetInitialStatesAsync(paths, TestContext.Current.CancellationToken);

        Assert.Equal(["volume"], first.Keys);
        Assert.Equal(["volume"], second.Keys);
        var report = Assert.Single(logs);
        Assert.Contains("sound_setting.volume.rear", report.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticLevel.Verbose, report.Level);
    }

    [Fact]
    public async Task ReauthenticationSwitchesEveryLaterCallToTheRenewedKeys()
    {
        var device = new FakeDevice { StatesResponse = _ => StatesResponse(("volume", 5), null, null) };
        using var client = device.CreateClient(Original);
        await client.InitializeSessionAsync(TestContext.Current.CancellationToken);

        await client.ReauthenticateAsync(Renewed, TestContext.Current.CancellationToken);
        await client.GetSingleStateAsync("volume", TestContext.Current.CancellationToken);

        var keys = device.Requests("ConfirmKeys").Select(request => ConfirmKeysRequest.Parser.ParseFrom(request)).ToArray();
        Assert.Equal([Original.SessionId, Renewed.SessionId], keys.Select(request => request.SessionId));
        Assert.Equal(
            PacketSigner.ComputeHmac(Renewed.HmacKey, Encoding.UTF8.GetBytes(Renewed.SessionId)),
            keys[1].KeyData.ToByteArray());
        AssertSignedQuery(Assert.Single(device.Requests("GetStatesWithAuth")), Renewed, FirstRandom, Renewed.SessionId);
    }

    [Fact]
    public async Task RejectedReauthenticationKeepsTheWorkingKeys()
    {
        var device = new FakeDevice { StatesResponse = _ => StatesResponse(("volume", 5), null, null) };
        using var client = device.CreateClient(Original);
        await client.InitializeSessionAsync(TestContext.Current.CancellationToken);
        device.RejectConfirmKeysFor = Renewed.SessionId;

        var rejection = await Assert.ThrowsAsync<RpcException>(
            () => client.ReauthenticateAsync(Renewed, TestContext.Current.CancellationToken));
        await client.GetSingleStateAsync("volume", TestContext.Current.CancellationToken);

        Assert.Equal(StatusCode.InvalidArgument, rejection.StatusCode);
        AssertSignedQuery(Assert.Single(device.Requests("GetStatesWithAuth")), Original, FirstRandom, Original.SessionId);
    }

    [Fact]
    public async Task UnconfirmedSigninIsRecordedButNotFatal()
    {
        // Proto3 reads an absent field as false, so devices that never set it still work.
        var device = new FakeDevice { SigninSucceeds = false };
        var logs = new ConcurrentQueue<string>();
        using var client = device.CreateClient(Original);
        client.LogAction = (message, _) => logs.Enqueue(message);

        await client.InitializeSessionAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logs, message => message.Contains("ConfirmSignin did not report success", StringComparison.Ordinal));
        Assert.Single(device.Requests("GetSessionRandom"));
    }

    private static void AssertSignedQuery(byte[] request, SonyCredentials credentials, byte[] sessionRandom, string sessionId)
    {
        // Field 1 is the signed body (path plus session random and id); field 2 its HMAC.
        Assert.True(ProtobufWireCodec.TryDecodeField(request, 0, out var body, out var next));
        Assert.True(ProtobufWireCodec.TryDecodeField(request, next, out var signature, out _));
        Assert.Equal(PacketSigner.ComputeHmac(credentials.HmacKey, body!.BytesValue!), signature!.BytesValue);

        Assert.True(ProtobufWireCodec.TryDecodeField(body.BytesValue!, 0, out _, out var embeddedAt));
        Assert.True(ProtobufWireCodec.TryDecodeField(body.BytesValue!, embeddedAt, out var embedded, out _));
        Assert.True(ProtobufWireCodec.TryDecodeField(embedded!.BytesValue!, 0, out var random, out var idAt));
        Assert.True(ProtobufWireCodec.TryDecodeField(embedded.BytesValue!, idAt, out var id, out _));
        Assert.Equal(sessionRandom, random!.BytesValue);
        Assert.Equal(sessionId, Encoding.UTF8.GetString(id!.BytesValue!));
    }

    private static byte[] StatesResponse((string Path, int Value) state, byte[]? nextRandom, string? nextSessionId)
    {
        var entry = Concat(
            ProtobufWireCodec.LengthDelimited(1, Encoding.UTF8.GetBytes(state.Path)),
            ProtobufWireCodec.EncodeVarint((2 << 3) | 0),
            ProtobufWireCodec.EncodeVarint(state.Value));
        var envelope = ProtobufWireCodec.LengthDelimited(1, ProtobufWireCodec.LengthDelimited(1, entry));
        if (nextRandom != null)
            envelope = Concat(envelope, ProtobufWireCodec.LengthDelimited(2, nextRandom));
        if (nextSessionId != null)
            envelope = Concat(envelope, ProtobufWireCodec.LengthDelimited(3, Encoding.UTF8.GetBytes(nextSessionId)));
        return ProtobufWireCodec.LengthDelimited(2, envelope);
    }

    private static bool Contains(byte[] haystack, string text) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static SonyCredentials Credentials(string name, string keyByte) => new()
    {
        KeyId = $"session-{name}",
        HmacKey = string.Concat(Enumerable.Repeat(keyByte, 32)),
        DeviceId = "device-1",
        RefreshToken = $"refresh-{name}"
    };

    /// <summary>Answers unary gRPC calls with framed messages and grpc-status trailers.</summary>
    private sealed class FakeDevice : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(string Method, byte[] Body)> _calls = new();

        public bool SigninSucceeds { get; init; } = true;
        public string? RejectConfirmKeysFor { get; set; }
        public Func<byte[], byte[]> StatesResponse { get; init; } = _ => [];

        public BraviaClient CreateClient(SonyCredentials credentials) =>
            new("192.0.2.10", 55051, credentials, new GrpcChannelOptions { HttpHandler = this, DisposeHttpClient = true });

        public byte[][] Requests(string method) =>
            [.. _calls.Where(call => call.Method == method).Select(call => call.Body)];

        public byte[] Single(string method) => Assert.Single(Requests(method));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.AbsolutePath.Split('/')[^1];
            var framed = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var body = framed[5..];
            _calls.Enqueue((method, body));

            try
            {
                return Reply(StatusCode.OK, Respond(method, body));
            }
            catch (RpcException error)
            {
                return Reply(error.StatusCode, null);
            }
        }

        private byte[] Respond(string method, byte[] body) => method switch
        {
            "ConfirmSignin" => new ConfirmSigninResponse { Success = SigninSucceeds }.ToByteArray(),
            "ConfirmKeys" when ConfirmKeysRequest.Parser.ParseFrom(body).SessionId == RejectConfirmKeysFor =>
                throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown key")),
            "ConfirmKeys" => new ConfirmKeysResponse().ToByteArray(),
            "GetSessionRandom" => new GetSessionRandomResponse
            {
                SessionRandom = ByteString.CopyFrom(FirstRandom),
                SessionId = GetSessionRandomRequest.Parser.ParseFrom(body).SessionId
            }.ToByteArray(),
            "GetStatesWithAuth" => StatesResponse(body),
            _ => throw new RpcException(new Status(StatusCode.Unimplemented, method))
        };

        private static HttpResponseMessage Reply(StatusCode status, byte[]? message)
        {
            var content = message is null ? [] : new byte[5 + message.Length];
            if (message is not null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(1), (uint)message.Length);
                message.CopyTo(content, 5);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }
    }
}
