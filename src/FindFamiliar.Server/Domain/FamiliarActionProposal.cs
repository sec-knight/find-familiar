namespace FindFamiliar.Server.Domain;

/// <summary>
/// A record of what a human was shown and invited to confirm. It is not authority to act.
///
/// A Pending proposal has produced nothing: no task, no session, no context entry, no revision
/// change. Only an explicit human confirmation turns it into work, and every gate is re-evaluated
/// inside that confirming transaction — this row says what was proposed, never what may execute.
///
/// Parameters are typed columns rather than a JSON blob, exactly as <see cref="WorkProposal"/> does
/// it. A blob would move validation out of the schema and into a parser, and that parser would be
/// reading model output. For the same reason there is no column naming a handler, command, service
/// or table: <see cref="Kind"/> is a closed two-member enum, so model text can never select arbitrary
/// executable behaviour.
///
/// <see cref="ConcurrencyToken"/> is the fence, as in <see cref="SessionHandoff"/>: every decision
/// presents the token it reviewed and each successful transition rotates it, so only one contender
/// can consume a Pending proposal.
/// </summary>
public sealed class FamiliarActionProposal
{
    public const int MaxTitleLength = 200;
    public const int MaxRequestedOutcomeLength = 4_000;

    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public FamiliarConversation Conversation { get; set; } = null!;

    /// <summary>
    /// Denormalised from the conversation so project ownership can be re-checked without a join, and
    /// so a proposal id from another project cannot be confirmed from this page.
    /// </summary>
    public Guid ProjectId { get; set; }

    public FamiliarProject Project { get; set; } = null!;

    /// <summary>The Familiar message that proposed this, so the page can show it beside its reason.</summary>
    public Guid MessageId { get; set; }

    public FamiliarMessage Message { get; set; } = null!;

    public FamiliarActionKind Kind { get; set; }

    public FamiliarActionStatus Status { get; set; } = FamiliarActionStatus.Pending;

    public Guid ConcurrencyToken { get; set; }

    /// <summary>The project's context revision when this was proposed.</summary>
    public int ObservedContextRevision { get; set; }

    /// <summary><see cref="FamiliarActionKind.CreateTask"/> only.</summary>
    public string? Title { get; set; }

    /// <summary><see cref="FamiliarActionKind.CreateTask"/> only.</summary>
    public string? RequestedOutcome { get; set; }

    /// <summary><see cref="FamiliarActionKind.StartPlanner"/> only: the task the session would run on.</summary>
    public Guid? TargetTaskId { get; set; }

    public FamiliarTask? TargetTask { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Set when a human confirmed or dismissed it.</summary>
    public DateTime? DecidedUtc { get; set; }

    /// <summary>Durable link to the task a confirmation created. Null until then, null forever if dismissed.</summary>
    public Guid? CreatedTaskId { get; set; }

    public FamiliarTask? CreatedTask { get; set; }

    /// <summary>Durable link to the session a confirmation created.</summary>
    public Guid? CreatedSessionId { get; set; }

    public AgentSession? CreatedSession { get; set; }
}
