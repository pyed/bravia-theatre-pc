using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Discovery;
using BraviaTheatre.Core.Models;
using BraviaTheatre.Core.Wire;
using Grpc.Core;

namespace BraviaTheatre.Core.Engine;

public sealed class BraviaEngine : IDisposable, IAsyncDisposable
{
    private readonly record struct QueuedCommand(long Generation, string Path, object Value);
    private static readonly TimeSpan CredentialRenewalWindow = TimeSpan.FromMinutes(5);
    internal const string DefaultDeviceName = "BRAVIA Theatre";

    // The soundbar ends a local session about an hour after the handshake and then rejects every
    // state query, which used to drop the connection on the hour. Renewing well before that, on
    // the open connection, keeps it.
    internal static readonly TimeSpan DeviceSessionRenewalInterval = TimeSpan.FromMinutes(50);

    private static readonly string[] MonitoredPaths =
    {
        "power",
        "volume",
        "mute",
        "sound_setting.night_mode",
        "sound_setting.sound_field",
        "sound_setting.voice_mode",
        "sound_setting.volume.bass",
        "sound_setting.volume.rear",
        "playback_control.function",
        "playback_control.audio_format",
        "playback_control.audio_channel"
    };

    // Codec and channel are read from either path; a codec of "no audio" clears the channel too.
    private static readonly string[] CodecPaths =
        ["audio_output.stream_info.audio_format", "playback_control.audio_format"];
    private static readonly string[] ChannelPaths =
        ["audio_output.stream_info.channel_info", "playback_control.audio_channel"];

    /// <summary>A state field, the paths that report it, and how a reported value is applied.</summary>
    /// <param name="Apply">Returns null when the value cannot be used, leaving the field as it was.</param>
    private sealed record StateField(string Name, string[] Paths, Func<SoundbarState, object, SoundbarState?> Apply);

    // Snapshots and live notifications both read fields through this table, so a path or a
    // parsing rule cannot differ between the two (they had: only notifications knew
    // playback_control.power).
    private static readonly StateField[] StateFields =
    [
        new(nameof(SoundbarState.Power), ["power", "playback_control.power"],
            (state, value) => state with { Power = ParseBoolean(value) }),
        new(nameof(SoundbarState.Volume), ["volume"],
            (state, value) => int.TryParse(value.ToString(), out var volume) ? state with { Volume = volume } : null),
        new(nameof(SoundbarState.Mute), ["mute"],
            (state, value) => state with { Mute = ParseBoolean(value) }),
        new(nameof(SoundbarState.NightMode), ["sound_setting.night_mode"],
            (state, value) => state with { NightMode = ParseBoolean(value) }),
        new(nameof(SoundbarState.SoundField), ["sound_setting.sound_field"],
            (state, value) => state with { SoundField = ParseBoolean(value) }),
        new(nameof(SoundbarState.VoiceMode), ["sound_setting.voice_mode"],
            (state, value) => state with { VoiceMode = ParseBoolean(value) }),
        new(nameof(SoundbarState.Bass), ["sound_setting.volume.bass"],
            (state, value) => NormalizeBass(value) is { } bass ? state with { Bass = bass } : null),
        new(nameof(SoundbarState.RearLevel), ["sound_setting.volume.rear"],
            (state, value) => int.TryParse(value.ToString(), out var rear) ? state with { RearLevel = rear } : null),
        new(nameof(SoundbarState.Function), ["playback_control.function"],
            (state, value) => NormalizeFunction(value) is { } function ? state with { Function = function } : null)
    ];

    private static readonly Dictionary<string, StateField> StateFieldsByPath = StateFields
        .SelectMany(field => field.Paths.Select(fieldPath => (fieldPath, field)))
        .ToDictionary(entry => entry.fieldPath, entry => entry.field, StringComparer.Ordinal);

    internal static IEnumerable<string> MappedPaths =>
        StateFieldsByPath.Keys.Concat(CodecPaths).Concat(ChannelPaths);

    internal static IReadOnlyList<string> QueriedPaths => MonitoredPaths;

    private readonly string? _configuredHost;
    private readonly int _configuredPort;
    private readonly SonyCredentials _credentials;
    private readonly SonyCredentialLifecycle? _credentialLifecycle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, int, SonyCredentials, IBraviaClient> _clientFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task<DiscoveredDevice?>> _discoverAsync;
    private DiscoveredDevice? _lastDiscovered;

    private readonly object _lifecycleLock = new();
    private Task? _workerTask;
    private int _disposeRequested;
    private int _resourcesDisposed;
    private int _activeCommandDrainReaders;
    private int _maxConcurrentCommandDrainReaders;
    private long _nextConnectionGeneration;
    private SonyCredentials? _authenticationRequiredCredentials;
    private SonyCredentials? _deviceAssociationRequiredCredentials;
    private SonyCredentials? _refreshSuppressedCredentials;
    private SonyCredentials? _proactiveRenewalSuppressedCredentials;

    private readonly Channel<QueuedCommand> _cmdChannel = Channel.CreateUnbounded<QueuedCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly object _stateLock = new();
    private SoundbarState _currentState = SoundbarState.Disconnected;
    private long _activeConnectionGeneration;
    private long _stateRevision;
    private readonly Dictionary<string, long> _fieldRevisions = new();

    // Subscribers are invoked outside _stateLock, so two threads that mutate different
    // fields can otherwise deliver an older snapshot after a newer one. Every published
    // snapshot carries the sequence it was stamped with under _stateLock, and delivery
    // is serialized so a superseded snapshot is dropped instead of arriving late.
    private readonly object _publishLock = new();
    private long _publishSequence;
    private long _publishedSequence;

    public event Action<SoundbarState>? StateChanged;
    public Action<string, DiagnosticLevel>? LogAction { get; set; }

