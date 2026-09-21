namespace BraviaTheatre.Core.Auth;

public enum CredentialRenewalStatus
{
    Succeeded,
    Unavailable,
    AuthenticationRequired,
    TransientFailure,
    Failed,
    DeviceAssociationRequired
}

public sealed record CredentialRenewalResult(
    CredentialRenewalStatus Status,
    SonyCredentials? Credentials = null,
    string? Diagnostic = null);

/// <summary>Serializes credential installation and silent renewal.</summary>
public sealed class SonyCredentialLifecycle
{
    private readonly record struct CompletedRefreshAttempt(
        long Version,
        SonyCredentials? Current,
        CredentialRenewalResult Result);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<SonyCredentials, Func<string, Task>, CancellationToken, SonyDeviceSelector?, Task<SonyCredentials>> _renewCredentialsAsync;
    private readonly Func<SonyCredentials, CancellationToken, Task> _persistCredentialsAsync;
    private SonyCredentials? _currentCredentials;
    private SonyCredentials? _pendingLocalKeyRefreshCredentials;
    private CompletedRefreshAttempt? _lastRefreshAttempt;
    private long _refreshAttemptVersion;

    public SonyCredentialLifecycle(
        SonyCredentials? initialCredentials,
        Func<SonyCredentials, Func<string, Task>, CancellationToken, Task<SonyCredentials>> renewCredentialsAsync,
        Func<SonyCredentials, CancellationToken, Task> persistCredentialsAsync)
        : this(initialCredentials,
            (credentials, checkpoint, cancellationToken, _) => renewCredentialsAsync(credentials, checkpoint, cancellationToken),
            persistCredentialsAsync)
    {
        ArgumentNullException.ThrowIfNull(renewCredentialsAsync);
    }

    public SonyCredentialLifecycle(
        SonyCredentials? initialCredentials,
        Func<SonyCredentials, Func<string, Task>, CancellationToken, SonyDeviceSelector?, Task<SonyCredentials>> renewCredentialsAsync,
        Func<SonyCredentials, CancellationToken, Task> persistCredentialsAsync)
    {
        _currentCredentials = initialCredentials;
        _renewCredentialsAsync = renewCredentialsAsync ?? throw new ArgumentNullException(nameof(renewCredentialsAsync));
        _persistCredentialsAsync = persistCredentialsAsync ?? throw new ArgumentNullException(nameof(persistCredentialsAsync));
    }

    public SonyCredentials? CurrentCredentials => Volatile.Read(ref _currentCredentials);
    public Action<string>? DiagnosticLog { get; set; }

    public bool IsLocalKeyRefreshPending(SonyCredentials credentials) =>
        ReferenceEquals(Volatile.Read(ref _pendingLocalKeyRefreshCredentials), credentials);

    public async Task<CredentialRenewalResult> InstallAsync(
        SonyCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsValid)
            return Failed("Sony credentials are incomplete.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _persistCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failed("Sony credentials could not be stored.");
            }

            Volatile.Write(ref _pendingLocalKeyRefreshCredentials, null);
            Volatile.Write(ref _currentCredentials, credentials);
            _lastRefreshAttempt = null;
            Interlocked.Increment(ref _refreshAttemptVersion);
            return Succeeded(credentials);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CredentialRenewalResult> RefreshAsync(
        SonyCredentials expected,
        CancellationToken cancellationToken = default,
        SonyDeviceSelector? reassociationSelector = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var observedAttemptVersion = Volatile.Read(ref _refreshAttemptVersion);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _currentCredentials);
            if (_lastRefreshAttempt is { } lastAttempt
                && ReferenceEquals(current, lastAttempt.Current)
                && (lastAttempt.Version > observedAttemptVersion
                    || lastAttempt.Result.Status == CredentialRenewalStatus.AuthenticationRequired)
                && !(reassociationSelector != null && lastAttempt.Result.Status == CredentialRenewalStatus.DeviceAssociationRequired))
            {
                return lastAttempt.Result;
            }

            if (current is null)
                return new(CredentialRenewalStatus.Unavailable, Diagnostic: "No Sony credentials are installed.");
            if (!ReferenceEquals(current, expected)
                && !ReferenceEquals(current, _pendingLocalKeyRefreshCredentials))
                return Succeeded(current);
            if (string.IsNullOrWhiteSpace(current.RefreshToken) || string.IsNullOrWhiteSpace(current.DeviceId))
                return new(CredentialRenewalStatus.Unavailable, Diagnostic: "Silent Sony credential renewal is unavailable.");

            SonyOAuth.LogRenewalDiagnostic(DiagnosticLog,
                $"RenewalStarting; ExpiresAtUtc={current.SessionKeysExpiresAtUtc:O}; RenewalMaterialAvailable=True; PendingCheckpoint={IsLocalKeyRefreshPending(current)}.");
            SonyCredentials? durableCheckpoint = null;

