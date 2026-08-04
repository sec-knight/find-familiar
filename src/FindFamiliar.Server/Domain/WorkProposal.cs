namespace FindFamiliar.Server.Domain;

/// <summary>
/// The editable, deterministic proposal a user reviews before any work exists. Exactly one
/// proposal exists per conversation.
///
/// <see cref="ConcurrencyToken"/> is the fence. Every state-changing action presents the token it
/// reviewed, and each successful transition rotates it, so a stale form can never overwrite newer
/// data and only one contender can consume a Pending proposal.
/// </summary>
public sealed class WorkProposal
{
    public const int MaxTitleLength = 200;
    public const int MaxRequestedOutcomeLength = 4_000;

    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Conversation Conversation { get; set; } = null!;

    /// <summary>Null while the user still has to choose between candidate projects.</summary>
    public Guid? ProjectId { get; set; }

    public FamiliarProject? Project { get; set; }

    public string Title { get; set; } = string.Empty;

    public string RequestedOutcome { get; set; } = string.Empty;

    /// <summary>Fixed to <see cref="AgentSessionRole.Planner"/> in Sprint 08.</summary>
    public AgentSessionRole Role { get; set; } = AgentSessionRole.Planner;

    /// <summary>
    /// The selected project's context revision at the moment the user last reviewed it. Approval
    /// requires this to still equal the project's current revision, so work is never dispatched
    /// against context the user never saw.
    /// </summary>
    public int? ObservedContextRevision { get; set; }

    public WorkProposalStatus Status { get; set; } = WorkProposalStatus.Pending;

    public int Revision { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public Guid? CreatedTaskId { get; set; }

    public Guid? CreatedSessionId { get; set; }
}
