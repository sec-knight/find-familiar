namespace FindFamiliar.Server.Domain;

public sealed class ContextEntry
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public FamiliarProject Project { get; set; } = null!;

    public Guid? TaskId { get; set; }

    public FamiliarTask? Task { get; set; }

    public Guid? SourceSessionId { get; set; }

    public AgentSession? SourceSession { get; set; }

    public ContextEntryKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public ContextEntryState State { get; set; } = ContextEntryState.Active;

    /// <summary>
    /// When true, nothing about this context entry is ever assembled into a pack sent to a provider.
    ///
    /// The context assembler is the single chokepoint through which anything leaves this machine, and
    /// this flag is what that chokepoint honours. It is deliberately a column rather than a
    /// convention somebody has to remember: a rule enforced by a schema survives a contributor who
    /// has never read the ADR.
    ///
    /// Exclusion is total and is stated rather than hidden. A brief that omitted flagged entries
    /// silently would make the Familiar answer confidently about a world it was only shown part of;
    /// instead the brief says how many were withheld, so "I cannot see everything" is a fact it
    /// carries rather than a hope.
    ///
    /// This is also what makes a future local-model lane possible without redesign: the boundary
    /// already exists, and pointing it at a model on this machine is then a routing decision rather
    /// than a rewrite.
    /// </summary>
    public bool IsSensitive { get; set; }

    public Guid? SupersedesContextEntryId { get; set; }

    public ContextEntry? SupersedesContextEntry { get; set; }

    public DateTime CreatedUtc { get; set; }
}
