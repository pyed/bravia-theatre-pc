namespace BraviaTheatre.Core;

/// <summary>
/// How much a diagnostic message matters. The log level setting keeps this level and the ones
/// above it, so each message states its level instead of having one guessed from its wording.
/// </summary>
public enum DiagnosticLevel
{
    /// <summary>Failures, and decisions the user will notice, such as sign-in being required.</summary>
    Critical,

    /// <summary>The connection's life: discovery results, handshakes, renewals.</summary>
    Info,

    /// <summary>Routine detail, such as every state update or retry.</summary>
    Verbose
}
