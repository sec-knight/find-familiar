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
/// <param name="Records">
/// The project's own recorded context — the entries written against the project rather than against
/// any one task, newest first.
///
/// <b>Enumerated, not searched.</b> Search applies a relevance floor, which is right for a question
/// and wrong for an inventory: a constraint nobody thinks to query for would be invisible, and a
/// client cannot ask about a record whose existence it has no way to learn. This is the same list the
/// project page shows, minus what this boundary filters.
/// </param>
/// <param name="RecordsWithheld">
/// How many of the project's own records were not returned — older than the bound, marked sensitive,
/// or raw provider input and output. A count and never a hint, so a reader is never left believing a
/// short list is the whole of it.
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
    IReadOnlyList<string> Limitations,
    IReadOnlyList<FamiliarTaskRecord> Records,
    int RecordsWithheld)
{
    /// <summary>
    /// Project records carried in one answer. A project's whole recorded history is not a
    /// conversational unit, and the count of what was left out is what keeps the bound honest.
    /// </summary>
    public const int MaxRecords = 12;
}

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
/// <param name="DecisionKind">
/// Which kind of decision this is: <c>SessionHandoff</c> — whether to run a proposed next step on an
/// existing task — or <c>PlanProposal</c> — whether to turn a drafted plan into work. They are
/// reported together because from the human's side they are one question, "what needs me", and a
/// client that had to know to ask twice would eventually ask once.
/// </param>
/// <param name="TaskId">The task this concerns, or null for a plan, which has no task until approved.</param>
/// <param name="PlannedItems">
/// What approving a plan would create, exactly as drafted. Null for a handoff.
///
/// Present so a person can be told what they are agreeing to before they agree. It is a description,
/// not a menu: the relay carries approve or reject and cannot include, exclude or reword an item.
/// </param>
public sealed record FamiliarOpenDecision(
    Guid DecisionId,
    string DecisionKind,
    Guid ProjectId,
    string ProjectName,
    Guid? TaskId,
    string? TaskTitle,
    string Reason,
    string? ProposedRole,
    string? ProposedKind,
    string? PriorOutcome,
    string? Evidence,
    IReadOnlyList<string> LegalChoices,
    Guid ExpectedConcurrencyToken,
    DateTime UpdatedUtc,
    IReadOnlyList<FamiliarPlannedItem>? PlannedItems = null);

/// <summary>
/// One task a plan would create if approved, as drafted.
///
/// <see cref="Role"/> is the role that would run on it — and only the first included item naming one
/// starts a session, because a plan written before any of it ran is a guess and the first result is
/// the best evidence about whether the next step is still right (ADR-0014).
/// </summary>
public sealed record FamiliarPlannedItem(
    string Title,
    string RequestedOutcome,
    string? Role,
    bool IsIncluded);

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

// ---------------------------------------------------------------- one task, in full

/// <summary>One session that ran, or is running, on a task.</summary>
/// <param name="Provider">
/// Which provider executed it, where one was recorded. Identity only — never a credential, never a
/// key, never an endpoint.
/// </param>
public sealed record FamiliarTaskSession(
    Guid SessionId,
    string Role,
    string Status,
    string? Provider,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    FamiliarSessionFailure? Failure = null);

public sealed record FamiliarSessionFailure(
    string Category,
    int? AdapterExitCode,
    bool? ProviderLaunched,
    int? ProviderExitCode,
    string Message);

/// <summary>
/// A record produced about this task, as an external client is shown it.
///
/// <see cref="SourceSessionId"/> is what links a result back to the session that produced it, so a
/// reader can say "the Reviewer found X" rather than "something found X".
/// </summary>
public sealed record FamiliarTaskRecord(
    Guid ContextId,
    string Category,
    string Title,
    string Excerpt,
    DateTime RecordedUtc,
    Guid? SourceSessionId);

/// <summary>
/// A complete, bounded human-relevant Planner artifact for a session handoff. The content is paged
/// so the whole stored artifact can be inspected without ever returning raw provider I/O.
/// </summary>
public static class FamiliarSessionHandoffPlanDefaults
{
    public const int DefaultPageLength = 4_000;
    public const int MaxPageLength = 4_000;
}

public sealed record FamiliarSessionHandoffPlan(
    Guid HandoffId,
    Guid TaskId,
    Guid ProjectId,
    string ProjectName,
    string TaskTitle,
    string Goal,
    string RequestedOutcome,
    string SourceRole,
    string? ProposedRole,
    string? ProposedKind,
    string HandoffStatus,
    string ArtifactTitle,
    string Content,
    int Offset,
    int TotalLength,
    bool HasMore,
    string Disclosure);

/// <summary>
/// Everything the Demiplane's task page shows about one task, for a caller entitled to see it.
///
/// <b>Assembled from the two services that already own these answers</b> — the Demiplane projection
/// for what the task's state means, and the context projection for its sessions and records. Neither
/// is re-derived here, because a second opinion about task state is exactly what ADR-0011 forbids and
/// what would make the Familiar contradict the page about the same task.
///
/// <b>Two filters this boundary applies that the page does not.</b> The task page serves the owner of
/// every project; this serves a credential a vendor holds. So entries marked sensitive are removed,
/// and raw provider prompts and output are removed — the same two rules the retrieval path applies,
/// applied here for the same reason and stated in <see cref="RecordsWithheld"/> rather than silently.
/// </summary>
/// <param name="RecordsWithheld">
/// How many of this task's records were not shown. A count and never a hint: a reader that sees three
/// records and no count will believe it has the whole history.
/// </param>
public sealed record FamiliarTaskDetail(
    Guid TaskId,
    string Title,
    string RequestedOutcome,
    string Status,
    string DisplayState,
    string Reason,
    bool NeedsHumanAttention,
    Guid ProjectId,
    string ProjectName,
    IReadOnlyList<FamiliarTaskSession> Sessions,
    IReadOnlyList<FamiliarTaskRecord> Records,
    int RecordsWithheld,
    FamiliarOpenDecision? AwaitingDecision,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string Disclosure)
{
    /// <summary>Records carried in one answer. A task's whole history is not a conversational unit.</summary>
    public const int MaxRecords = 12;

    /// <summary>Bound on one record's text, so a long session artifact cannot fill a context window.</summary>
    public const int MaxExcerptLength = 1_200;
}
