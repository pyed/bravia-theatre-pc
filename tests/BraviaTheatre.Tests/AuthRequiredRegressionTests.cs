using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using Grpc.Core;
using FakeBraviaClient = BraviaTheatre.Tests.EngineRegressionTests.FakeBraviaClient;

namespace BraviaTheatre.Tests;

public class AuthRequiredRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ValidCredentials_ConnectWithoutRefresh()
    {
        var credentials = Credentials("valid");
        var refreshCount = 0;
        var client = new FakeBraviaClient();
        var lifecycle = Lifecycle(credentials, (current, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(current);
        });
        var engine = CreateEngine(lifecycle, () => client);

        try
        {
            engine.Start();
            await client.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
            Assert.Equal(0, Volatile.Read(ref refreshCount));
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task NearExpiryCredentials_RefreshBeforeAuthenticatedConnect()
    {
        var stale = Credentials("stale", DateTimeOffset.UtcNow.AddMinutes(1));
        var renewed = Credentials("renewed", DateTimeOffset.UtcNow.AddHours(12));
        var refreshCount = 0;
        SonyCredentials? usedByClient = null;
        var client = new FakeBraviaClient();
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(renewed);
        });
        var engine = CreateEngine(lifecycle, credentials =>
        {
            usedByClient = credentials;
            return client;
        });

        try
        {
            engine.Start();
            await client.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Same(renewed, lifecycle.CurrentCredentials);
            Assert.Same(renewed, usedByClient);
            Assert.True(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task RenewedSnapshotStillInsideWindow_DoesNotProactivelyRefreshLoop()
    {
        var stale = Credentials("stale", DateTimeOffset.UtcNow.AddMinutes(1));
        var renewed = Credentials("short", DateTimeOffset.UtcNow);
        var keepalivePolled = NewSignal();
        var refreshCount = 0;
        var clientCount = 0;
        var stateCalls = 0;
        var keepaliveDelays = 0;
        var client = new FakeBraviaClient
        {
            StateHandler = (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref stateCalls) == 2)
                    keepalivePolled.TrySetResult(true);
                return Task.FromResult(new Dictionary<string, object?> { ["power"] = true });
            }
        };
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(renewed);
        });
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, _) =>
            {
                Interlocked.Increment(ref clientCount);
                return client;
            },
            (delay, ct) => delay == TimeSpan.FromSeconds(25)
                && Interlocked.Increment(ref keepaliveDelays) == 1
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.InfiniteTimeSpan, ct));

        try
        {
            engine.Start();
            await keepalivePolled.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Equal(1, Volatile.Read(ref clientCount));
            Assert.True(engine.CurrentState.Connected);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task HandshakeInvalidArgument_RefreshesAndRetriesOnce()
    {
        var stale = Credentials("stale");
        var renewed = Credentials("renewed", DateTimeOffset.UtcNow.AddHours(12));
        var rejected = new FakeBraviaClient { InitFailure = InvalidArgument("stale local credentials") };
        var connected = new FakeBraviaClient();
        var clients = new Queue<IBraviaClient>([rejected, connected]);
        var clientCredentials = new List<SonyCredentials>();
        var refreshCount = 0;
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(renewed);
        });
        var engine = CreateEngine(lifecycle, credentials =>
        {
            clientCredentials.Add(credentials);
            return clients.Dequeue();
        });

        try
        {
            engine.Start();
            await connected.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(rejected.Disposed.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Collection(
                clientCredentials,
                credentials => Assert.Same(stale, credentials),
                credentials => Assert.Same(renewed, credentials));
            Assert.True(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task RenewedHandshakeRejected_RetriesWithoutRefreshOrAuthenticationPrompt()
    {
        var stale = Credentials("stale");
        var renewed = Credentials("renewed", DateTimeOffset.UtcNow.AddHours(12));
        var first = new FakeBraviaClient { InitFailure = InvalidArgument("stale") };
        var retry = new FakeBraviaClient { InitFailure = InvalidArgument("still rejected") };
        var laterRetry = new FakeBraviaClient { InitFailure = InvalidArgument("still rejected later") };
        var clients = new Queue<IBraviaClient>([first, retry, laterRetry]);
        var refreshCount = 0;
        var clientCount = 0;
        var boundedRetryReached = NewSignal();
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(renewed);
        });
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, _) =>
            {
                Interlocked.Increment(ref clientCount);
                return clients.Dequeue();
            },
            (delay, ct) =>
            {
                if (delay == TimeSpan.FromSeconds(5))
                    return Task.CompletedTask;

                Assert.Equal(TimeSpan.FromSeconds(10), delay);
                boundedRetryReached.TrySetResult(true);
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        try
        {
            engine.Start();
            await retry.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await laterRetry.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await boundedRetryReached.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Equal(3, Volatile.Read(ref clientCount));
            Assert.False(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task RefreshTokenRejected_RequiresInteractiveAuthentication()
    {
        var stale = Credentials("revoked", DateTimeOffset.UtcNow.AddMinutes(1));
        var refreshCount = 0;
        var clientCount = 0;
        var authRequired = NewSignal();
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            throw new SonyOAuthException(SonyOAuthFailureKind.ReauthenticationRequired, "authorization revoked");
        });
        var engine = CreateEngine(lifecycle, () =>
        {
            Interlocked.Increment(ref clientCount);
            return new FakeBraviaClient();
        });
        engine.StateChanged += state =>
        {
            if (state.AuthRequired) authRequired.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await authRequired.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Equal(0, Volatile.Read(ref clientCount));
            Assert.True(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task InstalledReplacementClearsStickyAuthRequiredBeforeNetworkFailure()
    {
        var revoked = Credentials("revoked", DateTimeOffset.UtcNow.AddMinutes(1));
        var replacement = Credentials("replacement", DateTimeOffset.UtcNow.AddHours(12));
        var authRequired = NewSignal();
        var backoffStarted = NewSignal();
        var releaseBackoff = NewSignal();
        var client = new FakeBraviaClient
        {
            InitFailure = new RpcException(new Status(StatusCode.Unavailable, "synthetic network failure"))
        };
        var lifecycle = Lifecycle(revoked, (_, _) =>
            throw new SonyOAuthException(SonyOAuthFailureKind.ReauthenticationRequired, "authorization revoked"));
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, _) => client,
            (delay, ct) =>
            {
                if (delay == TimeSpan.FromSeconds(5))
                {
                    backoffStarted.TrySetResult(true);
                    return releaseBackoff.Task.WaitAsync(ct);
                }

                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        engine.StateChanged += state =>
        {
            if (state.AuthRequired) authRequired.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await authRequired.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await backoffStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var installed = await lifecycle.InstallAsync(replacement, TestContext.Current.CancellationToken);
            Assert.Equal(CredentialRenewalStatus.Succeeded, installed.Status);
            releaseBackoff.TrySetResult(true);
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            releaseBackoff.TrySetResult(true);
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task NearExpiryLegacyCredentials_KeepWorkingWithoutCloudCall()
    {
        var stale = Credentials("legacy", DateTimeOffset.UtcNow.AddMinutes(1)) with { RefreshToken = null };
        var refreshCount = 0;
        var client = new FakeBraviaClient();
        var lifecycle = Lifecycle(stale, (current, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(current);
        });
        var engine = CreateEngine(lifecycle, () => client);

        try
        {
            engine.Start();
            await client.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref refreshCount));
            Assert.True(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task RejectedLegacyCredentials_RequireInteractiveAuthenticationWithoutCloudCall()
    {
        var stale = Credentials("legacy") with { RefreshToken = null };
        var refreshCount = 0;
        var client = new FakeBraviaClient { InitFailure = InvalidArgument("expired") };
        var lifecycle = Lifecycle(stale, (current, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(current);
        });
        var engine = CreateEngine(lifecycle, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref refreshCount));
            Assert.False(engine.CurrentState.Connected);
            Assert.True(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task PreflightCloudFailure_UsesStillValidLocalCredentialsWithoutChurn()
    {
        var stale = Credentials("transient-preflight", DateTimeOffset.UtcNow.AddMinutes(1));
        var keepalivePolled = NewSignal();
        var refreshCount = 0;
        var clientCount = 0;
        var stateCalls = 0;
        var keepaliveDelays = 0;
        var client = new FakeBraviaClient
        {
            StateHandler = (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref stateCalls) == 2)
                    keepalivePolled.TrySetResult(true);
                return Task.FromResult(new Dictionary<string, object?> { ["power"] = true });
            }
        };
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            throw new SonyOAuthException(SonyOAuthFailureKind.Transient, "cloud unavailable");
        });
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, _) =>
            {
                Interlocked.Increment(ref clientCount);
                return client;
            },
            (delay, ct) => delay == TimeSpan.FromSeconds(25)
                && Interlocked.Increment(ref keepaliveDelays) == 1
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.InfiniteTimeSpan, ct));

        try
        {
            engine.Start();
            await keepalivePolled.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Equal(1, Volatile.Read(ref clientCount));
            Assert.True(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task FailedPreflightAndImmediateConnectionFailureUseGrowingBackoff()
    {
        var stale = Credentials("backoff", DateTimeOffset.UtcNow.AddMinutes(1));
        var thirdBackoff = NewSignal();
        var reconnectDelays = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        var refreshCount = 0;
        var clientCount = 0;
        var lifecycle = Lifecycle(stale, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            throw new SonyOAuthException(SonyOAuthFailureKind.Transient, "cloud unavailable");
        });
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, _) =>
            {
                Interlocked.Increment(ref clientCount);
                return new FakeBraviaClient
                {
                    StateHandler = (_, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return Task.FromResult(new Dictionary<string, object?>());
                    }
                };
            },
            (delay, ct) =>
            {
                if (delay == TimeSpan.FromSeconds(25))
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);

                reconnectDelays.Enqueue(delay);
                if (reconnectDelays.Count < 3)
                    return Task.CompletedTask;

                thirdBackoff.TrySetResult(true);
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        try
        {
            engine.Start();
            await thirdBackoff.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(
                [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)],
                reconnectDelays.ToArray());
            Assert.Equal(3, Volatile.Read(ref refreshCount));
            Assert.Equal(3, Volatile.Read(ref clientCount));
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task RotatedTokenCheckpoint_RecoversAfterTransientCloudFailureWithoutAuthenticationPrompt()
    {
        var credentials = Credentials("transient");
        var renewed = Credentials("renewed", DateTimeOffset.UtcNow.AddHours(12)) with
        {
            RefreshToken = "rotated-refresh"
        };
        var first = new FakeBraviaClient { InitFailure = InvalidArgument("stale") };
        var second = new FakeBraviaClient { InitFailure = InvalidArgument("still stale") };
        var connected = new FakeBraviaClient();
        var clients = new Queue<IBraviaClient>([first, second, connected]);
        var clientCredentials = new List<SonyCredentials>();
        var refreshCount = 0;
        var lifecycle = new SonyCredentialLifecycle(
            credentials,
            async (current, checkpointRotatedRefreshTokenAsync, _) =>
            {
                if (Interlocked.Increment(ref refreshCount) == 1)
                {
                    Assert.Same(credentials, current);
                    await checkpointRotatedRefreshTokenAsync("rotated-refresh");
                    throw new SonyOAuthException(SonyOAuthFailureKind.Transient, "cloud unavailable");
                }

                Assert.Equal("rotated-refresh", current.RefreshToken);
                return renewed;
            },
            static (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, current) =>
            {
                clientCredentials.Add(current);
                return clients.Dequeue();
            },
            (delay, ct) => delay == TimeSpan.FromSeconds(5)
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, ct));

        try
        {
            engine.Start();
            await connected.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(2, Volatile.Read(ref refreshCount));
            Assert.Same(renewed, lifecycle.CurrentCredentials);
            Assert.Collection(
                clientCredentials,
                current => Assert.Same(credentials, current),
                current =>
                {
                    Assert.Equal(credentials.KeyId, current.KeyId);
                    Assert.Equal("rotated-refresh", current.RefreshToken);
                },
                current => Assert.Same(renewed, current));
            Assert.True(engine.CurrentState.Connected);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolOrPersistenceRefreshFailure_DoesNotRequireAuthentication(bool persistenceFailure)
    {
        var stale = Credentials("failure");
        var renewed = Credentials("replacement", DateTimeOffset.UtcNow.AddHours(12));
        var client = new FakeBraviaClient { InitFailure = InvalidArgument("stale") };
        var lifecycle = new SonyCredentialLifecycle(
            stale,
            (_, _, _) => persistenceFailure
                ? Task.FromResult(renewed)
                : throw new SonyOAuthException(SonyOAuthFailureKind.Protocol, "invalid response"),
            (credentials, _) => persistenceFailure && ReferenceEquals(credentials, renewed)
                ? throw new IOException("synthetic persistence failure")
                : Task.CompletedTask);
        var engine = CreateEngine(lifecycle, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Same(stale, lifecycle.CurrentCredentials);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task NonHandshakeInvalidArgument_DoesNotRefresh()
    {
        var refreshCount = 0;
        var client = new FakeBraviaClient
        {
            StateHandler = (_, _) => throw InvalidArgument("unsupported path")
        };
        var lifecycle = Lifecycle(Credentials("request"), (current, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(current);
        });
        var engine = CreateEngine(lifecycle, () => client);

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref refreshCount));
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task HandshakeInvalidArgumentWithoutValidCredentials_DoesNotRequireAuthentication()
    {
        var client = new FakeBraviaClient { InitFailure = InvalidArgument("malformed authentication input") };
        var logs = new List<string>();
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "192.168.1.50",
            4000,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        engine.LogAction = logs.Add;

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(engine.CurrentState.AuthRequired);
            Assert.Contains(logs, log => log.Contains("Classification=request_failure", StringComparison.Ordinal));
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Theory]
    [InlineData(StatusCode.NotFound, "request_failure")]
    [InlineData(StatusCode.OutOfRange, "request_failure")]
    [InlineData(StatusCode.Unavailable, "network_failure")]
    public async Task OtherHandshakeStatus_DoesNotRefreshOrRequireAuthentication(
        StatusCode statusCode,
        string expectedClassification)
    {
        var refreshCount = 0;
        var logs = new List<string>();
        var client = new FakeBraviaClient
        {
            InitFailure = new RpcException(new Status(statusCode, "synthetic-session-secret"))
        };
        var lifecycle = Lifecycle(Credentials(statusCode.ToString()), (current, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(current);
        });
        var engine = CreateEngine(lifecycle, () => client);
        engine.LogAction = logs.Add;

        try
        {
            engine.Start();
            await client.Disposed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref refreshCount));
            Assert.False(engine.CurrentState.AuthRequired);
            Assert.Contains(logs, log => log.Contains($"Classification={expectedClassification}", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, log => log.Contains("synthetic-session-secret", StringComparison.Ordinal));
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task KeepaliveBoundary_ReconnectsAndRenewsAuthoritativeSnapshot()
    {
        var original = Credentials("original", DateTimeOffset.UtcNow.AddHours(1));
        var nearExpiry = Credentials("near", DateTimeOffset.UtcNow.AddMinutes(1));
        var renewed = Credentials("renewed", DateTimeOffset.UtcNow.AddHours(12));
        var first = new FakeBraviaClient();
        var second = new FakeBraviaClient();
        var keepaliveDelayStarted = NewSignal();
        var releaseKeepalive = NewSignal();
        var keepaliveCount = 0;
        var reconnectCount = 0;
        var clientCount = 0;
        var refreshCount = 0;
        SonyCredentials? secondClientCredentials = null;
        var lifecycle = Lifecycle(original, (_, _) =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.FromResult(renewed);
        });
        var engine = new BraviaEngine(
            lifecycle,
            "192.168.1.50",
            4000,
            (_, _, credentials) =>
            {
                if (Interlocked.Increment(ref clientCount) == 1) return first;
                secondClientCredentials = credentials;
                return second;
            },
            (delay, ct) =>
            {
                if (delay == TimeSpan.FromSeconds(25)
                    && Interlocked.Increment(ref keepaliveCount) == 1)
                {
                    keepaliveDelayStarted.TrySetResult(true);
                    return releaseKeepalive.Task.WaitAsync(ct);
                }

                if (delay == TimeSpan.FromSeconds(5)
                    && Interlocked.Increment(ref reconnectCount) == 1)
                    return Task.CompletedTask;

                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        try
        {
            engine.Start();
            await keepaliveDelayStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            var staleGeneration = engine.ActiveConnectionGeneration;
            var install = await lifecycle.InstallAsync(nearExpiry, TestContext.Current.CancellationToken);
            Assert.Equal(CredentialRenewalStatus.Succeeded, install.Status);

            releaseKeepalive.TrySetResult(true);
            await second.NotificationsStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await UntilAsync(() => engine.CurrentState.Volume == 10);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
            Assert.Same(renewed, secondClientCredentials);
            Assert.NotEqual(staleGeneration, engine.ActiveConnectionGeneration);
            engine.ApplyDeltaForConnection(staleGeneration, "volume", 99);
            Assert.Equal(10, engine.CurrentState.Volume);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            releaseKeepalive.TrySetResult(true);
            await StopEngineAsync(engine);
        }
    }

    [Fact]
    public async Task CancellationDuringRefresh_StopsWithoutCreatingClientOrReplacingCredentials()
    {
        var stale = Credentials("cancel", DateTimeOffset.UtcNow.AddMinutes(1));
        var refreshStarted = NewSignal();
        var refreshCanceled = NewSignal();
        var clientCount = 0;
        var lifecycle = Lifecycle(stale, async (_, ct) =>
        {
            refreshStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable");
            }
            finally
            {
                if (ct.IsCancellationRequested) refreshCanceled.TrySetResult(true);
            }
        });
        var engine = CreateEngine(lifecycle, () =>
        {
            Interlocked.Increment(ref clientCount);
            return new FakeBraviaClient();
        });

        try
        {
            engine.Start();
            await refreshStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await engine.StopAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await refreshCanceled.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref clientCount));
            Assert.Same(stale, lifecycle.CurrentCredentials);
            Assert.False(engine.CurrentState.AuthRequired);
        }
        finally
        {
            await StopEngineAsync(engine);
        }
    }

    private static SonyCredentials Credentials(string suffix, DateTimeOffset? expiresAt = null) => new()
    {
        KeyId = $"key-{suffix}",
        HmacKey = $"hmac-{suffix}",
        SessionKey = $"session-{suffix}",
        DeviceId = $"device-{suffix}",
        RefreshToken = $"refresh-{suffix}",
        SessionKeysExpiresAtUtc = expiresAt
    };

    private static SonyCredentialLifecycle Lifecycle(
        SonyCredentials credentials,
        Func<SonyCredentials, CancellationToken, Task<SonyCredentials>> renew) =>
        new(credentials, (current, _, ct) => renew(current, ct), static (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static BraviaEngine CreateEngine(
        SonyCredentialLifecycle lifecycle,
        Func<IBraviaClient> nextClient) =>
        CreateEngine(lifecycle, _ => nextClient());

    private static BraviaEngine CreateEngine(
        SonyCredentialLifecycle lifecycle,
        Func<SonyCredentials, IBraviaClient> nextClient) =>
        new(lifecycle, "192.168.1.50", 4000,
            (_, _, credentials) => nextClient(credentials),
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

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
}
