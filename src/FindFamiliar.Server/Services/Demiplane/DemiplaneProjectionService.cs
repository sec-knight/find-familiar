using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Providers;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services.Demiplane;

public interface IDemiplaneProjectionService
{
    Task<DemiplaneProjection?> GetProjectionAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Derives everything the Demiplane shows about one project.
///
/// All display-state rules live here rather than in Razor, so they are testable, stated once, and
/// cannot drift between the desktop map and the mobile trail — both consume this projection unchanged.
///
/// This answers a different question from <see cref="IWorkQueueService"/>, which is why the two
/// coexist: the work queue answers "what is the next action across all projects", the Demiplane
/// answers "what is true about this project and why". They read the same rows and must stay
/// consistent, which DemiplaneWorkQueueConsistencyTests asserts for the states they share.
///
/// Nothing here is execution authority. The projection is read-only, consults no claim, and is never
/// asked whether work may run.
/// </summary>
public sealed class DemiplaneProjectionService(
    FamiliarDbContext dbContext,
    IProviderCapacityService providerCapacity,
    TimeProvider timeProvider) : IDemiplaneProjectionService
{
    public async Task<DemiplaneProjection?> GetProjectionAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await dbContext.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

        if (project is null)
        {
            return null;
        }

        // Every query is scoped to this project. A task, session, handoff or context entry belonging
        // to another project can never reach this projection.
        var tasks = await dbContext.Tasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var taskIds = tasks.Select(task => task.Id).ToList();

        var sessions = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => taskIds.Contains(session.TaskId))
            .ToListAsync(cancellationToken);

        var handoffs = await dbContext.SessionHandoffs
            .AsNoTracking()
            .Where(handoff => taskIds.Contains(handoff.TaskId) && handoff.Status == SessionHandoffStatus.Pending)
            .ToListAsync(cancellationToken);

