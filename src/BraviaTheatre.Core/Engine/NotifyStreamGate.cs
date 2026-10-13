using System;
using System.Threading;
using System.Threading.Tasks;

namespace BraviaTheatre.Core.Engine;

/// <summary>
/// Lets a device-session renewal replace the notification stream bound to the old session,
/// without the end of that stream being taken for a lost connection.
/// </summary>
/// <remarks>
/// A renewal closes the current stream before the handshake starts and holds new streams back
/// until it finishes, so the replacement is always opened against the renewed session.
/// </remarks>
internal sealed class NotifyStreamGate
{
    private readonly object _sync = new();
    private CancellationTokenSource? _stream;
    private TaskCompletionSource? _renewal;

    /// <summary>
    /// Waits out any renewal in progress, then returns the cancellation source for a new stream.
    /// Pass it to <see cref="CloseStream"/> once the stream has ended.
    /// </summary>
    public async Task<CancellationTokenSource> OpenStreamAsync(CancellationToken connectionToken)
    {
        while (true)
        {
            Task renewal;
            lock (_sync)
            {
                if (_renewal == null)
                {
                    _stream = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                    return _stream;
                }

                renewal = _renewal.Task;
            }

            await renewal.WaitAsync(connectionToken).ConfigureAwait(false);
        }
    }

    public void CloseStream(CancellationTokenSource stream)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_stream, stream)) _stream = null;
        }

        stream.Dispose();
    }

    /// <summary>Closes the current stream and holds back new ones until <see cref="EndRenewal"/>.</summary>
    public void BeginRenewal()
    {
        CancellationTokenSource? stream;
        lock (_sync)
        {
            _renewal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stream = _stream;
        }

        try
        {
            stream?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The stream ended on its own while the renewal began.
        }
    }

    public void EndRenewal()
    {
        TaskCompletionSource? renewal;
        lock (_sync)
        {
            renewal = _renewal;
            _renewal = null;
        }

        renewal?.TrySetResult();
    }
}
