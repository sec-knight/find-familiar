namespace FindFamiliar.Server.Domain;

/// <summary>Why a handoff was proposed. Derived from the source session's role and terminal status only.</summary>
public enum SessionHandoffKind
{
    /// <summary>The source session completed, so the next role in the sequence is proposed.</summary>
    NextRole,

    /// <summary>The source session was cancelled, so the same role is proposed again.</summary>
    RetrySameRole
}
