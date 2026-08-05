using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Providers;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>One task, as the Demiplane already decided to describe it.</summary>
/// <remarks>
/// Every field here is copied from <see cref="DemiplaneTask"/> rather than derived again.
/// <see cref="RecommendedNextAction"/> is carried for the same reason: the Demiplane owns what a
/// human should do about a state, and a second opinion composed here would be a second set of rules
/// to keep in step with ADR-0011's.
/// </remarks>
public sealed record SnapshotTask(
    Guid TaskId,
    string Title,
    TaskDisplayState DisplayState,
    TaskDisplayReasonCode ReasonCode,
    string ReasonText,
    bool NeedsHumanAttention,
    AgentSessionRole? CurrentRole,
    string? Provider,
    bool HasPendingHandoff,
    string? RecommendedNextAction);

/// <summary>One session in this project's recent history.</summary>
public sealed record SnapshotSession(
    Guid SessionId,
    Guid TaskId,
    string TaskTitle,
    AgentSessionRole Role,
    AgentSessionStatus Status,
    string? Provider,
    DateTime StartedUtc,
    DateTime? CompletedUtc);

/// <summary>
/// A next step proposed by a finished session and not yet decided.
///
/// Only pending handoffs appear. That is a statement about consent that is currently outstanding,
/// not a history of decisions — and the absence of a row here says nothing about whether anything
/// was ever approved or declined.
/// </summary>
public sealed record SnapshotPendingHandoff(
    Guid HandoffId,
    Guid TaskId,
    string TaskTitle,
    AgentSessionRole ProposedRole,
    SessionHandoffKind Kind);

/// <summary>An active context entry, excerpted.</summary>
public sealed record SnapshotContextEntry(
    Guid ContextEntryId,
    Guid? TaskId,
    string? TaskTitle,
    ContextEntryKind Kind,
    string Title,
    string Excerpt,
    bool ExcerptTruncated,
    DateTime CreatedUtc);

/// <summary>How many tasks are in one display state. Counted across the whole project.</summary>
public sealed record SnapshotTaskStateCount(TaskDisplayState State, int Count);

/// <summary>
/// The project's shape at a glance. These counts cover every task in the project, not only the
/// <see cref="ProjectSnapshot.MaxTasks"/> that fitted — a truncated list must never make a project
/// look smaller than it is.
/// </summary>
public sealed record SnapshotHealth(
    int TotalTasks,
    IReadOnlyList<SnapshotTaskStateCount> TaskStateCounts,
    int NeedsAttentionCount,
    bool HasActiveWork)
{
    public int CountOf(TaskDisplayState state) =>
        TaskStateCounts.SingleOrDefault(count => count.State == state)?.Count ?? 0;
}

/// <summary>
/// One provider's readiness, as the Demiplane read it (ADR-0011: currently <c>Unknown</c> for every
/// provider). <see cref="ProviderCapacitySnapshot.Error"/> is deliberately not carried: it is the
/// only field on that record that can hold text this application did not author.
/// </summary>
public sealed record SnapshotProviderReadiness(
    string Provider,
    ProviderCapacityStatus Status,
    ProviderCapacityConfidence Confidence,
    string? Detail);

/// <summary>
/// What the server knows about execution capacity: counts and declared roles.
///
/// No <see cref="Worker.WorkerKey"/> and no <see cref="Worker.DisplayName"/>. Both are
/// administrator-chosen strings that in practice name machines and people, and neither answers a
/// question the Familiar can honestly ask.
/// </summary>
public sealed record SnapshotWorkforce(
    int EnabledWorkerCount,
    IReadOnlyList<AgentSessionRole> DeclaredRoles,
    int OnlineCount,
    int StaleCount,
    int OfflineCount);

