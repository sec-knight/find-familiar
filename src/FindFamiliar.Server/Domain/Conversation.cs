namespace FindFamiliar.Server.Domain;

/// <summary>
/// A durable, project-work-focused intake interaction: ordered messages plus exactly one current
/// work proposal. Not an open-ended chat.
///
/// <see cref="ApprovedTaskId"/> and <see cref="ApprovedSessionId"/> are durable links to work the
/// user approved. They are recorded by the approval transaction and never used to decide whether
/// that work may execute.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.AwaitingApproval;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public Guid? ApprovedTaskId { get; set; }

    public FamiliarTask? ApprovedTask { get; set; }

    public Guid? ApprovedSessionId { get; set; }

    public AgentSession? ApprovedSession { get; set; }

    public ICollection<ConversationMessage> Messages { get; } = new List<ConversationMessage>();

    public WorkProposal? Proposal { get; set; }
}
