using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Wire;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Jp.Co.Sony.Hes.Ssh.Controldevice.V1;

namespace BraviaTheatre.Core.Engine;

public sealed class BraviaClient : IDisposable
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
    private string? _sessionId;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public BraviaClient(string host, int port, SonyCredentials credentials)
    {
        _creds = credentials;

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
        await _sessionLock.WaitAsync(ct);
        try
        {
            var req = new GetSessionRandomRequest { SessionId = _creds.SessionId };
            var resp = await _protoClient.GetSessionRandomAsync(req, cancellationToken: ct);

            _sessionRandom = resp.SessionRandom.ToByteArray();
            _sessionId = resp.SessionId;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<Dictionary<string, object?>> GetInitialStatesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessionRandom == null || _sessionId == null)
            {
                var reqRandom = new GetSessionRandomRequest { SessionId = _creds.SessionId };
                var respRandom = await _protoClient.GetSessionRandomAsync(reqRandom, cancellationToken: ct);
                _sessionRandom = respRandom.SessionRandom.ToByteArray();
                _sessionId = respRandom.SessionId;
            }

            var reqBytes = StatesCodec.BuildGetStatesRequest(
                _creds.HmacKey,
                paths,
                _sessionRandom,
                _sessionId);

            var call = _channel.CreateCallInvoker().AsyncUnaryCall(GetStatesMethod, null, new CallOptions(cancellationToken: ct), reqBytes);
            var respBytes = await call.ResponseAsync;

            var (newRandom, _, newSessionId) = StatesCodec.ExtractSessionTokens(respBytes);
            if (newRandom != null) _sessionRandom = newRandom;
            if (newSessionId != null) _sessionId = newSessionId;

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
        await _sessionLock.WaitAsync(ct);
        try
        {
            // Fresh session random is required before each ExecCommand
            var reqRandom = new GetSessionRandomRequest { SessionId = _creds.SessionId };
            var respRandom = await _protoClient.GetSessionRandomAsync(reqRandom, cancellationToken: ct);
            _sessionRandom = respRandom.SessionRandom.ToByteArray();
            _sessionId = respRandom.SessionId;

            var reqBytes = CommandBuilder.BuildExecCommandRequest(
                _creds.HmacKey,
                path,
                _sessionRandom,
                _sessionId,
                intValue,
                boolValue,
                stringValue);

            var call = _channel.CreateCallInvoker().AsyncUnaryCall(ExecCommandMethod, null, new CallOptions(cancellationToken: ct), reqBytes);
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
        var startReq = new StartNotifyStatesRequest { SessionId = _creds.SessionId };
        var bodyBytes = startReq.ToByteArray();

        return _channel.CreateCallInvoker().AsyncServerStreamingCall(NotifyMethod, null, new CallOptions(cancellationToken: ct), bodyBytes);
    }

    public void Dispose()
    {
        _sessionLock.Dispose();
        _channel.Dispose();
    }
}
