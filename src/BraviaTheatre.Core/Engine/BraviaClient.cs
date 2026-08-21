using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Wire;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Jp.Co.Sony.Hes.Ssh.Controldevice.V1;

namespace BraviaTheatre.Core.Engine;

public sealed class BraviaClient : IBraviaClient
{
    private const string ServiceName = "jp.co.sony.hes.ssh.controldevice.v1.ControlDeviceService";

    private static readonly Method<byte[], byte[]> ExecCommandMethod = new(
        MethodType.Unary,
        ServiceName,
        "ExecCommandWithAuth",
        Marshallers.Create(b => b, b => b),
        Marshallers.Create(b => b, b => b));

    private static readonly Method<byte[], byte[]> GetStatesMethod = new(
        MethodType.Unary,
        ServiceName,
        "GetStatesWithAuth",
        Marshallers.Create(b => b, b => b),
        Marshallers.Create(b => b, b => b));

    private static readonly Method<byte[], byte[]> NotifyMethod = new(
        MethodType.ServerStreaming,
        ServiceName,
        "StartNotifyStates",
        Marshallers.Create(b => b, b => b),
        Marshallers.Create(b => b, b => b));

    private readonly GrpcChannel _channel;
    private readonly ControlDeviceService.ControlDeviceServiceClient _protoClient;
    private readonly SonyCredentials _creds;

    private byte[]? _sessionRandom;
    private string _sessionId = string.Empty;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public Action<string>? LogAction { get; set; }

    public BraviaClient(string host, int port, SonyCredentials credentials)
    {
        _creds = credentials;
        _sessionId = credentials.SessionId;

        var httpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
        };

        var options = new GrpcChannelOptions
        {
            HttpHandler = httpHandler,
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize = 16 * 1024 * 1024
        };