            async Task CheckpointRotatedRefreshTokenAsync(string refreshToken)
            {
                if (string.IsNullOrWhiteSpace(refreshToken))
                    throw new InvalidOperationException("Sony returned an invalid replacement refresh token.");
                if (string.Equals(refreshToken, current.RefreshToken, StringComparison.Ordinal))
                    return;

                var checkpoint = current with { RefreshToken = refreshToken };

                // Once Sony may have invalidated the old token, this brief atomic write must
                // complete even if the connection that initiated renewal is shutting down.
                await PersistRenewalAsync(checkpoint, CancellationToken.None, "RotatedTokenCheckpoint").ConfigureAwait(false);
                durableCheckpoint = checkpoint;
            }

            SonyCredentials renewed;
            try
            {
                renewed = await _renewCredentialsAsync(
                    current,
                    CheckpointRotatedRefreshTokenAsync,
                    cancellationToken,
                    reassociationSelector).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishCheckpoint(durableCheckpoint);
                throw;
            }
            catch (SonyOAuthException error)
            {
                PublishCheckpoint(durableCheckpoint);
                var result = error.Kind switch
                {
                    SonyOAuthFailureKind.ReauthenticationRequired =>
                        new(CredentialRenewalStatus.AuthenticationRequired, Diagnostic: "Sony authorization must be renewed interactively."),
                    SonyOAuthFailureKind.Transient =>
                        new(CredentialRenewalStatus.TransientFailure, Diagnostic: "Sony credential renewal is temporarily unavailable."),
                    SonyOAuthFailureKind.DeviceAssociationRequired =>
                        new(CredentialRenewalStatus.DeviceAssociationRequired, Diagnostic: "The Sony soundbar association needs attention."),
                    _ => Failed("Sony returned an invalid credential-renewal response.")
                };
                return CompleteAttempt(result);
            }
            catch (Exception error)
            {
                PublishCheckpoint(durableCheckpoint);
                SonyOAuth.LogRenewalDiagnostic(DiagnosticLog, $"Phase=Renewal; failed; ExceptionType={error.GetType().Name}.");
                return CompleteAttempt(Failed("Sony credential renewal failed."));
            }

            if (renewed is null || !renewed.IsValid)
            {
                PublishCheckpoint(durableCheckpoint);
                return CompleteAttempt(Failed("Sony returned incomplete local credentials."));
            }
            if (durableCheckpoint != null
                && !string.Equals(renewed.RefreshToken, durableCheckpoint.RefreshToken, StringComparison.Ordinal))
            {
                PublishCheckpoint(durableCheckpoint);
                return CompleteAttempt(Failed("Sony returned inconsistent renewal credentials."));
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PersistRenewalAsync(renewed, cancellationToken, "FinalPersistence").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishCheckpoint(durableCheckpoint);
                throw;
            }
            catch
            {
                PublishCheckpoint(durableCheckpoint);
                return CompleteAttempt(Failed("Renewed Sony credentials could not be stored."));
            }

            Volatile.Write(ref _pendingLocalKeyRefreshCredentials, null);
            Volatile.Write(ref _currentCredentials, renewed);
            return CompleteAttempt(Succeeded(renewed));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistRenewalAsync(SonyCredentials credentials, CancellationToken cancellationToken, string phase)
    {
        SonyOAuth.LogRenewalDiagnostic(DiagnosticLog, $"Phase={phase}; started.");
        try
        {
            await _persistCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
            SonyOAuth.LogRenewalDiagnostic(DiagnosticLog, $"Phase={phase}; succeeded; Persisted=True.");
        }
        catch (Exception error)
        {
            SonyOAuth.LogRenewalDiagnostic(DiagnosticLog, $"Phase={phase}; failed; Persisted=False; ExceptionType={error.GetType().Name}.");
            throw;
        }
    }

    private void PublishCheckpoint(SonyCredentials? checkpoint)
    {
        if (checkpoint is null)
            return;

        Volatile.Write(ref _pendingLocalKeyRefreshCredentials, checkpoint);
        Volatile.Write(ref _currentCredentials, checkpoint);
    }

    private CredentialRenewalResult CompleteAttempt(CredentialRenewalResult result)
    {
        SonyOAuth.LogRenewalDiagnostic(DiagnosticLog, $"RenewalCompleted; Status={result.Status}; Diagnostic={result.Diagnostic ?? "none"}.");
        var version = Interlocked.Increment(ref _refreshAttemptVersion);
        _lastRefreshAttempt = new(version, Volatile.Read(ref _currentCredentials), result);
        return result;
    }

    private static CredentialRenewalResult Succeeded(SonyCredentials credentials) =>
        new(CredentialRenewalStatus.Succeeded, credentials);

    private static CredentialRenewalResult Failed(string diagnostic) =>
        new(CredentialRenewalStatus.Failed, Diagnostic: diagnostic);
}
