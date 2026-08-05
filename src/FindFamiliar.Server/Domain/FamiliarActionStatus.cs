namespace FindFamiliar.Server.Domain;

/// <summary>
/// Lifecycle of a proposed action. <see cref="Pending"/> is the only non-terminal state; nothing
/// returns to it.
/// </summary>
public enum FamiliarActionStatus
{
    /// <summary>Shown to a human and awaiting their decision. Nothing has been created.</summary>
    Pending,

    /// <summary>A human confirmed it and its effects committed.</summary>
    Confirmed,

    /// <summary>A human decided it will not run. Nothing was created.</summary>
    Dismissed
}