/// <summary>
/// Everything a reasoning provider is ever shown about one project, and nothing else.
///
/// Three properties make this type worth having rather than passing entities around:
///
/// 1. <b>It is bounded.</b> Every collection has a published cap, so the size of a request cannot
///    grow with the size of a project. The caps are constants here so tests pin the contract rather
///    than a number that happened to be in the code.
/// 2. <b>It is a value.</b> No navigation property, no <c>DbContext</c>, no lazy load. What the
///    provider sees is what was measured.
/// 3. <b>It states its own gaps.</b> <see cref="Limitations"/> is the list of things this snapshot
///    knows it does not contain. Every bound that actually bit produces a line, so "say what you do
///    not know" has something specific to say.
/// </summary>
/// <param name="EstimatedCharacters">
/// The deterministic serialized-size estimate used for snapshot reduction, in characters of
/// <see cref="ProjectSnapshotSerialization.Options"/>'s output.
///
/// It is not the byte-for-byte length of the snapshot as finally written. It is measured with
/// <see cref="EstimatedCharacters"/>, <see cref="IsWithinBudget"/> and <see cref="ObservedAt"/> held
/// at deterministic placeholders — the first two because they would otherwise depend on their own
/// result, the third because a serialized instant varies in width with the clock and a budget that
/// moves with the clock drops a section on one page load and keeps it on the next. A fully populated
/// snapshot may therefore serialize a small, bounded number of characters longer than this, and
/// <see cref="IsWithinBudget"/> may differ by one character at the boundary.
///
/// The supported invariant: <see cref="EstimatedCharacters"/> is the deterministic serialized-size
/// estimate used for snapshot reduction. The final provider envelope must be serialized and checked
/// again immediately before transmission.
/// </param>
/// <param name="IsWithinBudget">
/// Whether <see cref="EstimatedCharacters"/> is within <see cref="MaxSnapshotCharacters"/>. Derived
/// from the estimate above and carrying the same bounded imprecision.
/// </param>
public sealed record ProjectSnapshot(
    Guid ProjectId,
    string ProjectName,
    string ProjectPurpose,
    bool ProjectPurposeTruncated,
    ProjectStatus ProjectStatus,
    int ContextRevision,
    IReadOnlyList<SnapshotTask> Tasks,
    IReadOnlyList<SnapshotSession> Sessions,
    IReadOnlyList<SnapshotPendingHandoff> PendingHandoffs,
    IReadOnlyList<SnapshotContextEntry> ContextEntries,
    SnapshotHealth Health,
    IReadOnlyList<SnapshotProviderReadiness> Providers,
    SnapshotWorkforce Workers,
    IReadOnlyList<string> Limitations,
    int EstimatedCharacters,
    bool IsWithinBudget,
    DateTimeOffset ObservedAt)
{
    /// <summary>Tasks kept, in the Demiplane's order: needs-attention, then state rank, then recency.</summary>
    public const int MaxTasks = 20;

    /// <summary>Most recent sessions across the whole project.</summary>
    public const int MaxSessions = 10;

    public const int MaxPendingHandoffs = 10;

    /// <summary>Most recent <see cref="ContextEntryState.Active"/> entries.</summary>
    public const int MaxContextEntries = 15;

    public const int MaxContextExcerptCharacters = 500;

    public const int MaxProjectPurposeCharacters = 1_000;

    /// <summary>
    /// The whole-snapshot budget, in characters of its serialized form as produced by
    /// <see cref="ProjectSnapshotSerialization"/>.
    ///
    /// Characters rather than tokens, because counting tokens means asking a provider, and this
    /// service must be buildable and testable with no provider configured at all.
    /// </summary>
    public const int MaxSnapshotCharacters = 24_000;

    /// <summary>
    /// The floor the reduction policy will not cut below. Below five tasks the snapshot has stopped
    /// describing the project, so the honest move is to refuse rather than to keep cutting.
    /// </summary>
    public const int MinimumTasksWhenOverBudget = 5;
}

/// <summary>Why a snapshot request ended the way it did.</summary>
public enum ProjectSnapshotOutcome
{
    /// <summary>A snapshot was built and is within budget.</summary>
    Available,

    /// <summary>No project with that id exists.</summary>
    ProjectNotFound,

    /// <summary>
    /// A snapshot was built but is still over budget after every documented reduction. It is
    /// carried anyway, because the page still renders the deterministic summary from it — what it
    /// must not do is send a project it has quietly cut down.
    /// </summary>
    TooLarge,

    /// <summary>
    /// The snapshot could not be read for an operational reason this application expects and
    /// classifies, currently only a busy or locked database.
    /// </summary>
    Unavailable
}

/// <summary>
/// The result of a snapshot request.
///
/// Expected operational failures are values here rather than exceptions, for the reason
/// <see cref="ProviderCapacitySnapshot.Faulted"/> exists: a page that cannot read a snapshot should
/// say so, not return a 500. <see cref="Detail"/> is always text this application authored — never
/// an exception message, a path or a connection string.
/// </summary>
public sealed record ProjectSnapshotResult(
    ProjectSnapshotOutcome Outcome,
    ProjectSnapshot? Snapshot,
    string? Detail)
{
    public static ProjectSnapshotResult Available(ProjectSnapshot snapshot) =>
        new(ProjectSnapshotOutcome.Available, snapshot, null);

    public static ProjectSnapshotResult TooLarge(ProjectSnapshot snapshot) =>
        new(
            ProjectSnapshotOutcome.TooLarge,
            snapshot,
            "This project is larger than this application can summarise for a reasoning provider safely.");

    public static ProjectSnapshotResult ProjectNotFound() =>
        new(ProjectSnapshotOutcome.ProjectNotFound, null, null);

    public static ProjectSnapshotResult Unavailable() =>
        new(
            ProjectSnapshotOutcome.Unavailable,
            null,
            "The database was busy, so this project could not be read just now.");
}