        // Only cancellation entries are needed, and only to read the durable reason a session ended.
        var cancellationEntries = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry =>
                entry.ProjectId == projectId
                && entry.Kind == ContextEntryKind.Handoff
                && entry.SourceSessionId != null)
            .ToListAsync(cancellationToken);

        var capableRoles = await CapableRolesAsync(projectId, cancellationToken);

        // The readiness strip is contextual: if it fails entirely the project still renders.
        var providers = await providerCapacity.GetAllAsync(cancellationToken);

        var nowUtc = timeProvider.GetUtcNow();

        var projected = tasks
            .Select(task => Project(
                task,
                sessions.Where(session => session.TaskId == task.Id).ToList(),
                handoffs.SingleOrDefault(handoff => handoff.TaskId == task.Id),
                cancellationEntries,
                capableRoles,
                nowUtc))
            .OrderByDescending(task => task.NeedsHumanAttention)
            .ThenBy(task => SortRank(task.DisplayState))
            .ThenByDescending(task => task.UpdatedUtc)
            .ThenBy(task => task.TaskId)
            .ToList();

        return new DemiplaneProjection(
            project.Id,
            project.Name,
            project.Purpose,
            project.Status,
            project.ContextRevision,
            projected,
            providers,
            nowUtc);
    }

    /// <summary>
    /// Roles at least one enabled worker declares. Used only to explain why an unclaimed session is
    /// waiting — it never gates anything, and a worker's capabilities remain self-reported (ADR-0008).
    /// </summary>
    private async Task<IReadOnlySet<AgentSessionRole>> CapableRolesAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        _ = projectId;

        var declared = await dbContext.Workers
            .AsNoTracking()
            .Where(worker => worker.Enabled)
            .Select(worker => worker.Capabilities)
            .ToListAsync(cancellationToken);

        return declared
            .SelectMany(WorkerCapabilities.Parse)
            .ToHashSet();
    }

    private static DemiplaneTask Project(
        FamiliarTask task,
        IReadOnlyList<AgentSession> sessions,
        SessionHandoff? pendingHandoff,
        IReadOnlyList<ContextEntry> cancellationEntries,
        IReadOnlySet<AgentSessionRole> capableRoles,
        DateTimeOffset nowUtc)
    {
        var ordered = sessions
            .OrderBy(session => session.StartedUtc)
            .ThenBy(session => session.Id)
            .ToList();

        var started = ordered.Where(session => session.Status == AgentSessionStatus.Started).ToList();
        var latestTerminal = ordered
            .Where(session => session.Status != AgentSessionStatus.Started)
            .OrderByDescending(session => session.CompletedUtc)
            .ThenByDescending(session => session.StartedUtc)
            .ThenByDescending(session => session.Id)
            .FirstOrDefault();

        var (state, reasonCode, reasonText, needsAttention, detail) = DeriveState(
            task,
            ordered,
            started,
            latestTerminal,
            pendingHandoff,
            cancellationEntries,
            capableRoles,
            nowUtc);

        var current = started.Count == 1 ? started[0] : null;

        var chain = BuildChain(ordered, pendingHandoff, current);

        var summary = FamiliarSummaryComposer.Compose(
            task,
            ordered,
            latestTerminal,
            current,
            pendingHandoff,
            state,
            reasonCode,
            reasonText,
            detail);

        return new DemiplaneTask(
            task.Id,
            task.Title,
            task.Status,
            state,
            reasonCode,
            reasonText,
            needsAttention,
            task.UpdatedUtc,
            chain,
            current?.Id,
            current?.Role,
            current?.Provider ?? latestTerminal?.Provider,
            pendingHandoff?.Id,
            pendingHandoff?.ConcurrencyToken,
            pendingHandoff?.ProposedRole,
            pendingHandoff?.Kind,
            summary);
    }

    /// <summary>
    /// The whole display-state rule, in one place and in priority order.
    /// </summary>
    private static (TaskDisplayState State, TaskDisplayReasonCode Code, string Text, bool NeedsAttention, string? Detail)
        DeriveState(
            FamiliarTask task,
            IReadOnlyList<AgentSession> ordered,
            IReadOnlyList<AgentSession> started,
            AgentSession? latestTerminal,
            SessionHandoff? pendingHandoff,
            IReadOnlyList<ContextEntry> cancellationEntries,
            IReadOnlySet<AgentSessionRole> capableRoles,
            DateTimeOffset nowUtc)
    {
        // Corruption first: unreachable through the application since ADR-0010's unique index, but a
        // database restored from before it can still hold this, and it must not be shown as ordinary.
        if (started.Count > 1)
        {
            return (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.MultipleStartedSessions,
                $"{started.Count} sessions are Started at once, which should be impossible. This database predates the uniqueness index.",
                true,
                null);
        }

        // A human's explicit decision about the task outranks anything derived from sessions.
        if (task.Status == TaskStatus.Completed)
        {
            return (
                TaskDisplayState.Succeeded,
                TaskDisplayReasonCode.MarkedCompleteByHuman,
                "You marked this task complete.",
                false,
                null);
        }

        if (task.Status == TaskStatus.Blocked)
        {
            return (
                TaskDisplayState.Blocked,
                TaskDisplayReasonCode.MarkedBlockedByHuman,
                "You marked this task blocked. The reason is not recorded in the task itself.",
                false,
                null);
        }

        if (started.Count == 1)
        {
            var session = started[0];

            if (session.ClaimedByWorkerId is not null
                && session.ClaimExpiresUtc is { } expiry
                && expiry <= nowUtc.UtcDateTime)
            {
                return (
                    TaskDisplayState.Waiting,
                    TaskDisplayReasonCode.LeaseExpired,
                    $"The {session.Role} session's worker lease expired without a result. It becomes claimable again automatically.",
                    false,
                    null);
            }

            if (session.ClaimedByWorkerId is not null)
            {
                return (
                    TaskDisplayState.Running,
                    TaskDisplayReasonCode.SessionRunning,
                    $"A {session.Role} session is running.",
                    false,
                    null);
            }

            // Unclaimed. Whether that is ordinary or a problem depends on whether any worker could
            // take it — this is the blocked-task condition ADR-0010 warned about.
            if (!capableRoles.Contains(session.Role))
            {
                return (
                    TaskDisplayState.Blocked,
                    TaskDisplayReasonCode.NoWorkerForRole,
                    $"Waiting for a worker that can run {session.Role}. No enabled worker declares that role, so this task cannot progress and nothing else can start on it.",
                    true,
                    null);
            }

            return (
                TaskDisplayState.Waiting,
                TaskDisplayReasonCode.AwaitingWorkerPickup,
                $"Waiting for an available {session.Role}.",
                false,
                null);
        }

        // Nothing is running. A proposed step is the most important thing a human can act on.
        if (pendingHandoff is not null)
        {
            var verb = pendingHandoff.Kind == SessionHandoffKind.RetrySameRole ? "retry the" : "start the";
            return (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.AwaitingHumanApproval,
                $"Waiting for your approval to {verb} {pendingHandoff.ProposedRole} session.",
                true,
                null);
        }

        if (ordered.Count == 0)
        {
            return (
                TaskDisplayState.NotStarted,
                TaskDisplayReasonCode.NeverStarted,
                "No session has run yet.",
                false,
                null);
        }

        if (latestTerminal is null)
        {
            return (
                TaskDisplayState.Waiting,
                TaskDisplayReasonCode.Unknown,
                "This task has sessions but none that finished, and none running.",
                false,
                null);
        }

        if (latestTerminal.Status == AgentSessionStatus.Cancelled)
        {
            var reason = SessionOutcomeClassifier.FindCancellationReason(cancellationEntries, latestTerminal.Id);
            var category = SessionOutcomeClassifier.ClassifyCancellation(reason);

            if (category is null)
            {
                // A human stopped it, in their own words. Shown verbatim, never interpreted.
                return (
                    TaskDisplayState.Cancelled,
                    TaskDisplayReasonCode.CancelledByHuman,
                    $"The {latestTerminal.Role} session was cancelled.",
                    false,
                    reason);
            }

            return (
                TaskDisplayState.Failed,
                category.Value,
                DescribeFailure(category.Value, latestTerminal.Role),
                true,
                reason);
        }

        // The latest session completed and proposed nothing.
        if (latestTerminal.Role == AgentSessionRole.Reviewer)
        {
            return (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.AwaitingHumanDecisionAfterReview,
                "A Reviewer finished. Completing this task is your decision.",
                true,
                null);
        }

        return (
            TaskDisplayState.NeedsAttention,
            TaskDisplayReasonCode.ProposedStepDeclined,
            $"The {latestTerminal.Role} session finished and the proposed next step was declined. Nothing will happen without a new decision.",
            true,
            null);
    }

    private static string DescribeFailure(TaskDisplayReasonCode code, AgentSessionRole role) => code switch
    {
        TaskDisplayReasonCode.ProviderRuntimeLaunchFailed =>
            $"The {role} session could not start: the provider runtime failed to launch.",

        TaskDisplayReasonCode.ProviderRunTimedOut =>
            $"The {role} session exceeded its time limit and was stopped.",

        TaskDisplayReasonCode.ProviderRequestFailed =>
            $"The provider request failed during the {role} session. A usage limit would also appear here — the adapter cannot yet tell exhaustion apart from other provider errors.",

        TaskDisplayReasonCode.ProviderResponseUnusable =>
            $"The provider returned a response the {role} session could not use.",

        TaskDisplayReasonCode.WaitingForProviderCapacity =>
            "The provider had no capacity left. This is a scheduling condition, not a failed implementation.",

        _ => $"The {role} session ended with a failure this version does not recognise."
    };

    /// <summary>
    /// The task's real internal structure: the sessions that ran, plus the proposed step as an
    /// explicitly un-started node. No edge is drawn between tasks, because the domain records none.
    /// </summary>
    private static IReadOnlyList<TaskChainStep> BuildChain(
        IReadOnlyList<AgentSession> ordered,
        SessionHandoff? pendingHandoff,
        AgentSession? current)
    {
        var chain = ordered
            .Select(session => new TaskChainStep(
                session.Id,
                session.Role,
                session.Status,
                IsProposed: false,
                IsCurrent: current is not null && session.Id == current.Id,
                session.StartedUtc,
                session.CompletedUtc))
            .ToList();

        if (pendingHandoff is not null)
        {
            chain.Add(new TaskChainStep(
                SessionId: null,
                pendingHandoff.ProposedRole,
                Status: null,
                IsProposed: true,
                IsCurrent: false,
                StartedUtc: null,
                CompletedUtc: null));
        }

        return chain;
    }

    /// <summary>Most actionable first, then in-flight, then settled.</summary>
    private static int SortRank(TaskDisplayState state) => state switch
    {
        TaskDisplayState.NeedsAttention => 0,
        TaskDisplayState.Failed => 1,
        TaskDisplayState.Blocked => 2,
        TaskDisplayState.Running => 3,
        TaskDisplayState.Waiting => 4,
        TaskDisplayState.NotStarted => 5,
        TaskDisplayState.Cancelled => 6,
        TaskDisplayState.Succeeded => 7,
        _ => 8
    };
}
