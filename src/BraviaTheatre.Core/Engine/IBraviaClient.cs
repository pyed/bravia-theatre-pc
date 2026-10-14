using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.Core.Engine;

internal interface IBraviaClient : IDisposable
{
    Action<string, DiagnosticLevel>? LogAction { get; set; }

    Task InitializeSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Repeats the handshake on the open connection with renewed credentials, which every later
    /// call then signs with. If the handshake fails, the client keeps its previous credentials.
    /// </summary>
    Task ReauthenticateAsync(SonyCredentials credentials, CancellationToken ct = default);

    Task<Dictionary<string, object?>> GetInitialStatesAsync(
        IEnumerable<string> paths,
        CancellationToken ct = default);

    Task<bool> ExecCommandAsync(
        string path,
        int? intValue = null,
        bool? boolValue = null,
        string? stringValue = null,
        CancellationToken ct = default);

    IAsyncEnumerable<byte[]> ReadNotificationsAsync(CancellationToken ct = default);
}
