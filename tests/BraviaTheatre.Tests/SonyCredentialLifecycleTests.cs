using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.Tests;

public sealed class SonyCredentialLifecycleTests
{
    [Fact]
    public async Task InstallAsync_PersistsBeforePublishing()
    {
        var initial = Credentials("initial");
        var replacement = Credentials("replacement");
        var persistEntered = Signal();
        var allowPersist = Signal();
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Renewal must not run."),
            async (credentials, cancellationToken) =>
            {
                Assert.Same(replacement, credentials);
                persistEntered.TrySetResult(true);
                await allowPersist.Task.WaitAsync(cancellationToken);
            });

        var installing = lifecycle.InstallAsync(replacement, TestContext.Current.CancellationToken);
        await persistEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(initial, lifecycle.CurrentCredentials);
        allowPersist.TrySetResult(true);
        var result = await installing;

        Assert.Equal(CredentialRenewalStatus.Succeeded, result.Status);
        Assert.Same(replacement, result.Credentials);
        Assert.Same(replacement, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCallersExecuteOneRenewalAndPersistence()
    {
        var initial = Credentials("initial");
        var replacement = Credentials("replacement");
        var renewalEntered = Signal();
        var allowRenewal = Signal();
        var renewalCalls = 0;
        var persistenceCalls = 0;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            async (expected, _, cancellationToken) =>
            {
                Assert.Same(initial, expected);
                Interlocked.Increment(ref renewalCalls);
                renewalEntered.TrySetResult(true);
                await allowRenewal.Task.WaitAsync(cancellationToken);
                return replacement;
            },
            (credentials, _) =>
            {
                Assert.Same(replacement, credentials);
                Interlocked.Increment(ref persistenceCalls);
                return Task.CompletedTask;
            });

        var first = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        await renewalEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        allowRenewal.TrySetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
        {
            Assert.Equal(CredentialRenewalStatus.Succeeded, result.Status);
            Assert.Same(replacement, result.Credentials);
        });
        Assert.Equal(1, renewalCalls);
        Assert.Equal(1, persistenceCalls);
        Assert.Same(replacement, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentFailureIsSharedButLaterAttemptCanRetry()
    {
        var initial = Credentials("initial");
        var renewalEntered = Signal();
        var allowRenewal = Signal();
        var renewalCalls = 0;
        var persistenceCalls = 0;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            async (_, checkpointRotatedRefreshTokenAsync, cancellationToken) =>
            {
                Interlocked.Increment(ref renewalCalls);
                await checkpointRotatedRefreshTokenAsync("rotated-refresh");
                renewalEntered.TrySetResult(true);
                await allowRenewal.Task.WaitAsync(cancellationToken);
                throw new SonyOAuthException(SonyOAuthFailureKind.Transient, "synthetic transient failure");
            },
            (_, _) =>
            {
                Interlocked.Increment(ref persistenceCalls);
                return Task.CompletedTask;
            });

        var first = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        await renewalEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        allowRenewal.TrySetResult(true);
        var concurrent = await Task.WhenAll(first, second);

        Assert.All(concurrent, result => Assert.Equal(CredentialRenewalStatus.TransientFailure, result.Status));
        Assert.Equal(1, renewalCalls);
        Assert.Equal(1, persistenceCalls);
        Assert.True(lifecycle.IsLocalKeyRefreshPending(lifecycle.CurrentCredentials!));

        var later = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.TransientFailure, later.Status);
        Assert.Equal(2, renewalCalls);
        Assert.Equal(1, persistenceCalls);
    }

    [Fact]
    public async Task RefreshAsync_ExplicitAuthorizationRejectionIsLatchedUntilInstall()
    {
        var initial = Credentials("initial");
        var installed = Credentials("installed");
        var renewalCalls = 0;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) =>
            {
                Interlocked.Increment(ref renewalCalls);
                throw new SonyOAuthException(SonyOAuthFailureKind.ReauthenticationRequired, "synthetic rejection");
            },
            (_, _) => Task.CompletedTask);

        var first = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        var second = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.AuthenticationRequired, first.Status);
        Assert.Equal(CredentialRenewalStatus.AuthenticationRequired, second.Status);
        Assert.Equal(1, renewalCalls);

        var install = await lifecycle.InstallAsync(installed, TestContext.Current.CancellationToken);
        var afterInstall = await lifecycle.RefreshAsync(installed, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Succeeded, install.Status);
        Assert.Equal(CredentialRenewalStatus.AuthenticationRequired, afterInstall.Status);
        Assert.Equal(2, renewalCalls);
    }

    [Fact]
    public async Task InstallAsync_WinsOverQueuedRefreshForOldSnapshot()
    {
        var initial = Credentials("initial");
        var installed = Credentials("installed");
        var persistEntered = Signal();
        var allowPersist = Signal();
        var renewalCalls = 0;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) =>
            {
                Interlocked.Increment(ref renewalCalls);
                return Task.FromResult(Credentials("unexpected"));
            },
            async (credentials, cancellationToken) =>
            {
                if (!ReferenceEquals(credentials, installed)) return;
                persistEntered.TrySetResult(true);
                await allowPersist.Task.WaitAsync(cancellationToken);
            });

        var installing = lifecycle.InstallAsync(installed, TestContext.Current.CancellationToken);
        await persistEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queuedRefresh = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        allowPersist.TrySetResult(true);
        var install = await installing;
        var refresh = await queuedRefresh;

        Assert.Equal(CredentialRenewalStatus.Succeeded, install.Status);
        Assert.Equal(CredentialRenewalStatus.Succeeded, refresh.Status);
        Assert.Same(installed, refresh.Credentials);
        Assert.Same(installed, lifecycle.CurrentCredentials);
        Assert.Equal(0, renewalCalls);
    }

    [Fact]
    public async Task RefreshAsync_PersistsReplacementBeforePublishing()
    {
        var initial = Credentials("initial");
        var replacement = Credentials("replacement");
        var persistEntered = Signal();
        var allowPersist = Signal();
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => Task.FromResult(replacement),
            async (credentials, cancellationToken) =>
            {
                Assert.Same(replacement, credentials);
                persistEntered.TrySetResult(true);
                await allowPersist.Task.WaitAsync(cancellationToken);
            });

        var refreshing = lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);
        await persistEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(initial, lifecycle.CurrentCredentials);
        allowPersist.TrySetResult(true);
        var result = await refreshing;

        Assert.Equal(CredentialRenewalStatus.Succeeded, result.Status);
        Assert.Same(replacement, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task RefreshAsync_StaleExpectedSnapshotReturnsCurrentWithoutCloud()
    {
        var stale = Credentials("stale");
        var current = Credentials("current");
        var renewalCalls = 0;
        var lifecycle = new SonyCredentialLifecycle(
            current,
            (_, _, _) =>
            {
                Interlocked.Increment(ref renewalCalls);
                return Task.FromResult(Credentials("unexpected"));
            },
            (_, _) => throw new Xunit.Sdk.XunitException("Persistence must not run."));

        var result = await lifecycle.RefreshAsync(stale, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Succeeded, result.Status);
        Assert.Same(current, result.Credentials);
        Assert.Equal(0, renewalCalls);
    }

    [Fact]
    public async Task RefreshAsync_CancellationDuringRenewalDoesNotPersistOrPublish()
    {
        var initial = Credentials("initial");
        var renewalEntered = Signal();
        var persistenceCalls = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            async (_, _, cancellationToken) =>
            {
                renewalEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Credentials("unreachable");
            },
            (_, _) =>
            {
                Interlocked.Increment(ref persistenceCalls);
                return Task.CompletedTask;
            });

        var refreshing = lifecycle.RefreshAsync(initial, cancellation.Token);
        await renewalEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        Assert.Equal(0, persistenceCalls);
        Assert.Same(initial, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task RefreshAsync_CancellationDuringPersistenceDoesNotPublish()
    {
        var initial = Credentials("initial");
        var persistEntered = Signal();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => Task.FromResult(Credentials("replacement")),
            async (_, cancellationToken) =>
            {
                persistEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        var refreshing = lifecycle.RefreshAsync(initial, cancellation.Token);
        await persistEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        Assert.Same(initial, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task RefreshAsync_PersistenceFailureDoesNotPublish()
    {
        var initial = Credentials("initial");
        var replacement = Credentials("replacement");
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => Task.FromResult(replacement),
            (_, _) => throw new IOException("synthetic persistence failure"));

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Failed, result.Status);
        Assert.Null(result.Credentials);
        Assert.Same(initial, lifecycle.CurrentCredentials);
        Assert.DoesNotContain("synthetic", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_FinalPersistenceFailureKeepsRotatedTokenCheckpoint()
    {
        var initial = Credentials("initial");
        var replacement = Credentials("replacement") with { RefreshToken = "rotated-refresh" };
        SonyCredentials? checkpoint = null;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            async (_, checkpointRotatedRefreshTokenAsync, _) =>
            {
                await checkpointRotatedRefreshTokenAsync("rotated-refresh");
                return replacement;
            },
            (credentials, _) =>
            {
                if (ReferenceEquals(credentials, replacement))
                    throw new IOException("synthetic final persistence failure");
                checkpoint = credentials;
                return Task.CompletedTask;
            });

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Failed, result.Status);
        Assert.NotNull(checkpoint);
        Assert.Same(checkpoint, lifecycle.CurrentCredentials);
        Assert.Equal(initial.KeyId, checkpoint.KeyId);
        Assert.Equal("rotated-refresh", checkpoint.RefreshToken);
        Assert.True(lifecycle.IsLocalKeyRefreshPending(checkpoint));
    }

    [Theory]
    [InlineData(SonyOAuthFailureKind.ReauthenticationRequired, CredentialRenewalStatus.AuthenticationRequired)]
    [InlineData(SonyOAuthFailureKind.Transient, CredentialRenewalStatus.TransientFailure)]
    [InlineData(SonyOAuthFailureKind.Protocol, CredentialRenewalStatus.Failed)]
    public async Task RefreshAsync_MapsOnlyClassifiedOAuthOutcome(
        SonyOAuthFailureKind failureKind,
        CredentialRenewalStatus expectedStatus)
    {
        var initial = Credentials("initial");
        var persistenceCalls = 0;
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => throw new SonyOAuthException(failureKind, "refresh-token-secret"),
            (_, _) =>
            {
                Interlocked.Increment(ref persistenceCalls);
                return Task.CompletedTask;
            });

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Credentials);
        Assert.Equal(0, persistenceCalls);
        Assert.DoesNotContain("refresh-token-secret", result.Diagnostic, StringComparison.Ordinal);
        Assert.Same(initial, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task RefreshAsync_WithoutRenewalMaterialIsUnavailable()
    {
        var initial = Credentials("initial", includeRefreshToken: false);
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Renewal must not run."),
            (_, _) => throw new Xunit.Sdk.XunitException("Persistence must not run."));

        var result = await lifecycle.RefreshAsync(initial, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Unavailable, result.Status);
        Assert.Null(result.Credentials);
        Assert.Same(initial, lifecycle.CurrentCredentials);
    }

    [Fact]
    public async Task InstallAsync_PersistenceFailureKeepsCurrentSnapshot()
    {
        var initial = Credentials("initial");
        var lifecycle = new SonyCredentialLifecycle(
            initial,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Renewal must not run."),
            (_, _) => throw new IOException("synthetic persistence failure"));

        var result = await lifecycle.InstallAsync(Credentials("replacement"), TestContext.Current.CancellationToken);

        Assert.Equal(CredentialRenewalStatus.Failed, result.Status);
        Assert.Same(initial, lifecycle.CurrentCredentials);
    }

    private static SonyCredentials Credentials(string suffix, bool includeRefreshToken = true) => new()
    {
        DeviceId = $"device-{suffix}",
        KeyId = $"key-{suffix}",
        SessionKey = $"session-{suffix}",
        HmacKey = $"hmac-{suffix}",
        RefreshToken = includeRefreshToken ? $"refresh-{suffix}" : null
    };

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
