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
