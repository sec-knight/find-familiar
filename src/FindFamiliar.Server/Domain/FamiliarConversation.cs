namespace FindFamiliar.Server.Domain;

/// <summary>
/// The durable conversation about one project: append-only messages plus the proposals they raised.
///
/// Deliberately not <see cref="Conversation"/>. That aggregate is a work-intake interaction with
/// exactly one proposal and a terminal status, and <c>WorkApprovalService</c>'s safety rests on all
/// three of those properties. A per-project, never-terminal, many-proposal chat would have to break
/// each of them, so it lives in its own tables and shares nothing but the database.
///
/// One conversation per project, enforced by a unique index on <see cref="ProjectId"/>. Continuity
/// across days is the point; a thread per session would fragment the memory this application exists
/// to preserve. Multiple threads later means relaxing one index, and nothing else assumes one.
/// </summary>
public sealed class FamiliarConversation
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public FamiliarProject Project { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ICollection<FamiliarMessage> Messages { get; } = new List<FamiliarMessage>();

    public ICollection<FamiliarActionProposal> Proposals { get; } = new List<FamiliarActionProposal>();
}
