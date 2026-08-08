namespace FindFamiliar.Server.Services.Familiar.Gateway;

/// <summary>
/// What an external client is told the Familiar is.
///
/// Deliberately small. This is identity, not personality: enough for a frontier client to know which
/// Familiar it is speaking for and what it may ask of it, and nothing that would make the client the
/// owner of the Familiar's character. The durable persona belongs to this server, and a manifest that
/// shipped a paragraph of system prompt would be handing it to whichever body happened to connect.
/// </summary>
/// <param name="WriteCapabilities">
/// Empty, and stated rather than omitted. A client that is told nothing about writes has to guess
/// whether the absence is a limitation or an oversight; a client shown an empty list has been told.
/// Sprint 14 exposes no mutation of any kind, and this field is where that stops being true if it
/// ever does.
/// </param>
public sealed record FamiliarManifest(
    string Name,
    string Kind,
    string Description,
    string? Guidance,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> WriteCapabilities);

/// <summary>One recorded piece of context, as an external body is shown it.</summary>
/// <param name="ContextId">
/// The durable id of the context entry. Carried so an answer can cite something checkable, and so a
/// later conversation can refer to the same record rather than to a paraphrase of it.
/// </param>
/// <param name="Excerpt">
/// A window of the entry, not the whole of it. <see cref="IsExcerpted"/> says which, because a model
/// shown a truncated record and not told it was truncated will treat the tail it never saw as absent
/// rather than as unread.
/// </param>
public sealed record FamiliarContextItem(
    Guid ContextId,
    Guid ProjectId,
    string ProjectName,
    string Category,
    string Title,
    string Excerpt,
    bool IsExcerpted,
    DateTime RecordedUtc);

/// <summary>
/// What a search of the Familiar's memory found, and what it could not or would not carry.
///
/// The empty result is the interesting one. An external model handed no items and no explanation will
/// answer from whatever it recalls about software projects in general, in the same confident register
/// it uses for things it actually knows — the failure this whole system exists to prevent, now one
/// network hop further away where it is harder to see. So the two empty cases are distinguished:
/// <see cref="NoMatchAboveFloor"/> means the store had near-misses and none was responsive, and a bare
/// empty <see cref="Items"/> means nothing shared a word with the question at all.
/// </summary>
/// <param name="SensitiveWithheld">
/// How many matching records were withheld for sensitivity. A count and never a hint: the client is
/// told that something exists and nothing about what, which is the same what-not-which rule the
/// native retrieval path follows.
/// </param>
/// <param name="BelowThreshold">
/// How many records shared a word with the question and did not clear the relevance floor. Disclosed
/// so "nothing relevant" is distinguishable from "nothing at all", and so the reader can tell that a
/// search actually ran.
/// </param>
public sealed record FamiliarContextResult(
    string Query,
    Guid? ProjectId,
    string? ProjectName,
    IReadOnlyList<FamiliarContextItem> Items,
    int SensitiveWithheld,
    int BelowThreshold,
    bool Truncated,
    string Disclosure)
{
    public bool FoundNothing => Items.Count == 0;

    /// <summary>The search ran, something was close, and none of it was close enough.</summary>
    public bool NoMatchAboveFloor => Items.Count == 0 && BelowThreshold > 0;
}

/// <summary>One task inside a project snapshot, reduced to what an outside reader needs.</summary>
/// <param name="NeedsHumanAttention">
/// Copied from the Demiplane's own classification rather than derived again here. ADR-0011 settled
/// that the Demiplane owns what a task's state is, and a gateway holding a second opinion would be a
/// second set of rules to keep in step — with the disagreement visible to an external client, which
/// is the worst place for it to surface.
/// </param>
public sealed record FamiliarProjectTask(
    Guid TaskId,
    string Title,
    string DisplayState,
    string Reason,
    bool NeedsHumanAttention,
    string? AwaitingRole);

/// <summary>
/// One project as an external body is shown it: shape, health, what is waiting on a person.
/// </summary>
/// <param name="NewestRecordedActivityUtc">
/// When the newest record about this project was written, or null when nothing has been.
///
/// Carried because a snapshot without it is silently a claim about the present, and an external model
/// will read it in the present tense. This is the field that turns "the project is in Sprint 13" back
/// into "the newest record is from Sprint 13".
/// </param>
/// <param name="Limitations">
/// What this snapshot could not see, in its own words. An external reader has no other way to learn
/// the edges of what it was handed.
/// </param>
public sealed record FamiliarProjectContext(
    Guid ProjectId,
    string Name,
    string Purpose,
    int TotalTasks,
    int NeedsAttentionCount,
    int RunningCount,
    IReadOnlyList<FamiliarProjectTask> Tasks,
    int TasksOmitted,
    DateTime? NewestRecordedActivityUtc,
    IReadOnlyList<string> Limitations);

/// <summary>
/// The list an external client is shown when it has to choose a project.
///
/// Sensitive projects are absent, not marked absent, and the count is the only trace. Naming a
/// project a caller may not read is itself a disclosure — "which" is exactly what the sensitivity
/// rule withholds.
/// </summary>
public sealed record FamiliarProjectList(
    IReadOnlyList<FamiliarProjectSummary> Projects,
    int SensitiveWithheld);

public sealed record FamiliarProjectSummary(
    Guid ProjectId,
    string Name,
    string Purpose,
    int NeedsAttentionCount,
    DateTime? NewestRecordedActivityUtc);

