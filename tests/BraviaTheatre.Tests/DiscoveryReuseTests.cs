using System.Collections.Concurrent;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Discovery;
using BraviaTheatre.Core.Engine;
using Grpc.Core;
using FakeBraviaClient = BraviaTheatre.Tests.EngineRegressionTests.FakeBraviaClient;

namespace BraviaTheatre.Tests;

/// <summary>
/// Without a static host, every reconnect used to multicast and probe the subnet again, although
/// the soundbar was almost always still where it had just been.
/// </summary>
public class DiscoveryReuseTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepaliveDelay = TimeSpan.FromSeconds(25);
    private static readonly DiscoveredDevice Soundbar = new("192.0.2.10", 55051, "Test Bar");
    private static readonly DiscoveredDevice MovedSoundbar = new("192.0.2.20", 55051, "Test Bar");

    [Fact]
    public async Task ReconnectTriesTheLastDiscoveredSoundbarFirst()
    {
        var first = DropsAfterFirstKeepalive();
        var second = new FakeBraviaClient();
        var hosts = new ConcurrentQueue<string>();
        var discoveries = 0;
        var clients = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            null,
            55051,
            (host, _, _) =>
            {
                hosts.Enqueue(host);
                return Interlocked.Increment(ref clients) switch
                {
                    1 => first,
                    2 => second,
                    _ => throw new InvalidOperationException("Unexpected extra reconnect.")
                };
            },
            OneKeepaliveThenWait(),
            discoverAsync: (_, _) =>
            {
                Interlocked.Increment(ref discoveries);
                return Task.FromResult<DiscoveredDevice?>(Soundbar);
            });

        try
        {
            engine.Start();
            await second.Initialized.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref discoveries));
            Assert.Equal(["192.0.2.10", "192.0.2.10"], hosts.ToArray());
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SoundbarThatNoLongerAnswersThereIsDiscoveredAgain()
    {
        var first = DropsAfterFirstKeepalive();
        var unreachable = new FakeBraviaClient
        {
            InitFailure = new RpcException(new Status(StatusCode.Unavailable, "No route to host."))
        };
        var third = new FakeBraviaClient();
        var hosts = new ConcurrentQueue<string>();
        var discoveries = 0;
        var clients = 0;
        var engine = new BraviaEngine(
            new SonyCredentials(),
            null,
            55051,
            (host, _, _) =>
            {
                hosts.Enqueue(host);
                return Interlocked.Increment(ref clients) switch
                {
                    1 => first,
                    2 => unreachable,
                    3 => third,
                    _ => throw new InvalidOperationException("Unexpected extra reconnect.")
                };
            },
            OneKeepaliveThenWait(),
            discoverAsync: (_, _) => Task.FromResult<DiscoveredDevice?>(
                Interlocked.Increment(ref discoveries) == 1 ? Soundbar : MovedSoundbar));

        try
        {
            engine.Start();
            await third.Initialized.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(2, Volatile.Read(ref discoveries));
            Assert.Equal(["192.0.2.10", "192.0.2.10", "192.0.2.20"], hosts.ToArray());
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    // The first keepalive comes back empty even after a session renewal, which ends the
    // connection and makes the engine reconnect.
    private static FakeBraviaClient DropsAfterFirstKeepalive()
    {
        var client = new FakeBraviaClient();
        var calls = 0;
        client.StateHandler = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new Dictionary<string, object?> { ["power"] = true }
                : new Dictionary<string, object?>());
        };
        return client;
    }

    private static Func<TimeSpan, CancellationToken, Task> OneKeepaliveThenWait()
    {
        var keepalives = 0;
        return (delay, ct) => delay != KeepaliveDelay
            ? Task.CompletedTask
            : Interlocked.Increment(ref keepalives) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
