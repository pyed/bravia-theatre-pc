using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using Grpc.Core;
using FakeBraviaClient = BraviaTheatre.Tests.EngineRegressionTests.FakeBraviaClient;

namespace BraviaTheatre.Tests;

public class AuthRequiredRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static SonyCredentials ValidCredentials =>
        new() { SessionId = "session-1", HmacKey = "hmac-1" };

    private static SonyCredentials MissingCredentials => new();

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    [Fact]
    public async Task HandshakeInvalidArgument_WithValidCredentials_DisconnectsAuthRequired()
    {
        var client = new FakeBraviaClient { InitFailure = InvalidArgument("sign-in required") };
        var engine = CreateEngine(ValidCredentials, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var observed = engine.CurrentState;
            Assert.False(observed.Connected);
            Assert.True(observed.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task HandshakeInvalidArgument_WithoutCredentials_DisconnectsNotAuthRequired()
    {
        var client = new FakeBraviaClient { InitFailure = InvalidArgument("sign-in required") };
        var engine = CreateEngine(MissingCredentials, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var observed = engine.CurrentState;
            Assert.False(observed.Connected);
            Assert.False(observed.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task AuthFailureThenNetworkFailure_KeepsAuthRequiredSticky()
    {
        var authClient = new FakeBraviaClient { InitFailure = InvalidArgument("sign-in required") };
        var networkClient = new FakeBraviaClient
        {
            InitFailure = new OperationCanceledException("network down")
        };
        var clients = new List<IBraviaClient> { authClient, networkClient };
        var index = -1;

        var engine = CreateEngine(ValidCredentials, () =>
        {
            var i = Math.Max(Interlocked.Increment(ref index), 0);
            return clients[Math.Min(i, clients.Count - 1)];
        });

        try
        {
            engine.Start();
            await authClient.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await networkClient.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var observed = engine.CurrentState;
            Assert.False(observed.Connected);
            Assert.True(observed.AuthRequired, "AuthRequired must stay sticky across a later network failure");
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task AuthFailureThenSuccessfulHandshake_ClearsAuthRequiredAndConnects()
    {
        var authClient = new FakeBraviaClient { InitFailure = InvalidArgument("sign-in required") };
        var okClient = new FakeBraviaClient();
        var clients = new List<IBraviaClient> { authClient, okClient };
        var index = -1;

        var engine = CreateEngine(ValidCredentials, () =>
        {
            var i = Math.Max(Interlocked.Increment(ref index), 0);
            return clients[Math.Min(i, clients.Count - 1)];
        });

        try
        {
            engine.Start();
            await UntilAsync(() => engine.CurrentState.Connected);

            var observed = engine.CurrentState;
            Assert.True(observed.Connected);
            Assert.False(observed.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task HandshakeUnavailable_WithValidCredentials_DoesNotRequireAuth()
    {
        var client = new FakeBraviaClient
        {
            InitFailure = new RpcException(new Status(StatusCode.Unavailable, "network failure"))
        };
        var engine = CreateEngine(ValidCredentials, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var observed = engine.CurrentState;
            Assert.False(observed.Connected);
            Assert.False(observed.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task HandshakeNotFound_WithValidCredentials_DoesNotRequireAuth()
    {
        var client = new FakeBraviaClient
        {
            InitFailure = new RpcException(new Status(StatusCode.NotFound, "path not found"))
        };
        var engine = CreateEngine(ValidCredentials, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var observed = engine.CurrentState;
            Assert.False(observed.Connected);
            Assert.False(observed.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task HandshakeOutOfRange_WithValidCredentials_DoesNotRequireAuth()
    {
        var client = new FakeBraviaClient
        {
            InitFailure = new RpcException(new Status(StatusCode.OutOfRange, "value out of range"))
        };
        var engine = CreateEngine(ValidCredentials, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var observed = engine.CurrentState;
            Assert.False(observed.Connected);
            Assert.False(observed.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task NonHandshakeInvalidArgument_DisconnectsNotAuthRequired()
    {
        var client = new FakeBraviaClient
        {
            StateHandler = (_, _) => throw InvalidArgument("unsupported path")
        };
        var engine = CreateEngine(ValidCredentials, () => client);
        var disconnectedAuthRequired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += state =>
        {
            if (!state.Connected && client.Initialized.Task.IsCompleted)
                disconnectedAuthRequired.TrySetResult(state.AuthRequired);
        };

        try
        {
            engine.Start();
            var authRequired = await disconnectedAuthRequired.Task.WaitAsync(
                TestTimeout,
                TestContext.Current.CancellationToken);

            Assert.False(authRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    private static BraviaEngine CreateEngine(SonyCredentials credentials, Func<IBraviaClient> nextClient) =>
        new(credentials, "192.168.1.50", 4000,
            (_, _, _) => nextClient(),
            (_, _) => Task.CompletedTask);

    private static async Task StopEngineAsync(BraviaEngine engine)
    {
        try
        {
            await engine.StopAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