    public SoundbarState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    public BraviaEngine(SonyCredentials credentials, string? host = null, int port = 55051)
        : this(
            credentials,
            host,
            port,
            static (clientHost, clientPort, clientCredentials) => new BraviaClient(clientHost, clientPort, clientCredentials),
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    public BraviaEngine(SonyCredentialLifecycle credentialLifecycle, string? host = null, int port = 55051)
        : this(
            credentialLifecycle,
            host,
            port,
            static (clientHost, clientPort, clientCredentials) => new BraviaClient(clientHost, clientPort, clientCredentials),
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal BraviaEngine(
        SonyCredentials credentials,
        string? host,
        int port,
        Func<string, int, SonyCredentials, IBraviaClient> clientFactory,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task<DiscoveredDevice?>>? discoverAsync = null)
    {
        _credentials = credentials;
        _configuredHost = host;
        _configuredPort = port;
        _clientFactory = clientFactory;
        _delayAsync = delayAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _discoverAsync = discoverAsync ?? MdnsDiscovery.DiscoverAsync;
    }

    internal BraviaEngine(
        SonyCredentialLifecycle credentialLifecycle,
        string? host,
        int port,
        Func<string, int, SonyCredentials, IBraviaClient> clientFactory,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task<DiscoveredDevice?>>? discoverAsync = null)
    {
        ArgumentNullException.ThrowIfNull(credentialLifecycle);
        _credentialLifecycle = credentialLifecycle;
        _credentials = new SonyCredentials();
        _configuredHost = host;
        _configuredPort = port;
        _clientFactory = clientFactory;
        _delayAsync = delayAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _discoverAsync = discoverAsync ?? MdnsDiscovery.DiscoverAsync;
    }

    private void Log(string msg, DiagnosticLevel level)
    {
        try
        {
            LogAction?.Invoke($"[Engine] {msg}", level);
        }
        catch
        {
            // Logging must never affect engine lifecycle or cleanup.
        }
    }

    private SonyCredentials CurrentCredentials =>
        _credentialLifecycle?.CurrentCredentials ?? _credentials;

    private static bool IsNearingExpiry(SonyCredentials credentials) =>
        credentials.SessionKeysExpiresAtUtc is { } expiresAt
        && expiresAt <= DateTimeOffset.UtcNow + CredentialRenewalWindow;

    internal static string DescribeFailure(Exception error)
    {
        if (error is not RpcException rpcError)
            return error.GetType().Name;

        // A transport failure arrives as StatusCode=Unknown or Unavailable whatever happened.
        // The types underneath (never their messages, which carry addresses) tell a reset
        // connection from a stream the device ended or a request that timed out.
        var causes = new List<string>();
        for (var cause = rpcError.Status.DebugException; cause != null && causes.Count < 4; cause = cause.InnerException)
        {
            causes.Add(cause switch
            {
                System.Net.Sockets.SocketException socket => $"{cause.GetType().Name}({socket.SocketErrorCode})",
                System.Net.Http.HttpRequestException request => $"{cause.GetType().Name}({request.HttpRequestError})",
                System.Net.Http.HttpProtocolException protocol => $"{cause.GetType().Name}({protocol.ErrorCode})",
                _ => cause.GetType().Name
            });
        }

        return causes.Count == 0
            ? $"{nameof(RpcException)} (StatusCode={rpcError.StatusCode})"
            : $"{nameof(RpcException)} (StatusCode={rpcError.StatusCode}, Cause={string.Join(" <- ", causes)})";
    }

    private bool TryUseRenewalResult(
        CredentialRenewalResult renewal,
        ref SonyCredentials credentials,
        ref bool authRequired,
        bool authenticatedHandshakeRejected)
    {
        if (renewal.Status == CredentialRenewalStatus.Succeeded
            && renewal.Credentials != null)
        {
            credentials = renewal.Credentials;
            authRequired = false;
            _authenticationRequiredCredentials = null;
            _deviceAssociationRequiredCredentials = null;
            _refreshSuppressedCredentials = null;
            _proactiveRenewalSuppressedCredentials = IsNearingExpiry(credentials)
                ? credentials
                : null;
            Log(credentials.SessionKeysExpiresAtUtc is { } expiresAt
                ? $"Local session credentials renewed; expire at {expiresAt:O}."
                : "Local session credentials renewed; Sony did not provide an expiry.", DiagnosticLevel.Info);
            return true;
        }

        switch (renewal.Status)
        {
            case CredentialRenewalStatus.Unavailable:
                if (authenticatedHandshakeRejected)
                {
                    _authenticationRequiredCredentials = credentials;
                    authRequired = true;
                    Log("Silent credential renewal unavailable; interactive authentication required.", DiagnosticLevel.Critical);
                    return false;
                }

                authRequired = false;
                Log("Silent credential renewal unavailable; trying the current local credentials.", DiagnosticLevel.Info);
                return true;
            case CredentialRenewalStatus.AuthenticationRequired:
                _deviceAssociationRequiredCredentials = null;
                _authenticationRequiredCredentials = credentials;
                authRequired = true;
                Log("Sony authorization was rejected; interactive authentication required.", DiagnosticLevel.Critical);
                return false;
            case CredentialRenewalStatus.DeviceAssociationRequired:
                _deviceAssociationRequiredCredentials = CurrentCredentials;
                authRequired = false;
                Log("Sony soundbar association needs attention; account authorization remains available.", DiagnosticLevel.Critical);
                return false;
            case CredentialRenewalStatus.TransientFailure:
                authRequired = false;
                Log("Sony credential renewal is temporarily unavailable; retrying without requiring sign-in.", DiagnosticLevel.Info);
                return !authenticatedHandshakeRejected;
            default:
                authRequired = false;
                Log("Sony credential renewal failed; retrying without requiring sign-in.", DiagnosticLevel.Critical);
                return !authenticatedHandshakeRejected;
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            _workerTask ??= Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await WorkerLoopCoreAsync();
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Engine worker stopped unexpectedly: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
        }
        finally
        {
            SetDisconnectedState();
        }
    }

    private async Task WorkerLoopCoreAsync()
    {
        int backoffSec = 5;

        // Sticky across retry attempts once a handshake proves auth is required;
        // cleared by replacement credentials or a successful authenticated handshake.
        bool authRequired = false;

        while (!_cts.Token.IsCancellationRequested)
        {
            string host = _configuredHost ?? string.Empty;
            int port = _configuredPort;
            // A static host skips discovery, so there is no advertised name to show; use
            // the neutral product name rather than assuming a particular model.
            string deviceName = DefaultDeviceName;
            var reusingLastDiscovery = false;

            if (string.IsNullOrEmpty(host) && _lastDiscovered is { } known)
            {
                // A reconnect (the daily credential renewal, a network blip) almost always
                // finds the soundbar where it was, so try it there before multicasting and
                // probing the subnet again. A failed attempt forgets it.
                host = known.Host;
                port = known.Port;
                deviceName = known.Name;
                reusingLastDiscovery = true;
                Log($"Trying the last discovered soundbar at {host}:{port}.", DiagnosticLevel.Info);
            }
            else if (string.IsNullOrEmpty(host))
            {
                Log("Discovering soundbar on local network (mDNS + LAN probe)...", DiagnosticLevel.Verbose);
                var discovered = await _discoverAsync(TimeSpan.FromSeconds(6), _cts.Token);
                if (discovered != null)
                {
                    host = discovered.Host;
                    port = discovered.Port;
                    deviceName = discovered.Name;
                    Log($"Discovered soundbar at {host}:{port} ({deviceName})", DiagnosticLevel.Info);
                }
                else
                {
                    Log("Discovery returned no devices. Retrying...", DiagnosticLevel.Verbose);
                }
            }

            if (string.IsNullOrEmpty(host))
            {
                await _delayAsync(TimeSpan.FromSeconds(backoffSec), _cts.Token);
                backoffSec = Math.Min(backoffSec * 2, 60);
                continue;
            }

            var sessionEstablished = false;

            IBraviaClient? client = null;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var connectionTasks = new List<Task>();
            var connectionGeneration = Interlocked.Increment(ref _nextConnectionGeneration);
            var connectionFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var credentials = CurrentCredentials;
                var refreshedForHandshake = false;
                CredentialRenewalStatus? preflightRenewalStatus = null;
                var suppressKeepaliveRenewal = false;
                var readyToConnect = true;

                if (_authenticationRequiredCredentials != null
                    && !ReferenceEquals(credentials, _authenticationRequiredCredentials))
                {
                    _authenticationRequiredCredentials = null;
                    authRequired = false;
                }
                if (!ReferenceEquals(credentials, _refreshSuppressedCredentials))
                    _refreshSuppressedCredentials = null;
                if (!ReferenceEquals(credentials, _deviceAssociationRequiredCredentials))
                    _deviceAssociationRequiredCredentials = null;
                if (!ReferenceEquals(credentials, _proactiveRenewalSuppressedCredentials))
                    _proactiveRenewalSuppressedCredentials = null;

                if (ReferenceEquals(credentials, _authenticationRequiredCredentials))
                {
                    authRequired = true;
                    readyToConnect = false;
                }
                else if (ReferenceEquals(credentials, _deviceAssociationRequiredCredentials))
                {
                    // Only replacement credentials can resolve this snapshot, so retrying would
                    // re-exchange the refresh token and re-read the device list on every backoff
                    // to reach the same decision that is waiting on the user.
                    authRequired = false;
                    readyToConnect = false;
                }
                else if (_credentialLifecycle != null
                    && IsNearingExpiry(credentials)
                    && !ReferenceEquals(credentials, _proactiveRenewalSuppressedCredentials))
                {
                    Log("[Credential renewal] Trigger=proactive; local credentials nearing expiry; refreshing before authenticated connect.", DiagnosticLevel.Critical);
                    var renewal = await _credentialLifecycle.RefreshAsync(credentials, connectionCts.Token);
                    preflightRenewalStatus = renewal.Status;
                    readyToConnect = TryUseRenewalResult(
                        renewal,
                        ref credentials,
                        ref authRequired,
                        authenticatedHandshakeRejected: false);
                    refreshedForHandshake = renewal.Status == CredentialRenewalStatus.Succeeded
                        && !_credentialLifecycle.IsLocalKeyRefreshPending(credentials);
                    suppressKeepaliveRenewal = renewal.Status != CredentialRenewalStatus.Succeeded;
                }

                var initialized = false;
                while (readyToConnect && !initialized)
                {
                    if (_credentialLifecycle?.CurrentCredentials is { } latestCredentials
                        && !ReferenceEquals(credentials, latestCredentials))
                    {
                        credentials = latestCredentials;
                        _authenticationRequiredCredentials = null;
                        authRequired = false;
                        if (!ReferenceEquals(credentials, _refreshSuppressedCredentials))
                            _refreshSuppressedCredentials = null;
                        if (!ReferenceEquals(credentials, _proactiveRenewalSuppressedCredentials))
                            _proactiveRenewalSuppressedCredentials = null;
                        refreshedForHandshake = !_credentialLifecycle.IsLocalKeyRefreshPending(credentials);
                    }

                    Log($"Connecting gRPC channel to {host}:{port}...", DiagnosticLevel.Info);
                    client = _clientFactory(host, port, credentials);
                    client.LogAction = Log;

                    try
                    {
                        await client.InitializeSessionAsync(connectionCts.Token);
                        authRequired = false;
                        if (ReferenceEquals(credentials, _refreshSuppressedCredentials))
                            _refreshSuppressedCredentials = null;
                        initialized = true;
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
                    {
                        if (!credentials.IsValid)
                        {
                            Log("Authenticated handshake failed: StatusCode=InvalidArgument, Classification=request_failure (credentials incomplete).", DiagnosticLevel.Critical);
                            throw;
                        }

                        Log("Authenticated handshake rejected with InvalidArgument.", DiagnosticLevel.Critical);
                        if (_credentialLifecycle == null)
                        {
                            _authenticationRequiredCredentials = credentials;
                            authRequired = true;
                            Log("Silent credential renewal unavailable; interactive authentication required.", DiagnosticLevel.Critical);
                            break;
                        }

                        if (refreshedForHandshake)
                        {
                            _refreshSuppressedCredentials = credentials;
                            authRequired = false;
                            Log("Authenticated handshake rejected renewed credentials; Classification=request_failure. Further refresh is suppressed for this credential snapshot.", DiagnosticLevel.Critical);
                            break;
                        }

                        if (ReferenceEquals(credentials, _refreshSuppressedCredentials))
                        {
                            authRequired = false;
                            Log("Authenticated handshake still rejects the current renewed credentials; Classification=request_failure. Retrying without another refresh.", DiagnosticLevel.Info);
                            break;
                        }

                        if (preflightRenewalStatus is { } preflightStatus)
                        {
                            if (preflightStatus == CredentialRenewalStatus.Unavailable)
                            {
                                _authenticationRequiredCredentials = credentials;
                                authRequired = true;
                                Log("Authenticated handshake rejected and silent renewal is unavailable; interactive authentication required.", DiagnosticLevel.Critical);
                            }
                            else
                            {
                                authRequired = false;
                                Log("Authenticated handshake rejected after a failed preflight renewal; retrying later without requiring sign-in.", DiagnosticLevel.Critical);
                            }
                            break;
                        }

                        Log("[Credential renewal] Trigger=reactive; authenticated handshake rejected; attempting one credential refresh.", DiagnosticLevel.Critical);
                        var renewal = await _credentialLifecycle.RefreshAsync(credentials, connectionCts.Token);
                        if (!TryUseRenewalResult(
                                renewal,
                                ref credentials,
                                ref authRequired,
                                authenticatedHandshakeRejected: true))
                            break;

                        refreshedForHandshake = renewal.Status == CredentialRenewalStatus.Succeeded
                            && !_credentialLifecycle.IsLocalKeyRefreshPending(credentials);
                        try
                        {
                            client.Dispose();
                        }
                        catch (Exception disposeError)
                        {
                            Log($"Rejected client dispose warning: {DescribeFailure(disposeError)}", DiagnosticLevel.Info);
                        }
                        client = null;
                    }
                    catch (RpcException ex)
                    {
                        var classification = ex.StatusCode switch
                        {
                            StatusCode.Unavailable => "network_failure",
                            StatusCode.NotFound or StatusCode.OutOfRange => "request_failure",
                            _ => "unknown"
                        };
                        Log($"Authenticated handshake failed: StatusCode={ex.StatusCode}, Classification={classification}.", DiagnosticLevel.Critical);
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A malformed device response (for example a session random of the
                        // wrong length, or an hmac_key that is not usable) is not a transport
                        // fault. Classifying it here keeps it out of the generic connection
                        // handler, which reported only an exception type name and gave no
                        // indication that the device, not the network, is at fault.
                        Log($"Authenticated handshake failed: ExceptionType={DescribeFailure(ex)}, Classification=protocol_failure. Retrying without requiring sign-in.", DiagnosticLevel.Critical);
                        authRequired = false;
                        break;
                    }
                }

                if (initialized && client != null)
                {
                    Log("Full security handshake completed (ConfirmSignin + ConfirmKeys).", DiagnosticLevel.Info);
                    sessionEstablished = true;
                    if (string.IsNullOrEmpty(_configuredHost))
                        _lastDiscovered = new DiscoveredDevice(host, port, deviceName);

                    ActivateConnection(connectionGeneration, deviceName);

                    var notifyStream = new NotifyStreamGate();
                    var notifyTask = NotifyLoopAsync(client, connectionGeneration, notifyStream, connectionCts.Token);
                    connectionTasks.Add(notifyTask);
                    Log("Live notify stream started.", DiagnosticLevel.Info);

                    var initialSnapshotTask = InitialSnapshotAsync(
                        client,
                        deviceName,
                        connectionGeneration,
                        connectionFailure,
                        connectionCts.Token);
                    connectionTasks.Add(initialSnapshotTask);

                    // Start command drain loop and keepalive loop
                    var cmdDrainTask = CommandDrainLoopAsync(client, connectionGeneration, connectionCts.Token);
                    connectionTasks.Add(cmdDrainTask);

                    var keepAliveTask = KeepAlivePollLoopAsync(
                        client,
                        deviceName,
                        connectionGeneration,
                        suppressKeepaliveRenewal,
                        notifyStream,
                        connectionCts.Token);
                    connectionTasks.Add(keepAliveTask);

                    var connectionEnded = Task.WhenAny(notifyTask, cmdDrainTask, keepAliveTask, connectionFailure.Task);
                    if (await Task.WhenAny(initialSnapshotTask, connectionEnded) == initialSnapshotTask
                        && await initialSnapshotTask)
                    {
                        backoffSec = 5;
                        await connectionEnded;
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Connection/Stream error: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
            }
            finally
            {
                if (reusingLastDiscovery && !sessionEstablished)
                    _lastDiscovered = null;

                try
                {
                    connectionCts.Cancel();
                }
                catch (Exception ex)
                {
                    Log($"Connection cancellation warning: {DescribeFailure(ex)}", DiagnosticLevel.Info);
                }

                SetDisconnectedState(connectionGeneration, authRequired,
                    ReferenceEquals(CurrentCredentials, _deviceAssociationRequiredCredentials));

                try
                {
                    await AwaitConnectionTasksAsync(connectionTasks, connectionCts.Token);
                }
                finally
                {
                    try
                    {
                        client?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log($"Client dispose warning: {DescribeFailure(ex)}", DiagnosticLevel.Info);
                    }
                }
            }

            if (!_cts.Token.IsCancellationRequested)
            {
                await _delayAsync(TimeSpan.FromSeconds(backoffSec), _cts.Token);
                backoffSec = Math.Min(backoffSec * 2, 60);
            }
        }
    }

    internal Task Completion
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _workerTask ?? Task.CompletedTask;
            }
        }
    }

    internal int ActiveCommandDrainReaders => Volatile.Read(ref _activeCommandDrainReaders);
    internal int MaxConcurrentCommandDrainReaders => Volatile.Read(ref _maxConcurrentCommandDrainReaders);
    internal long ActiveConnectionGeneration
    {
        get { lock (_stateLock) return _activeConnectionGeneration; }
    }

    private async Task NotifyLoopAsync(
        IBraviaClient client,
        long connectionGeneration,
        NotifyStreamGate notifyStream,
        CancellationToken ct)
    {
        try
        {
            var reopened = false;
            while (true)
            {
                var stream = await notifyStream.OpenStreamAsync(ct);
                if (reopened) Log("Live notify stream started on the renewed session.", DiagnosticLevel.Info);

                try
                {
                    await foreach (var msgBytes in client.ReadNotificationsAsync(stream.Token))
                    {
                        stream.Token.ThrowIfCancellationRequested();
                        var (path, value) = NotifyParser.ParseNotifyMessage(msgBytes);

                        if (!string.IsNullOrEmpty(path))
                        {
                            ApplyDeltaForConnection(connectionGeneration, path, value);
                        }
                    }

                    return;
                }
                catch (Exception) when (stream.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // A device-session renewal closed this stream; the next one binds to the
                    // renewed session.
                    reopened = true;
                }
                finally
                {
                    notifyStream.CloseStream(stream);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            // gRPC reports the engine's own cancellation this way; it is not a failure.
        }
        catch (Exception ex)
        {
            Log($"Notify stream ended: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
        }
    }

    private async Task<bool> InitialSnapshotAsync(
        IBraviaClient client,
        string deviceName,
        long connectionGeneration,
        TaskCompletionSource<bool> connectionFailure,
        CancellationToken ct)
    {
        try
        {
            var baselineRevision = CaptureSnapshotRevision(connectionGeneration);
            if (baselineRevision == null) return false;

            var initialStates = await client.GetInitialStatesAsync(MonitoredPaths, ct);
            ct.ThrowIfCancellationRequested();

            if (initialStates.Count == 0)
            {
                Log("Initial snapshot returned no monitored state.", DiagnosticLevel.Critical);
                connectionFailure.TrySetResult(true);
                return false;
            }

            Log($"Snapshot received ({initialStates.Count} paths). Applying state...", DiagnosticLevel.Verbose);
            ApplySnapshotForConnection(initialStates, deviceName, connectionGeneration, baselineRevision.Value);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log($"Initial snapshot warning: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
            connectionFailure.TrySetResult(true);
            return false;
        }
    }

    private async Task AwaitConnectionTasksAsync(IReadOnlyCollection<Task> tasks, CancellationToken connectionToken)
    {
        if (tasks.Count == 0) return;

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"Connection worker cleanup warning: {DescribeFailure(ex)}", DiagnosticLevel.Info);
        }
    }

    private void ActivateConnection(long connectionGeneration, string deviceName)
    {
        SoundbarState? connectedState = null;
        var sequence = 0L;

        lock (_stateLock)
        {
            _activeConnectionGeneration = connectionGeneration;
            _fieldRevisions.Clear();
            _stateRevision++;

            var next = _currentState with { Connected = true, DeviceName = deviceName, AuthRequired = false, DeviceAssociationRequired = false };
            if (next != _currentState)
            {
                _currentState = next;
                connectedState = next;
                sequence = ++_publishSequence;
            }
        }

        if (connectedState != null)
        {
            PublishState(connectedState, sequence);
        }
    }

    private void SetDisconnectedState(long? connectionGeneration = null, bool authRequired = false, bool deviceAssociationRequired = false)
    {
        SoundbarState? disconnectedState = null;
        var sequence = 0L;

        lock (_stateLock)
        {
            // Never clobber a state owned by a different, still-active connection,
            // but a connection that never activated (e.g. handshake rejected)
            // must still be able to publish the auth-required flag.
            if (connectionGeneration.HasValue
                && _activeConnectionGeneration != 0
                && _activeConnectionGeneration != connectionGeneration.Value)
                return;

            _activeConnectionGeneration = 0;
            _fieldRevisions.Clear();
            _stateRevision++;

            var disconnected = SoundbarState.Disconnected with
            {
                AuthRequired = authRequired,
                DeviceAssociationRequired = !authRequired && deviceAssociationRequired
            };
            if (_currentState != disconnected)
            {
                _currentState = disconnected;
                disconnectedState = _currentState;
                sequence = ++_publishSequence;
            }
        }

        if (disconnectedState != null)
        {
            PublishState(disconnectedState, sequence);
        }
    }

    private long? CaptureSnapshotRevision(long connectionGeneration)
    {
        lock (_stateLock)
        {
            return _activeConnectionGeneration == connectionGeneration
                ? _stateRevision
                : null;
        }
    }

    private void PublishState(SoundbarState state, long sequence)
    {
        lock (_publishLock)
        {
            if (sequence <= _publishedSequence)
                return;
            _publishedSequence = sequence;

            var handlers = StateChanged;
            if (handlers == null) return;

            foreach (Action<SoundbarState> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(state);
                }
                catch (Exception ex)
                {
                    Log($"StateChanged subscriber warning: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
                }
            }
        }
    }

    private async Task CommandDrainLoopAsync(IBraviaClient client, long connectionGeneration, CancellationToken ct)
    {
        var activeReaders = Interlocked.Increment(ref _activeCommandDrainReaders);
        UpdateMaximum(ref _maxConcurrentCommandDrainReaders, activeReaders);

        try
        {
            await CommandDrainCoreAsync(client, connectionGeneration, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCommandDrainReaders);
        }
    }

    private async Task CommandDrainCoreAsync(IBraviaClient client, long connectionGeneration, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _cmdChannel.Reader.WaitToReadAsync(ct))
                    break;

                while (_cmdChannel.Reader.TryRead(out var cmd))
                {
                    if (cmd.Generation != connectionGeneration)
                        continue;

                    var path = cmd.Path;
                    var value = cmd.Value;

                    // A slider drag queues a command per step, each costing two round trips;
                    // only the last value of a run of the same slider matters.
                    if (path is "volume" or "sound_setting.volume.rear")
                    {
                        while (_cmdChannel.Reader.TryPeek(out var next)
                            && next.Generation == connectionGeneration
                            && next.Path == path)
                        {
                            _cmdChannel.Reader.TryRead(out cmd);
                            value = cmd.Value;
                        }
                    }

                    if (ct.IsCancellationRequested || !IsConnectionActive(connectionGeneration))
                        break;

                    try
                    {
                        bool succeeded;
                        if (value is int intVal)
                        {
                            succeeded = await client.ExecCommandAsync(path, intValue: intVal, ct: ct);
                        }
                        else if (value is bool boolVal)
                        {
                            succeeded = await client.ExecCommandAsync(path, boolValue: boolVal, ct: ct);
                        }
                        else if (value is string strVal)
                        {
                            succeeded = await client.ExecCommandAsync(path, stringValue: strVal, ct: ct);
                        }
                        else
                        {
                            Log($"Command '{path}' has an unsupported value type.", DiagnosticLevel.Critical);
                            return;
                        }

                        if (!succeeded)
                        {
                            Log($"Command '{path}' was rejected by the device.", DiagnosticLevel.Info);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Command '{path}' exec error: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Command drain error: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
                return;
            }
        }
    }

    private bool IsConnectionActive(long connectionGeneration)
    {
        lock (_stateLock)
        {
            return _activeConnectionGeneration == connectionGeneration;
        }
    }

    private async Task KeepAlivePollLoopAsync(
        IBraviaClient client,
        string deviceName,
        long connectionGeneration,
        bool suppressCredentialRenewal,
        NotifyStreamGate notifyStream,
        CancellationToken ct)
    {
        var sessionStarted = _timeProvider.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delayAsync(TimeSpan.FromSeconds(25), ct);
                ct.ThrowIfCancellationRequested();

                var credentials = CurrentCredentials;
                if (!suppressCredentialRenewal
                    && _credentialLifecycle != null
                    && IsNearingExpiry(credentials)
                    && !ReferenceEquals(credentials, _proactiveRenewalSuppressedCredentials))
                {
                    switch (await RenewCredentialsInPlaceAsync(_credentialLifecycle, client, credentials, notifyStream, ct))
                    {
                        case InPlaceRenewal.Renewed:
                            sessionStarted = _timeProvider.GetTimestamp();
                            break;
                        case InPlaceRenewal.KeepCurrent:
                            // As after a failed renewal before connecting: the current keys work
                            // until they expire, and the device's rejection then starts a reactive
                            // renewal. Retrying on every poll would only repeat the failure.
                            suppressCredentialRenewal = true;
                            break;
                        default:
                            Log("Local credentials nearing expiry; reconnecting for renewal.", DiagnosticLevel.Info);
                            return;
                    }
                }

                if (_timeProvider.GetElapsedTime(sessionStarted) >= DeviceSessionRenewalInterval)
                {
                    Log("Device session is due for renewal.", DiagnosticLevel.Verbose);
                    await RenewDeviceSessionAsync(client, notifyStream, ct);
                    sessionStarted = _timeProvider.GetTimestamp();
                }

                var baselineRevision = CaptureSnapshotRevision(connectionGeneration);
                if (baselineRevision == null) return;

                var snapshot = await client.GetInitialStatesAsync(MonitoredPaths, ct);
                ct.ThrowIfCancellationRequested();

                if (snapshot.Count == 0)
                {
                    // Every path was rejected, which means the device ended the session. Renew it
                    // once on the open connection; if that does not restore it, reconnect.
                    Log("Device rejected every state query; renewing the device session.", DiagnosticLevel.Info);
                    await RenewDeviceSessionAsync(client, notifyStream, ct);
                    sessionStarted = _timeProvider.GetTimestamp();

                    baselineRevision = CaptureSnapshotRevision(connectionGeneration);
                    if (baselineRevision == null) return;

                    snapshot = await client.GetInitialStatesAsync(MonitoredPaths, ct);
                    ct.ThrowIfCancellationRequested();
                }

                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Keepalive returned no monitored state.");

                ApplySnapshotForConnection(snapshot, deviceName, connectionGeneration, baselineRevision.Value);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                // gRPC reports the engine's own cancellation this way; it is not a failure.
                break;
            }
            catch (Exception ex)
            {
                Log($"Keepalive poll failed: {DescribeFailure(ex)}", DiagnosticLevel.Critical);
                throw;
            }
        }
    }

    private enum InPlaceRenewal
    {
        Renewed,
        KeepCurrent,
        Reconnect
    }

    // Renewing the daily credentials used to end the connection and build a new one, so the
    // soundbar showed as disconnected once a day. The device accepts a new handshake on the open
    // connection, as the hourly session renewal relies on, so renew and repeat the handshake
    // there. Any outcome but a clean success leads to the reconnect path, which handles them all.
    private async Task<InPlaceRenewal> RenewCredentialsInPlaceAsync(
        SonyCredentialLifecycle lifecycle,
        IBraviaClient client,
        SonyCredentials credentials,
        NotifyStreamGate notifyStream,
        CancellationToken ct)
    {
        Log("[Credential renewal] Trigger=proactive; local credentials nearing expiry; renewing on the open connection.", DiagnosticLevel.Critical);
        var renewal = await lifecycle.RefreshAsync(credentials, ct);
        var renewed = credentials;
        var authRequired = false;

        // Sign-in or a soundbar choice is needed; ending the connection reports it.
        if (!TryUseRenewalResult(renewal, ref renewed, ref authRequired, authenticatedHandshakeRejected: false))
            return InPlaceRenewal.Reconnect;
        if (renewal.Status != CredentialRenewalStatus.Succeeded || lifecycle.IsLocalKeyRefreshPending(renewed))
            return InPlaceRenewal.KeepCurrent;

        // The notification stream belongs to the old session, as in a device-session renewal.
        notifyStream.BeginRenewal();
        try
        {
            await client.ReauthenticateAsync(renewed, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log($"[Credential renewal] The handshake with renewed credentials failed on the open connection ({DescribeFailure(ex)}); reconnecting with them.", DiagnosticLevel.Critical);
            return InPlaceRenewal.Reconnect;
        }
        finally
        {
            notifyStream.EndRenewal();
        }

        Log("[Credential renewal] Handshake completed with the renewed credentials on the open connection.", DiagnosticLevel.Critical);
        return InPlaceRenewal.Renewed;
    }

    private async Task RenewDeviceSessionAsync(
        IBraviaClient client,
        NotifyStreamGate notifyStream,
        CancellationToken ct)
    {
        // The notification stream is bound to the old session, so close it before the handshake
        // and let its replacement open once the new session exists.
        notifyStream.BeginRenewal();
        try
        {
            await client.InitializeSessionAsync(ct);
        }
        finally
        {
            notifyStream.EndRenewal();
        }

        Log("Device session renewed; handshake completed on the open connection.", DiagnosticLevel.Info);
    }

    internal void ApplySnapshot(Dictionary<string, object?> snapshot, string deviceName) =>
        ApplySnapshotCore(snapshot, deviceName, null, long.MaxValue);

    internal void ApplySnapshotForConnection(
        Dictionary<string, object?> snapshot,
        string deviceName,
        long connectionGeneration,
        long baselineRevision) =>
        ApplySnapshotCore(snapshot, deviceName, connectionGeneration, baselineRevision);

    private void ApplySnapshotCore(
        Dictionary<string, object?> snapshot,
        string deviceName,
        long? connectionGeneration,
        long baselineRevision)
    {
        SoundbarState? stateToPublish = null;
        var sequence = 0L;

        lock (_stateLock)
        {
            if (connectionGeneration.HasValue
                && _activeConnectionGeneration != connectionGeneration.Value)
                return;

            var cur = _currentState;
            var next = cur with { Connected = true, DeviceName = deviceName };
            var appliedFields = new List<string>();

            bool CanApply(string field) => baselineRevision == long.MaxValue
                || !_fieldRevisions.TryGetValue(field, out var revision)
                || revision <= baselineRevision;

            foreach (var field in StateFields)
            {
                if (!TryGetFirst(snapshot, field.Paths, out var value)
                    || value is null
                    || !CanApply(field.Name)
                    || field.Apply(next, value) is not { } applied)
                {
                    continue;
                }

                next = applied;
                appliedFields.Add(field.Name);
            }

            var codec = next.Codec;
            var channel = next.Channel;
            bool codecPresent = TryGetFirst(snapshot, CodecPaths, out var codecValue);
            bool noAudioApplied = false;

            if (codecPresent && CanApply(nameof(SoundbarState.Codec)))
            {
                var normalizedCodec = NormalizeAudioValue(codecValue);
                if (normalizedCodec != null)
                {
                    codec = normalizedCodec;
                    appliedFields.Add(nameof(SoundbarState.Codec));
                }
                else if (CanApply(nameof(SoundbarState.Channel)))
                {
                    codec = null;
                    channel = null;
                    noAudioApplied = true;
                    appliedFields.Add(nameof(SoundbarState.Codec));
                    appliedFields.Add(nameof(SoundbarState.Channel));
                }
            }

            bool channelPresent = TryGetFirst(snapshot, ChannelPaths, out var channelValue);

            if (channelPresent && !noAudioApplied && CanApply(nameof(SoundbarState.Channel)))
            {
                channel = NormalizeAudioValue(channelValue);
                appliedFields.Add(nameof(SoundbarState.Channel));
            }

            MarkFieldsLocked(appliedFields);

            next = next with { Codec = codec, Channel = channel };

            if (next != cur)
            {
                _currentState = next;
                stateToPublish = next;
                sequence = ++_publishSequence;
            }
        }

        if (stateToPublish != null)
            PublishState(stateToPublish, sequence);
    }

    internal void ApplyDelta(string path, object? value) => ApplyDeltaCore(null, path, value);

    internal void ApplyDeltaForConnection(long connectionGeneration, string path, object? value) =>
        ApplyDeltaCore(connectionGeneration, path, value);

    private void ApplyDeltaCore(long? connectionGeneration, string path, object? value)
    {
        SoundbarState? stateToPublish = null;
        var sequence = 0L;

        lock (_stateLock)
        {
            if (connectionGeneration.HasValue
                && _activeConnectionGeneration != connectionGeneration.Value)
                return;

            var cur = _currentState;
            var next = cur;
            var updatedFields = new List<string>();

            if (CodecPaths.Contains(path))
            {
                var codec = NormalizeAudioValue(value);
                next = codec == null
                    ? cur with { Codec = null, Channel = null }
                    : cur with { Codec = codec };
                updatedFields.Add(nameof(SoundbarState.Codec));
                if (codec == null) updatedFields.Add(nameof(SoundbarState.Channel));
            }
            else if (ChannelPaths.Contains(path))
            {
                next = cur with { Channel = NormalizeAudioValue(value) };
                updatedFields.Add(nameof(SoundbarState.Channel));
            }
            else if (value != null
                && StateFieldsByPath.TryGetValue(path, out var field)
                && field.Apply(cur, value) is { } applied)
            {
                next = applied;
                updatedFields.Add(field.Name);
            }
            else
            {
                return;
            }

            MarkFieldsLocked(updatedFields);
            if (next != cur)
            {
                _currentState = next;
                stateToPublish = next;
                sequence = ++_publishSequence;
            }
        }

        if (stateToPublish != null)
            PublishState(stateToPublish, sequence);
    }

    private void MarkFieldsLocked(IEnumerable<string> fields)
    {
        long? revision = null;
        foreach (var field in fields)
        {
            revision ??= ++_stateRevision;
            _fieldRevisions[field] = revision.Value;
        }
    }

    private static bool TryGetFirst(Dictionary<string, object?> snapshot, string[] paths, out object? value)
    {
        foreach (var candidate in paths)
        {
            if (snapshot.TryGetValue(candidate, out value))
                return true;
        }

        value = null;
        return false;
    }

    private static string? NormalizeBass(object value) =>
        value.ToString()?.Trim().ToLowerInvariant() is { } bass and ("min" or "mid" or "max") ? bass : null;

    private static string? NormalizeFunction(object value)
    {
        var function = value.ToString()?.Trim();
        return string.IsNullOrEmpty(function)
            || function.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || function.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null
                : function;
    }

    private static bool ParseBoolean(object value)
    {
        if (value is bool boolean) return boolean;
        var text = value.ToString()?.Trim();
        return text is not null
            && (text.Equals("on", StringComparison.OrdinalIgnoreCase)
                || text.Equals("active", StringComparison.OrdinalIgnoreCase)
                || text == "1"
                || text.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static string? NormalizeAudioValue(object? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        return text.Equals("NoAudio", StringComparison.OrdinalIgnoreCase)
            || text.Equals("NoChannel", StringComparison.OrdinalIgnoreCase)
            || text.Equals("none", StringComparison.OrdinalIgnoreCase)
            || text.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || text.Equals("invalid", StringComparison.OrdinalIgnoreCase)
                ? null
                : text;
    }

    public Task<bool> SetVolumeAsync(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        return Task.FromResult(TryQueueCommand(
            "volume",
            state => !state.Power || state.Volume == volume
                ? null
                : (state with { Volume = volume, Mute = false }, volume),
            nameof(SoundbarState.Volume),
            nameof(SoundbarState.Mute)));
    }

    public Task<bool> SetBassAsync(string level)
    {
        if (string.IsNullOrWhiteSpace(level)) return Task.FromResult(false);
        level = level.Trim().ToLowerInvariant();
        if (level != "min" && level != "mid" && level != "max") return Task.FromResult(false);

        return Task.FromResult(TryQueueCommand(
            "sound_setting.volume.bass",
            state => !state.Power || state.Bass == level
                ? null
                : (state with { Bass = level }, level),
            nameof(SoundbarState.Bass)));
    }

    public Task<bool> SetFunctionAsync(string function)
    {
        if (string.IsNullOrWhiteSpace(function)) return Task.FromResult(false);
        function = function.Trim().ToLowerInvariant();
        return Task.FromResult(TryQueueCommand(
            "playback_control.function",
            state => !state.Power || state.Function == function
                ? null
                : (state with { Function = function }, function),
            nameof(SoundbarState.Function)));
    }

    public Task<bool> SetRearLevelAsync(int level)
    {
        level = Math.Clamp(
            level,
            BraviaControlRanges.MinimumRearLevel,
            BraviaControlRanges.MaximumRearLevel);
        return Task.FromResult(TryQueueCommand(
            "sound_setting.volume.rear",
            state => !state.Power || state.RearLevel == level
                ? null
                : (state with { RearLevel = level }, level),
            nameof(SoundbarState.RearLevel)));
    }

    public Task<bool> ToggleMuteAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "mute",
            state => !state.Power
                ? null
                : (state with { Mute = !state.Mute }, (object)!state.Mute),
            nameof(SoundbarState.Mute)));
    }

    public Task<bool> ToggleNightModeAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "sound_setting.night_mode",
            state => !state.Power
                ? null
                : (state with { NightMode = !state.NightMode }, (object)!state.NightMode),
            nameof(SoundbarState.NightMode)));
    }

    public Task<bool> ToggleSoundFieldAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "sound_setting.sound_field",
            state => !state.Power
                ? null
                : (state with { SoundField = !state.SoundField }, (object)!state.SoundField),
            nameof(SoundbarState.SoundField)));
    }

    public Task<bool> ToggleVoiceModeAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "sound_setting.voice_mode",
            state => !state.Power
                ? null
                : (state with { VoiceMode = !state.VoiceMode }, (object)!state.VoiceMode),
            nameof(SoundbarState.VoiceMode)));
    }

    public Task<bool> TogglePowerAsync()
    {
        return Task.FromResult(TryQueueCommand(
            "power",
            state =>
            {
                var target = !state.Power;
                return (state with { Power = target, Mute = target ? false : state.Mute }, (object)target);
            },
            nameof(SoundbarState.Power),
            nameof(SoundbarState.Mute)));
    }

    private bool TryQueueCommand(
        string path,
        Func<SoundbarState, (SoundbarState State, object Value)?> transition,
        params string[] updatedFields)
    {
        SoundbarState? stateToPublish;
        long sequence;

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposeRequested) != 0
                || _activeConnectionGeneration == 0
                || !_currentState.Connected)
                return false;

            var transitionResult = transition(_currentState);
            if (transitionResult == null || transitionResult.Value.State == _currentState)
                return false;

            var command = new QueuedCommand(
                _activeConnectionGeneration,
                path,
                transitionResult.Value.Value);

            if (!_cmdChannel.Writer.TryWrite(command))
                return false;

            _currentState = transitionResult.Value.State;
            MarkFieldsLocked(updatedFields);
            stateToPublish = _currentState;
            sequence = ++_publishSequence;
        }

        PublishState(stateToPublish, sequence);
        return true;
    }

    private void BeginStop()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        _cmdChannel.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (Exception ex)
        {
            Log($"Engine cancellation warning: {DescribeFailure(ex)}", DiagnosticLevel.Info);
        }
    }

    public async Task StopAsync()
    {
        BeginStop();

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            FinalizeResources();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        BeginStop();

        var completion = Completion;
        if (completion.IsCompleted)
        {
            _ = completion.Exception;
            FinalizeResources();
        }
        else
        {
            _ = completion.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((BraviaEngine)state!).FinalizeResources();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        GC.SuppressFinalize(this);
    }

    private void FinalizeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        _cts.Dispose();
    }
}
