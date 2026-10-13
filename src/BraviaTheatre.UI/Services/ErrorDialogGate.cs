using System;
using System.Collections.Generic;

namespace BraviaTheatre.UI.Services;

/// <summary>
/// Decides which unhandled UI errors get a dialog. The log still records every occurrence.
/// </summary>
/// <remarks>
/// A failure in a window message handler repeats for every window and message that reaches it,
/// and a modal dialog keeps pumping messages while it waits. Showing a dialog per exception
/// therefore stacked a dozen identical dialogs on screen, each nested inside the last.
/// </remarks>
internal sealed class ErrorDialogGate
{
    internal static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private bool _isOpen;

    public ErrorDialogGate(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns true when a dialog should be shown for <paramref name="exception"/>; the caller
    /// must then call <see cref="Close"/> once the dialog is dismissed.
    /// </summary>
    public bool TryOpen(Exception exception)
    {
        if (_isOpen) return false;

        var signature = GetSignature(exception);
        var now = _timeProvider.GetUtcNow();
        if (_lastShown.TryGetValue(signature, out var shownAt) && now - shownAt < RepeatInterval)
            return false;

        _lastShown[signature] = now;
        _isOpen = true;
        return true;
    }

    public void Close() => _isOpen = false;

    internal static string GetSignature(Exception exception)
    {
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var site = exception.TargetSite;
        return site == null ? type : $"{type}@{site.DeclaringType?.FullName}.{site.Name}";
    }
}
