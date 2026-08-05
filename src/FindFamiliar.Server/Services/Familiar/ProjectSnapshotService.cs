using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Providers;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// Assembles a <see cref="ProjectSnapshot"/> for one project.
///
/// It is built <b>on top of</b> <see cref="IDemiplaneProjectionService"/> rather than beside it. The
/// Demiplane already owns every rule about what a task's state is and why (ADR-0011); deriving a
/// second answer here would give the page and the Familiar two different accounts of the same row,
/// and only one of them could be right. So task state, its reason, its attention flag and its
/// recommended next action are copied, never recomputed.
///
/// What this service adds is what the Demiplane has no reason to carry: session history, active
/// context, the shape of the workforce, and — the part that matters most — bounds. A projection may
/// be as large as a project is. A snapshot may not, because it is the thing that gets sent somewhere.
///
/// Nothing here writes, and nothing here calls a provider. Both are load-bearing: this runs on a
/// <c>GET</c>, and it must be callable on a machine with no credentials at all.
/// </summary>
public sealed class ProjectSnapshotService(
    FamiliarDbContext dbContext,
    IDemiplaneProjectionService demiplaneProjection,
    TimeProvider timeProvider) : IProjectSnapshotService
{
    public async Task<ProjectSnapshotResult> GetSnapshotAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await BuildAsync(projectId, cancellationToken);
        }
        catch (Exception exception) when (SessionHandoffApprovalService.IsDatabaseBusy(exception))
        {
            // A contended SQLite file is an ordinary operational condition on this database — the
            // runner, the capture path and the claim scan all write to it. Reported as a typed
            // outcome so a page can say "not right now" instead of returning a 500.
            return ProjectSnapshotResult.Unavailable();
        }
    }

    private async Task<ProjectSnapshotResult> BuildAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var projection = await demiplaneProjection.GetProjectionAsync(projectId, cancellationToken);

        if (projection is null)
        {
            return ProjectSnapshotResult.ProjectNotFound();
        }

        // Every query below filters on this project. None of them takes the project id from a task
        // list or any other derived collection: the filter is the projectId that was asked for, so a
        // row belonging to another project has no path into this snapshot.
        var sessionCount = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.Task.ProjectId == projectId)
            .CountAsync(cancellationToken);

        var sessions = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.Task.ProjectId == projectId)
            .OrderByDescending(session => session.StartedUtc)
            .ThenByDescending(session => session.Id)
            .Take(ProjectSnapshot.MaxSessions)
            .Select(session => new SnapshotSession(
                session.Id,
                session.TaskId,
                session.Task.Title,
                session.Role,
                session.Status,
                session.Provider,
                session.StartedUtc,
                session.CompletedUtc))
            .ToListAsync(cancellationToken);

        var activeEntryCount = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == projectId && entry.State == ContextEntryState.Active)
            .CountAsync(cancellationToken);

        var supersededEntryCount = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == projectId && entry.State == ContextEntryState.Superseded)
            .CountAsync(cancellationToken);

        // Excerpted in the query rather than after it. A context entry may hold twelve thousand
        // characters, and reading fifteen of them in full to keep five hundred of each is work this
        // page does on every load.
        var entries = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == projectId && entry.State == ContextEntryState.Active)
            .OrderByDescending(entry => entry.CreatedUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(ProjectSnapshot.MaxContextEntries)
            .Select(entry => new SnapshotContextEntry(
                entry.Id,
                entry.TaskId,
                // Project-level entries have no task. Written as an explicit null test so the query
                // keeps them: an unguarded navigation read here would silently become an inner join.
                entry.Task == null ? null : entry.Task.Title,
                entry.Kind,
                entry.Title,
                entry.Content.Substring(0, ProjectSnapshot.MaxContextExcerptCharacters),
                entry.Content.Length > ProjectSnapshot.MaxContextExcerptCharacters,
                entry.CreatedUtc))
            .ToListAsync(cancellationToken);

        var workforce = await ReadWorkforceAsync(cancellationToken);

        var facts = new SnapshotFacts(
            projection,
            sessionCount,
            activeEntryCount,
            supersededEntryCount,
            workforce,
            projection.Tasks.Count(task => task.PendingHandoffId is not null));

        var tasks = projection.Tasks
            .Take(ProjectSnapshot.MaxTasks)
            .Select(task => new SnapshotTask(
                task.TaskId,
                task.Title,
                task.DisplayState,
                task.ReasonCode,
                task.ReasonText,
                task.NeedsHumanAttention,
                task.CurrentRole,
                task.Provider,
                task.PendingHandoffId is not null,
                task.Summary.RecommendedNextAction))
            .ToList();

        return Reduce(facts, tasks, sessions, entries);
    }

    /// <summary>
    /// The documented reduction policy: context entries, then sessions, then tasks beyond the floor,
    /// each step re-measured so nothing is dropped that the budget did not require.
    ///
    /// Past the floor there is nothing honest left to remove, so the snapshot is returned over
    /// budget and marked as such. Silently sending half a project would produce answers that are
    /// confident and wrong about the half that was cut.
    /// </summary>
    private ProjectSnapshotResult Reduce(
        SnapshotFacts facts,
        IReadOnlyList<SnapshotTask> tasks,
        IReadOnlyList<SnapshotSession> sessions,
        IReadOnlyList<SnapshotContextEntry> entries)
    {
        var omissions = new List<string>();

        var snapshot = Compose(facts, tasks, sessions, entries, omissions, isWithinBudget: true);

        if (snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters)
        {
            return ProjectSnapshotResult.Available(snapshot);
        }

        if (entries.Count > 0)
        {
            // The count is of the entries this snapshot had, not of the project's active entries. Any
            // beyond MaxContextEntries were never here to be omitted, and their absence is already
            // stated by the cap's own limitation.
            var omittedEntries = entries.Count;
            entries = [];
            omissions.Add(
                $"All {omittedEntries} context entries included before reduction were omitted: the snapshot exceeded its {ProjectSnapshot.MaxSnapshotCharacters:N0}-character budget.");
            snapshot = Compose(facts, tasks, sessions, entries, omissions, isWithinBudget: true);

            if (snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters)
            {
                return ProjectSnapshotResult.Available(snapshot);
            }
        }

        if (sessions.Count > 0)
        {
            // As above: the sessions this snapshot held, not every session the project has ever run.
            var omittedSessions = sessions.Count;
            sessions = [];
            omissions.Add(
                $"All {omittedSessions} recent sessions included before reduction were omitted: the snapshot exceeded its {ProjectSnapshot.MaxSnapshotCharacters:N0}-character budget.");
            snapshot = Compose(facts, tasks, sessions, entries, omissions, isWithinBudget: true);

            if (snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters)
            {
                return ProjectSnapshotResult.Available(snapshot);
            }
        }

        if (tasks.Count > ProjectSnapshot.MinimumTasksWhenOverBudget)
        {
            tasks = tasks.Take(ProjectSnapshot.MinimumTasksWhenOverBudget).ToList();
            omissions.Add(
                $"Only the first {ProjectSnapshot.MinimumTasksWhenOverBudget} tasks were kept: the snapshot exceeded its {ProjectSnapshot.MaxSnapshotCharacters:N0}-character budget.");
            snapshot = Compose(facts, tasks, sessions, entries, omissions, isWithinBudget: true);

            if (snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters)
            {
                return ProjectSnapshotResult.Available(snapshot);
            }
        }

        omissions.Add(
            $"This project is still larger than the {ProjectSnapshot.MaxSnapshotCharacters:N0}-character budget after every reduction, so it is not sent to a reasoning provider.");

        return ProjectSnapshotResult.TooLarge(
            Compose(facts, tasks, sessions, entries, omissions, isWithinBudget: false));
    }

    private ProjectSnapshot Compose(
        SnapshotFacts facts,
        IReadOnlyList<SnapshotTask> tasks,
        IReadOnlyList<SnapshotSession> sessions,
        IReadOnlyList<SnapshotContextEntry> entries,
        IReadOnlyList<string> omissions,
        bool isWithinBudget)
    {
        var projection = facts.Projection;

        var purpose = projection.ProjectPurpose.Length > ProjectSnapshot.MaxProjectPurposeCharacters
            ? projection.ProjectPurpose[..ProjectSnapshot.MaxProjectPurposeCharacters]
            : projection.ProjectPurpose;

        var handoffs = projection.Tasks
            .Where(task => task.PendingHandoffId is not null)
            .Take(ProjectSnapshot.MaxPendingHandoffs)
            .Select(task => new SnapshotPendingHandoff(
                task.PendingHandoffId!.Value,
                task.TaskId,
                task.Title,
                task.ProposedRole!.Value,
                task.ProposedKind!.Value))
            .ToList();

        var health = new SnapshotHealth(
            projection.Tasks.Count,
            Enum.GetValues<TaskDisplayState>()
                .Select(state => new SnapshotTaskStateCount(state, projection.CountOf(state)))
                .Where(count => count.Count > 0)
                .ToList(),
            projection.NeedsAttention.Count,
            projection.HasActiveWork);

        var providers = projection.Providers
            .Select(provider => new SnapshotProviderReadiness(
                provider.Provider,
                provider.Status,
                provider.Confidence,
                provider.Detail))
            .ToList();

        var snapshot = new ProjectSnapshot(
            projection.ProjectId,
            projection.ProjectName,
            purpose,
            purpose.Length < projection.ProjectPurpose.Length,
            projection.ProjectStatus,
            projection.ContextRevision,
            tasks,
            sessions,
            handoffs,
            entries,
            health,
            providers,
            facts.Workforce,
            DescribeLimitations(facts, tasks, sessions, handoffs, entries, purpose, omissions),
            EstimatedCharacters: 0,
            isWithinBudget,
            timeProvider.GetUtcNow());

        return snapshot with { EstimatedCharacters = ProjectSnapshotSerialization.Measure(snapshot) };
    }

    /// <summary>
    /// Every bound that actually bit, stated plainly, plus the standing gaps in what this server can
    /// know. Composed from the content that survived rather than from the intent to truncate, so a
    /// category that was dropped for size cannot leave behind a line claiming it is still here.
    ///
    /// A truncation with no line is a defect: it is the difference between a Familiar that says
    /// "there are 27 more tasks I cannot see" and one that quietly answers as though 20 were all of
    /// them.
    /// </summary>
    private static IReadOnlyList<string> DescribeLimitations(
        SnapshotFacts facts,
        IReadOnlyList<SnapshotTask> tasks,
        IReadOnlyList<SnapshotSession> sessions,
        IReadOnlyList<SnapshotPendingHandoff> handoffs,
        IReadOnlyList<SnapshotContextEntry> entries,
        string purpose,
        IReadOnlyList<string> omissions)
    {
        var limitations = new List<string>();

        if (tasks.Count > 0 && facts.Projection.Tasks.Count > tasks.Count)
        {
            limitations.Add(
                $"Showing {tasks.Count} of {facts.Projection.Tasks.Count} tasks, ordered by attention then recency.");
        }

        if (sessions.Count > 0 && facts.SessionCount > sessions.Count)
        {
            limitations.Add(
                $"Showing the {sessions.Count} most recent sessions of {facts.SessionCount}; older sessions are not included.");
        }

        if (handoffs.Count > 0 && facts.PendingHandoffCount > handoffs.Count)
        {
            limitations.Add(
                $"Showing {handoffs.Count} of {facts.PendingHandoffCount} proposed next steps awaiting your decision.");
        }

        if (entries.Count > 0 && facts.ActiveContextEntryCount > entries.Count)
        {
            limitations.Add(
                $"Showing the {entries.Count} most recent active context entries of {facts.ActiveContextEntryCount}; older entries are not included.");
        }

        var excerpted = entries.Count(entry => entry.ExcerptTruncated);
        if (excerpted > 0)
        {
            limitations.Add(
                $"{excerpted} of the context entries shown are cut to their first {ProjectSnapshot.MaxContextExcerptCharacters:N0} characters.");
        }

        if (facts.SupersededContextEntryCount > 0)
        {
            limitations.Add(
                $"Superseded context entries are not included; {facts.SupersededContextEntryCount} of this project's entries have been superseded.");
        }

        if (purpose.Length < facts.Projection.ProjectPurpose.Length)
        {
            limitations.Add(
                $"The project's purpose is shown as its first {ProjectSnapshot.MaxProjectPurposeCharacters:N0} characters.");
        }

        limitations.AddRange(omissions);

        // Standing gaps. These are not truncations — they are things this server cannot determine at
        // all, and they are stated on every snapshot so an answer built on them can repeat them.
        if (facts.Projection.Providers.Count == 0)
        {
            limitations.Add("No provider readiness is reported, so nothing is known about provider capacity.");
        }
        else if (facts.Projection.Providers.Any(provider => provider.Status == ProviderCapacityStatus.Unknown))
        {
            limitations.Add("Provider remaining capacity is unknown; this application cannot read it.");
        }

        limitations.Add(
            "Worker capabilities are self-reported and are not verified, and workers are recorded here only as counts and declared roles.");

        return limitations;
    }

    private async Task<SnapshotWorkforce> ReadWorkforceAsync(CancellationToken cancellationToken)
    {
        // Workers are registered against the server, not against a project (ADR-0008), so this is
        // the one read here with no project filter — and the only thing taken from it is counts and
        // declared roles. Keys and display names name machines and are never carried.
        var workers = await dbContext.Workers
            .AsNoTracking()
            .Where(worker => worker.Enabled)
            .Select(worker => new { worker.Capabilities, worker.LastHeartbeatUtc })
            .ToListAsync(cancellationToken);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var availability = workers
            .Select(worker => WorkerCoordinationService.DeriveAvailability(worker.LastHeartbeatUtc, nowUtc))
            .ToList();

        var roles = workers
            .SelectMany(worker => WorkerCapabilities.Parse(worker.Capabilities))
            .Distinct()
            .OrderBy(role => (int)role)
            .ToList();

        return new SnapshotWorkforce(
            workers.Count,
            roles,
            availability.Count(state => state == WorkerAvailability.Online),
            availability.Count(state => state == WorkerAvailability.Stale),
            availability.Count(state => state == WorkerAvailability.Offline));
    }

    /// <summary>
    /// The project-wide totals a snapshot needs in order to say what it left out, kept beside the
    /// projection so a reduction can be re-described without querying again.
    /// </summary>
    private sealed record SnapshotFacts(
        DemiplaneProjection Projection,
        int SessionCount,
        int ActiveContextEntryCount,
        int SupersededContextEntryCount,
        SnapshotWorkforce Workforce,
        int PendingHandoffCount);
}
