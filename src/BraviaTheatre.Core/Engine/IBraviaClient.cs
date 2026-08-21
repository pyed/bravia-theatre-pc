using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BraviaTheatre.Core.Engine;

internal interface IBraviaClient : IDisposable
{
    Action<string>? LogAction { get; set; }

    Task InitializeSessionAsync(CancellationToken ct = default);

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
