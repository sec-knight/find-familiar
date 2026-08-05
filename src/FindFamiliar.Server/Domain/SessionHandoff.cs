namespace FindFamiliar.Server.Domain;

/// <summary>
/// A proposed next step on a task, recorded when a session reaches a terminal state.
///
/// A handoff is consent state, not work and not context. Creating one produces no task, no session,
/// no context entry, and no context-revision change. Only an explicit human approval turns it into a
/// Started session, and that approval is fenced by <see cref="ConcurrencyToken"/> exactly as ADR-0009
/// fenced conversational approval.
///
/// It deliberately carries no free text. The assignment packet already renders the source session's
/// plan, summary and raw output as context entries, so a notes field here would be a second,
/// divergent copy of an artifact that already exists — and a channel for instructions that bypass
/// the context system's provenance. A human with guidance to add records a context entry on the task.
/// </summary>
public sealed class SessionHandoff
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public FamiliarTask Task { get; set; } = null!;

    /// <summary>The terminal session that produced this proposal.</summary>
    public Guid SourceSessionId { get; set; }

    public AgentSession SourceSession { get; set; } = null!;

    /// <summary>The terminal status the source session reached: Completed or Cancelled.</summary>
    public AgentSessionStatus SourceOutcome { get; set; }

    public AgentSessionRole ProposedRole { get; set; }

    public SessionHandoffKind Kind { get; set; }

    public SessionHandoffStatus Status { get; set; } = SessionHandoffStatus.Pending;

    /// <summary>
    /// The project's context revision when this handoff was created. Advisory only: it is displayed
    /// so a human can see that context moved, and is deliberately not an approval gate.
    ///
    /// Sprint 08's revision gate protects content the user authored — project, title, requested
    /// outcome — against context that changed underneath it. A handoff has no such content. The only
    /// decision is "run this role on this task now", and the session created reads whatever context
    /// is current at its own start. Gating here would block on activity in any other task in the
    /// same project, for no safety gain.
    /// </summary>
    public int ObservedContextRevision { get; set; }

    /// <summary>
    /// The fence. Every decision presents the token it reviewed, and each successful transition
    /// rotates it, so only one contender can consume a Pending handoff.
    /// </summary>
    public Guid ConcurrencyToken { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Set when a human approved or declined it.</summary>
    public DateTime? DecidedUtc { get; set; }

    /// <summary>Durable link to the session approval created. Null until then, and null forever if declined.</summary>
    public Guid? CreatedSessionId { get; set; }

    public AgentSession? CreatedSession { get; set; }
}
