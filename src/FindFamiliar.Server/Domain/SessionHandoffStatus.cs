namespace FindFamiliar.Server.Domain;

/// <summary>
/// Lifecycle of a proposed next step. <see cref="Pending"/> is the only non-terminal state; nothing
/// returns to it.
/// </summary>
public enum SessionHandoffStatus
{
    /// <summary>Awaiting a human decision. Nothing has been created.</summary>
    Pending,

    /// <summary>A human approved it and exactly one session was created.</summary>
    Approved,

    /// <summary>A human decided this step will not run.</summary>
    Declined,

    /// <summary>
    /// A newer terminal session event on the same task replaced this decision point. System-only:
    /// no request may set this value.
    /// </summary>
    Superseded
}
