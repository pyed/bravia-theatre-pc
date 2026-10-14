using System.Collections.Concurrent;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

/// <summary>
/// The hand-off of engine states to the UI thread, moved out of <c>App</c> so it can be tested
/// without WPF: the queue stands in for the dispatcher.
/// </summary>
public class EngineStateRelayTests
{
    [Fact]
    public void StatesPublishedBeforeTheUiRunsCollapseToTheLatest()
    {
        var ui = new Queue<Action>();
        var applied = new List<int>();
        var relay = new EngineStateRelay(Enqueue(ui), state => applied.Add(state.Volume));
        var generation = relay.StartGeneration();

        for (var volume = 1; volume <= 50; volume++)
            Assert.True(relay.Publish(generation, Volume(volume)));

        Assert.Single(ui);
        RunAll(ui);
        Assert.Equal([50], applied);
    }

    [Fact]
    public void AReplacedEnginesStateIsNeverApplied()
    {
        var ui = new Queue<Action>();
        var applied = new List<int>();
        var relay = new EngineStateRelay(Enqueue(ui), state => applied.Add(state.Volume));
        var old = relay.StartGeneration();

        Assert.True(relay.Publish(old, Volume(1)));
        var current = relay.StartGeneration();
        Assert.False(relay.Publish(old, Volume(2)));
        RunAll(ui);
        Assert.Empty(applied);

        Assert.True(relay.Publish(current, Volume(3)));
        RunAll(ui);
        Assert.Equal([3], applied);
    }

    [Fact]
    public void AStatePublishedWhileOneIsAppliedIsAppliedToo()
    {
        var ui = new Queue<Action>();
        var applied = new List<int>();
        EngineStateRelay? relay = null;
        var generation = 0L;
        relay = new EngineStateRelay(Enqueue(ui), state =>
        {
            applied.Add(state.Volume);
            if (state.Volume == 1) relay!.Publish(generation, Volume(2));
        });
        generation = relay.StartGeneration();

        relay.Publish(generation, Volume(1));
        RunAll(ui);

        Assert.Equal([1, 2], applied);
    }

    [Fact]
    public void ARefusedPostDoesNotBlockLaterStates()
    {
        var accepting = false;
        var ui = new Queue<Action>();
        var applied = new List<int>();
        var relay = new EngineStateRelay(
            action =>
            {
                if (!accepting) return false;
                ui.Enqueue(action);
                return true;
            },
            state => applied.Add(state.Volume));
        var generation = relay.StartGeneration();

        relay.Publish(generation, Volume(1));
        accepting = true;
        relay.Publish(generation, Volume(2));
        RunAll(ui);

        Assert.Equal([2], applied);
    }

    [Fact]
    public async Task ConcurrentPublishersQueueOneDispatchAtATimeAndEndOnTheLastState()
    {
        using var ui = new BlockingCollection<Action>();
        var queued = 0;
        var maxQueued = 0;
        var lastApplied = 0;
        var relay = new EngineStateRelay(
            action =>
            {
                var now = Interlocked.Increment(ref queued);
                InterlockedMax(ref maxQueued, now);
                ui.Add(action);
                return true;
            },
            state => Volatile.Write(ref lastApplied, state.Volume));
        var generation = relay.StartGeneration();
        var uiThread = Task.Run(() =>
        {
            foreach (var action in ui.GetConsumingEnumerable())
            {
                Interlocked.Decrement(ref queued);
                action();
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(publisher => Task.Run(() =>
        {
            for (var i = 1; i <= 2000; i++)
                relay.Publish(generation, Volume(i));
        }, TestContext.Current.CancellationToken)));
        relay.Publish(generation, Volume(-1));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref lastApplied) != -1)
            await Task.Delay(5, timeout.Token);

        ui.CompleteAdding();
        await uiThread;
        Assert.Equal(1, Volatile.Read(ref maxQueued));
    }

    private static Func<Action, bool> Enqueue(Queue<Action> ui) => action =>
    {
        ui.Enqueue(action);
        return true;
    };

    private static void RunAll(Queue<Action> ui)
    {
        while (ui.TryDequeue(out var action))
            action();
    }

    private static SoundbarState Volume(int volume) => SoundbarState.Disconnected with { Connected = true, Volume = volume };

    private static void InterlockedMax(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
