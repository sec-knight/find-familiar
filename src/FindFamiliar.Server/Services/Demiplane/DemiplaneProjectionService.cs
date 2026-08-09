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

        // Every handoff, not only the pending one. Reading only Pending rows would make "no proposal
        // exists" and "a human declined the proposal" indistinguishable, and the projection would
        // have to guess which — inventing a decision for every task that predates Sprint 09, because
        // ADR-0010's migration deliberately backfills none.
        var handoffs = await dbContext.SessionHandoffs
            .AsNoTracking()
            .Where(handoff => taskIds.Contains(handoff.TaskId))
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
                handoffs.Where(handoff => handoff.TaskId == task.Id).ToList(),
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
        IReadOnlyList<SessionHandoff> handoffs,
        IReadOnlyList<ContextEntry> cancellationEntries,
        IReadOnlySet<AgentSessionRole> capableRoles,
        DateTimeOffset nowUtc)
    {
        var pendingHandoff = handoffs.SingleOrDefault(
            handoff => handoff.Status == SessionHandoffStatus.Pending);

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

        // The decision that applies is the one recorded against the latest terminal session, not any
        // decision ever made on this task. An older Approved handoff says nothing about what a human
        // chose after the session that just finished.
        var latestDecision = latestTerminal is null
            ? null
            : handoffs.SingleOrDefault(handoff => handoff.SourceSessionId == latestTerminal.Id);

        var (state, reasonCode, reasonText, needsAttention, detail) = DeriveState(
            task,
            ordered,
            started,
            latestTerminal,
            pendingHandoff,
            latestDecision,
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
            SessionHandoff? latestDecision,
            IReadOnlyList<ContextEntry> cancellationEntries,
            IReadOnlySet<AgentSessionRole> capableRoles,
            DateTimeOffset nowUtc)
    {
        // Corruption first: unreachable through the application since ADR-0010's unique index, and it
        // must not be shown as ordinary.
        //
        // The message states only what was observed. We have no persisted evidence of *why* this
        // happened: the projection reads AgentSessions alone, never the migration history or the
        // schema, so it cannot tell a pre-index database from a dropped index, an out-of-band write,
        // a restore over a live file, or a defect. Naming any one of those would be a guess presented
        // as a fact, on the one message that admits the page is confused.
        if (started.Count > 1)
        {
            return (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.MultipleStartedSessions,
                "Multiple sessions are recorded as started for this task. This state should not be reachable. Inspect the task and database directly.",
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
            var category = SessionOutcomeClassifier.ClassifyFailure(latestTerminal)
                ?? SessionOutcomeClassifier.ClassifyCancellation(reason);

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
                DescribeFailure(category.Value, latestTerminal.Role, latestTerminal),
                true,
                reason);
        }

        // The latest session completed and nothing is pending. What that means depends entirely on
        // whether a decision was actually recorded — absence of a proposal is not evidence of one.
        //
        // A recorded decision is checked before the Reviewer's generic wording. A Reviewer that
        // proposed a step which a human then declined is not the same situation as a Reviewer that
        // proposed nothing, and collapsing the two would discard a real human decision this page
        // exists to surface. The decision is still scoped to latestTerminal's SourceSessionId, so
        // nothing is inferred from absence and no earlier session's decision can reach here.
        return latestDecision?.Status switch
        {
            SessionHandoffStatus.Declined => (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.ProposedStepDeclined,
                $"The {latestTerminal.Role} session finished and you declined the proposed {latestDecision.ProposedRole} session. Nothing will happen without a new decision.",
                true,
                null),

            // Defensive: an Approved or Superseded handoff on the newest terminal session implies a
            // session that should have appeared above. Report that a decision exists without
            // characterising one we cannot see the effect of.
            SessionHandoffStatus.Approved or SessionHandoffStatus.Superseded => (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.ProposedStepAlreadyDecided,
                $"The {latestTerminal.Role} session finished and its proposed next step was already decided. Nothing is currently proposed.",
                true,
                null),

            // No applicable recorded decision. A finished Reviewer is the one role whose completion
            // is itself the thing a human acts on, so it keeps its own wording; every other role
            // gets the observable statement and nothing more.
            _ when latestTerminal.Role == AgentSessionRole.Reviewer => (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.AwaitingHumanDecisionAfterReview,
                "A Reviewer finished. Completing this task is your decision.",
                true,
                null),

            // No handoff row at all. Ordinary for work that predates Sprint 09, and the only honest
            // statement is what is observable: it finished, and nothing is proposed.
            _ => (
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.NoNextStepProposed,
                $"The {latestTerminal.Role} session finished and no next step is currently proposed.",
                true,
                null)
        };

    }

    private static string DescribeFailure(
        TaskDisplayReasonCode code,
        AgentSessionRole role,
        AgentSession session) => code switch
    {
        TaskDisplayReasonCode.AdapterPreflightFailed =>
            $"{role} could not start: {session.FailureCategory ?? "adapter preflight failure"}"
            + $"{(session.FailureAdapterExitCode is { } adapterCode ? $" (adapter exit {adapterCode})" : string.Empty)}. "
            + "Provider was not launched.",

        TaskDisplayReasonCode.ProviderRuntimeLaunchFailed =>
            $"The {role} session could not start: the provider runtime failed to launch.",

        TaskDisplayReasonCode.ProviderRunTimedOut =>
            $"The {role} session exceeded its time limit and was stopped.",

        TaskDisplayReasonCode.ProviderRequestFailed =>
            $"The provider runtime exited non-zero during the {role} session"
            + $"{(session.FailureProviderExitCode is { } providerCode ? $" (provider exit {providerCode})" : string.Empty)}.",

        TaskDisplayReasonCode.ProviderResponseUnusable =>
            $"The provider returned a response the {role} session could not use.",

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
