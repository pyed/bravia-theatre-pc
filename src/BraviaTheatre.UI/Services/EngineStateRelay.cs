using System;
using System.Threading;
using BraviaTheatre.Core.Models;

namespace BraviaTheatre.UI.Services;

/// <summary>
/// Carries engine states from the engine's threads to the UI thread.
/// </summary>
/// <remarks>
/// The engine can publish faster than the UI applies, so only the latest state waits, and at most
/// one dispatch is queued at a time. Replacing the engine starts a new generation: a state from an
/// older one is dropped, even if it was already waiting.
/// </remarks>
internal sealed class EngineStateRelay
{
    private readonly object _lock = new();
    private readonly Func<Action, bool> _post;
    private readonly Action<SoundbarState> _apply;
    private long _generation;
    private (long Generation, SoundbarState State)? _pending;
    private int _dispatchQueued;

    /// <param name="post">Queues work on the UI thread; false if that is no longer possible.</param>
    /// <param name="apply">Applies a state on the UI thread.</param>
    public EngineStateRelay(Func<Action, bool> post, Action<SoundbarState> apply)
    {
        _post = post;
        _apply = apply;
    }

    public long Generation => Volatile.Read(ref _generation);

    /// <summary>Starts a new generation and drops any waiting state.</summary>
    public long StartGeneration()
    {
        lock (_lock)
        {
            _pending = null;
            return Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>Called on any thread; returns false if the state belongs to an old generation.</summary>
    public bool Publish(long generation, SoundbarState state)
    {
        lock (_lock)
        {
            if (generation != Generation) return false;
            _pending = (generation, state);
        }

        ScheduleDispatch();
        return true;
    }

    private void ScheduleDispatch()
    {
        if (Interlocked.Exchange(ref _dispatchQueued, 1) != 0) return;

        bool posted;
        try
        {
            posted = _post(Dispatch);
        }
        catch
        {
            posted = false;
        }

        if (!posted) Interlocked.Exchange(ref _dispatchQueued, 0);
    }

    private void Dispatch()
    {
        (long Generation, SoundbarState State)? latest;
        lock (_lock)
        {
            latest = _pending;
            _pending = null;
        }

        Interlocked.Exchange(ref _dispatchQueued, 0);
        if (latest is { } pending && pending.Generation == Generation)
            _apply(pending.State);

        // A state published while this one was being applied needs its own dispatch.
        bool more;
        lock (_lock) more = _pending != null;
        if (more) ScheduleDispatch();
    }
}