/// <summary>
/// One decision Find Familiar is currently waiting on a human for.
///
/// <b>Reported, never inferred.</b> Every field comes from the Demiplane's own projection of persisted
/// rows — the same classification the human sees on the project page. Nothing here is composed from
/// model prose, and a decision appears only when a real Pending handoff row exists. That matters
/// because the next slice will let a client carry a person's answer back: a decision invented from
/// prose would be an invitation to approve something that was never asked.
/// </summary>
/// <param name="DecisionId">
/// The opaque identifier a later submission will name. It identifies the decision point, not the task.
/// </param>
/// <param name="ExpectedConcurrencyToken">
/// The stale-decision fence. A client reads it here and presents it when submitting, so a decision
/// taken against a view that has since moved is refused rather than applied.
///
/// Safe to disclose, and deliberately so: it is a fence, not a capability. Holding it with only
/// <c>familiar.read</c> permits nothing — there is no operation it can be presented to, and when one
/// exists it will require <c>familiar.decide</c> in its own right.
/// </param>
/// <param name="LegalChoices">
/// What the workflow will actually accept for this decision right now. Never a superset: a client that
/// offers a person a choice the domain would refuse has made the person's answer meaningless.
/// </param>
/// <param name="Evidence">
/// What the finished session found, in the Familiar's own plain-language account of persisted history.
/// This is what lets a client explain the decision rather than merely announce it.
/// </param>
public sealed record FamiliarOpenDecision(
    Guid DecisionId,
    string DecisionKind,
    Guid ProjectId,
    string ProjectName,
    Guid TaskId,
    string TaskTitle,
    string Reason,
    string ProposedRole,
    string ProposedKind,
    string? PriorOutcome,
    string? Evidence,
    IReadOnlyList<string> LegalChoices,
    Guid ExpectedConcurrencyToken,
    DateTime UpdatedUtc);

/// <summary>
/// Everything waiting on the human, across every project this caller may read.
///
/// <b>Bounded and counted, like every other gateway answer.</b> A caller is told how many decisions
/// were withheld for sensitivity and how many were omitted for bounds, so an empty or short list is
/// never mistaken for "nothing is waiting".
/// </summary>
public sealed record FamiliarOpenDecisionList(
    IReadOnlyList<FamiliarOpenDecision> Decisions,
    int SensitiveWithheld,
    int Omitted,
    string Disclosure)
{
    /// <summary>A person cannot act on twenty decisions in a conversation turn; they can act on a few.</summary>
    public const int MaxDecisions = 10;
}

// ---------------------------------------------------------------- runtime: why work is or is not moving

/// <summary>
/// One worker, as the Familiar is shown it — the same facts the Demiplane's Workers page renders.
///
/// <b>Enough to explain, not merely to report.</b> A task that says "Waiting for an available Planner"
/// is only half an answer; the other half is whether a Planner-capable worker exists, is enabled, has
/// heartbeated recently, and is already busy. Those are four different problems with four different
/// fixes, and a client that cannot tell them apart can only repeat the display string back.
/// </summary>
/// <param name="SecondsSinceHeartbeat">
/// Derived at read time, and included because "last heartbeat 02:14" means nothing to a reader that
/// does not know what time it is now.
/// </param>
/// <param name="ActiveWork">What this worker is running, or null when it is idle.</param>
public sealed record FamiliarWorker(
    string WorkerKey,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Capabilities,
    string Availability,
    double SecondsSinceHeartbeat,
    FamiliarWorkerActiveWork? ActiveWork);

/// <param name="TaskTitle">
/// Null when the claimed task belongs to a project this caller may not read. The claim itself is
/// still reported — a worker being busy is a fact about the machine — but what it is busy with is
/// not disclosed, exactly as a sensitive project is never named anywhere else.
/// </param>
public sealed record FamiliarWorkerActiveWork(
    string Role,
    string? TaskTitle,
    bool LeaseExpired);

/// <summary>
/// Whether a role can actually run right now, and if not, which of the several possible reasons
/// applies. This is the field that turns "waiting for a Planner" into something a person can act on.
/// </summary>
/// <param name="Blocked">
/// True when no enabled worker declares this role at all. The task cannot progress until a worker is
/// registered or enabled, which is an operator action rather than something waiting will fix.
/// </param>
public sealed record FamiliarRoleReadiness(
    string Role,
    int WorkersDeclaringRole,
    int EnabledAndOnline,
    int IdleAndReady,
    bool Blocked,
    string Explanation);

/// <summary>
/// The runtime the Familiar's work actually executes on: who can run what, what they are running, and
/// what the providers behind them report.
///
/// This exists because of a specific failure. The Demiplane could show that a task was waiting for a
/// Planner while the Familiar could only repeat that sentence — it had no way to inspect the worker
/// pool and say whether the Planner was missing, disabled, offline, or simply busy. Peer frontends
/// over the same state should not differ in what they can find out (ADR-0019).
/// </summary>
public sealed record FamiliarRuntimeState(
    IReadOnlyList<FamiliarWorker> Workers,
    IReadOnlyList<FamiliarRoleReadiness> Roles,
    IReadOnlyList<FamiliarProviderCapacity> Providers,
    int ActiveClaims,
    string Disclosure);

/// <param name="Detail">The provider's own explanation where it gave one. Never a credential, never a key.</param>
public sealed record FamiliarProviderCapacity(
    string Provider,
    string Status,
    string Confidence,
    string? Detail);