        _channel = GrpcChannel.ForAddress($"http://{host}:{port}", options);
        _protoClient = new ControlDeviceService.ControlDeviceServiceClient(_channel);
    }

    public async Task InitializeSessionAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        await _sessionLock.WaitAsync(cts.Token);
        try
        {
            // 1. ConfirmSignin: SHA256 of device_id
            using var sha = SHA256.Create();
            var devIdBytes = Encoding.UTF8.GetBytes(_creds.DeviceId ?? string.Empty);
            var authData = sha.ComputeHash(devIdBytes);

            var signinReq = new ConfirmSigninRequest
            {
                AuthData = ByteString.CopyFrom(authData)
            };
            await _protoClient.ConfirmSigninAsync(signinReq, cancellationToken: cts.Token);

            // 2. ConfirmKeys: HMAC-SHA256 of session_id using hmac_key
            var sessId = _creds.SessionId;
            var sessIdBytes = Encoding.UTF8.GetBytes(sessId);
            var keyData = PacketSigner.ComputeHmac(_creds.HmacKey, sessIdBytes);

            var keysReq = new ConfirmKeysRequest
            {
                SessionId = sessId,
                KeyData = ByteString.CopyFrom(keyData)
            };
            await _protoClient.ConfirmKeysAsync(keysReq, cancellationToken: cts.Token);

            // 3. GetSessionRandom
            var req = new GetSessionRandomRequest { SessionId = sessId };
            var resp = await _protoClient.GetSessionRandomAsync(req, cancellationToken: cts.Token);

            ApplySessionRandomResponse(resp, sessId);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<Dictionary<string, object?>> GetInitialStatesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object?>();
        foreach (var path in paths)
        {
            try
            {
                var dict = await GetSingleStateAsync(path, ct);
                foreach (var (k, v) in dict)
                    result[k] = v;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (RpcException ex) when (IsUnsupportedPathStatus(ex.StatusCode))
            {
                LogAction?.Invoke($"[Query] Path '{path}' unsupported ({ex.StatusCode}).");
            }
        }
        return result;
    }

    public async Task<Dictionary<string, object?>> GetSingleStateAsync(string path, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await _sessionLock.WaitAsync(cts.Token);
        try
        {
            if (_sessionRandom == null || string.IsNullOrEmpty(_sessionId))
            {
                var requestSessionId = CurrentSessionId;
                var reqRandom = new GetSessionRandomRequest { SessionId = requestSessionId };
                var respRandom = await _protoClient.GetSessionRandomAsync(reqRandom, cancellationToken: cts.Token);
                ApplySessionRandomResponse(respRandom, requestSessionId);
            }

            var reqBytes = StatesCodec.BuildSingleGetStatesRequest(
                _creds.HmacKey,
                path,
                _sessionRandom ?? throw new InvalidDataException("Session random was not initialized."),
                _sessionId);

            var call = _channel.CreateCallInvoker().AsyncUnaryCall(GetStatesMethod, null, new CallOptions(cancellationToken: cts.Token), reqBytes);
            var respBytes = await call.ResponseAsync;

            var (newRandom, _, newSessionId) = StatesCodec.ExtractSessionTokens(respBytes);
            if (newRandom != null && newRandom.Length == 8) _sessionRandom = newRandom;
            if (!string.IsNullOrEmpty(newSessionId)) _sessionId = newSessionId;

            return StatesCodec.ParseGetStatesResponse(respBytes);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<bool> ExecCommandAsync(
        string path,
        int? intValue = null,
        bool? boolValue = null,
        string? stringValue = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await _sessionLock.WaitAsync(cts.Token);
        try
        {
            // Fresh session random is required before each ExecCommand
            var requestSessionId = CurrentSessionId;
            var reqRandom = new GetSessionRandomRequest { SessionId = requestSessionId };
            var respRandom = await _protoClient.GetSessionRandomAsync(reqRandom, cancellationToken: cts.Token);
            ApplySessionRandomResponse(respRandom, requestSessionId);

            var reqBytes = CommandBuilder.BuildExecCommandRequest(
                _creds.HmacKey,
                path,
                _sessionRandom ?? throw new InvalidDataException("Session random was not initialized."),
                _sessionId,
                intValue,
                boolValue,
                stringValue);

            var call = _channel.CreateCallInvoker().AsyncUnaryCall(ExecCommandMethod, null, new CallOptions(cancellationToken: cts.Token), reqBytes);
            var respBytes = await call.ResponseAsync;

            return CommandBuilder.ParseExecResponse(respBytes);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public AsyncServerStreamingCall<byte[]> StartNotifyStream(CancellationToken ct = default)
    {
        var startReq = new StartNotifyStatesRequest { SessionId = CurrentSessionId };
        var bodyBytes = startReq.ToByteArray();

        return _channel.CreateCallInvoker().AsyncServerStreamingCall(NotifyMethod, null, new CallOptions(cancellationToken: ct), bodyBytes);
    }

    async IAsyncEnumerable<byte[]> IBraviaClient.ReadNotificationsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var notifyStream = StartNotifyStream(ct);
        while (await notifyStream.ResponseStream.MoveNext(ct))
        {
            yield return notifyStream.ResponseStream.Current;
        }
    }

    public void Dispose()
    {
        _sessionLock.Dispose();
        _channel.Dispose();
    }

    internal string CurrentSessionId => string.IsNullOrEmpty(_sessionId) ? _creds.SessionId : _sessionId;

    internal static bool IsUnsupportedPathStatus(StatusCode statusCode)
    {
        return statusCode is StatusCode.InvalidArgument
            or StatusCode.NotFound
            or StatusCode.OutOfRange;
    }

    internal static string ResolveSessionId(string requestSessionId, string? responseSessionId)
    {
        return string.IsNullOrWhiteSpace(responseSessionId) ? requestSessionId : responseSessionId;
    }

    private void ApplySessionRandomResponse(GetSessionRandomResponse response, string requestSessionId)
    {
        if (response.SessionRandom.Length != 8)
            throw new InvalidDataException("GetSessionRandom returned an invalid session random.");

        _sessionRandom = response.SessionRandom.ToByteArray();
        _sessionId = ResolveSessionId(requestSessionId, response.SessionId);
    }
}
