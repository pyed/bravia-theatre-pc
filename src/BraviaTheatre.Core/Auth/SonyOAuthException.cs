namespace BraviaTheatre.Core.Auth;

public enum SonyOAuthFailureKind
{
    ReauthenticationRequired,
    Transient,
    Protocol,
    DeviceAssociationRequired
}

/// <summary>A classified Sony cloud failure whose message never contains response content.</summary>
public sealed class SonyOAuthException : Exception
{
    internal SonyOAuthException(SonyOAuthFailureKind kind, string message, Exception? innerException = null, int? httpStatusCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
    }

    public SonyOAuthFailureKind Kind { get; }
    public int? HttpStatusCode { get; }
}
