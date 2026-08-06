namespace FindFamiliar.Server.Domain;

/// <summary>
/// Lifecycle of a drafted plan. <see cref="Pending"/> is the only non-terminal state; nothing returns
/// to it.
/// </summary>
public enum FamiliarPlanStatus
{
    /// <summary>Drafted and shown to a human. Nothing has been created.</summary>
    Pending,

    /// <summary>A human approved it and its effects committed.</summary>
    Approved,

    /// <summary>A human decided it will not run. Nothing was created.</summary>
    Declined
}

/// <summary>
/// One proposed piece of work inside a plan. A record of what a person will be shown, not authority
/// to create anything.
///
/// Typed columns rather than a JSON blob, for the reason <see cref="FamiliarActionProposal"/> gives:
/// a blob moves validation out of the schema and into a parser, and that parser would be reading
/// model output. There is no column naming a handler, a command, a service or a table — the only
/// executable choice an item expresses is <see cref="Role"/>, which is a closed three-member enum.
/// </summary>
public sealed class FamiliarPlanItem
{
    public const int MaxTitleLength = 200;
    public const int MaxRequestedOutcomeLength = 4_000;

    /// <summary>
    /// Ids the drafting model cited for this item, space separated, in the same form a turn records
    /// its offered evidence. Validated against what was actually in the pack before it is stored, so
    /// an item cannot claim a source that was never shown.
    /// </summary>
    public const int MaxEvidenceLength = 256;

    public Guid Id { get; set; }

    public Guid PlanId { get; set; }

    public FamiliarPlanProposal Plan { get; set; } = null!;

    /// <summary>Display order as drafted, so the plan reads the same on every device and every reload.</summary>
    public int Position { get; set; }

    public string Title { get; set; } = string.Empty;

    public string RequestedOutcome { get; set; } = string.Empty;

    /// <summary>
    /// The session role this item would start, or null for a task that is only created.
    ///
    /// Null is a real answer, not a missing one: "write this down as work, do not start anything" is
    /// a legitimate item and the commonest safe one.
    /// </summary>
    public AgentSessionRole? Role { get; set; }

    public string? EvidenceEntryIds { get; set; }

    /// <summary>
    /// Whether a human has this item in or out. Defaults to in, and is the human's to change — an
    /// itemised approval that shipped every item regardless would be a checkbox that did nothing.
    /// </summary>
    public bool IsIncluded { get; set; } = true;

    /// <summary>
    /// The task this item created, once a plan is approved. Null while Pending, and null forever for
    /// an item a human excluded.
    /// </summary>
    public Guid? CreatedTaskId { get; set; }
}

/// <summary>
/// A plan the Familiar drafted in conversation: several proposed items, approved or declined as one.
///
/// <b>One row for the whole plan, and at most one undecided per conversation.</b> That shape is the
/// same one <c>IX_FamiliarActionProposals_ConversationId_Pending</c> uses and it is chosen for the
/// same reason: contenders race for a single row, a human decides once, and a half-approved sprint
/// cannot exist in the database. A plan is a unit of intent, and approving four of its six items by
/// accident because two writes interleaved is not a state anyone should be able to reach.
///
/// A Pending plan has produced nothing — no task, no session, no context entry, no revision change.
/// Only an explicit human approval turns it into work, and every gate is re-evaluated inside that
/// approving transaction (ADR-0014). This row says what was proposed, never what may execute.
///
/// <see cref="ConcurrencyToken"/> is the fence, as on <see cref="FamiliarActionProposal"/> and
/// <see cref="SessionHandoff"/>: a decision presents the token it reviewed and each transition
/// rotates it, so a plan cannot be approved twice or approved from a stale rendering.
/// </summary>
public sealed class FamiliarPlanProposal
{
    public const int MaxSummaryLength = 2_000;

    /// <summary>
    /// A bound on how much one approval can create at once. Not a technical limit — a plan of forty
    /// items is not reviewable, and an approval nobody could have read is the risk this whole design
    /// is arranged against.
    /// </summary>
    public const int MaxItems = 8;

    public Guid Id { get; set; }

    public Guid ChatId { get; set; }

    public FamiliarChat Chat { get; set; } = null!;

    /// <summary>The turn whose reply drafted this, so the plan can be shown beside its reasoning.</summary>
    public Guid TurnId { get; set; }

    public FamiliarChatTurn Turn { get; set; } = null!;

    /// <summary>
    /// The project this plan's work belongs to. Required: a plan spanning projects is out of scope
    /// for Sprint 13, and allowing a null here would make "which project does this task go in?" a
    /// question answered at approval time from model text.
    /// </summary>
    public Guid ProjectId { get; set; }

    public FamiliarProject Project { get; set; } = null!;

    public FamiliarPlanStatus Status { get; set; } = FamiliarPlanStatus.Pending;

    public Guid ConcurrencyToken { get; set; }

    /// <summary>
    /// The project's context revision when this was drafted. Re-checked when the plan is approved: a
    /// plan drafted against a project that has since moved is a plan about a world that no longer
    /// exists, and it is refused with that reason rather than applied anyway.
    /// </summary>
    public int ObservedContextRevision { get; set; }

    /// <summary>One or two sentences of what this plan is for. The Familiar's own words.</summary>
    public string Summary { get; set; } = string.Empty;

    public List<FamiliarPlanItem> Items { get; set; } = [];

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Set when a human approved or declined it.</summary>
    public DateTime? DecidedUtc { get; set; }

    public bool IsPending => Status == FamiliarPlanStatus.Pending;
}
