namespace BraviaTheatre.Core.Auth;

public enum SonyOAuthFailureKind
{
    ReauthenticationRequired,
    Transient,
    Protocol
}

/// <summary>A classified Sony cloud failure whose message never contains response content.</summary>
public sealed class SonyOAuthException : Exception
{
    internal SonyOAuthException(SonyOAuthFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public SonyOAuthFailureKind Kind { get; }
}
