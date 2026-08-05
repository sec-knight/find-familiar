using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The Sprint 09 approval transaction (ADR-0010).
///
/// Every race here runs on a real file-backed SQLite database with independent contexts, for the
/// reason ADR-0008 and ADR-0009 both give: an in-memory provider would prove nothing about the
/// serialization this design depends on.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class SessionHandoffApprovalServiceTests
{
    [Fact]
    public async Task Approval_starts_one_session_of_the_proposed_role_and_creates_no_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);
        var revisionBefore = await CurrentRevisionAsync(dbContext, task.ProjectId);
        var tasksBefore = await dbContext.Tasks.CountAsync();

        var outcome = await NewService(dbContext).ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));

        Assert.Equal(SessionHandoffDecisionStatus.Approved, outcome.Status);
        Assert.Equal(AgentSessionRole.Implementer, outcome.Role);

        var session = await dbContext.AgentSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == outcome.SessionId);
        Assert.Equal(AgentSessionRole.Implementer, session.Role);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Equal(task.Id, session.TaskId);

        // Session start only. No task was created, so the +2 of a conversational approval would be wrong.
        var revisionAfter = await CurrentRevisionAsync(dbContext, task.ProjectId);
        Assert.Equal(revisionBefore + 1, revisionAfter);
        Assert.Equal(revisionAfter, session.ContextRevisionRead);
        Assert.Equal(tasksBefore, await dbContext.Tasks.CountAsync());

        var stored = await ReadHandoffAsync(dbContext, handoff.Id);
        Assert.Equal(SessionHandoffStatus.Approved, stored.Status);
        Assert.Equal(session.Id, stored.CreatedSessionId);
        Assert.NotNull(stored.DecidedUtc);
        Assert.NotEqual(handoff.ConcurrencyToken, stored.ConcurrencyToken);
    }

    [Fact]
    public async Task Approval_does_not_touch_task_UpdatedUtc()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);
        var updatedBefore = await dbContext.Tasks
            .AsNoTracking()
            .Where(candidate => candidate.Id == task.Id)
            .Select(candidate => candidate.UpdatedUtc)
            .SingleAsync();

        await NewService(dbContext).ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));

        // Parity with the manual start path, which has never touched it (ADR-0005).
        var updatedAfter = await dbContext.Tasks
            .AsNoTracking()
            .Where(candidate => candidate.Id == task.Id)
            .Select(candidate => candidate.UpdatedUtc)
            .SingleAsync();
        Assert.Equal(updatedBefore, updatedAfter);
    }

    [Fact]
    public async Task Replayed_approval_returns_the_original_session_and_creates_no_second_one()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);

        var first = await NewService(dbContext).ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));
        var revisionAfterFirst = await CurrentRevisionAsync(dbContext, task.ProjectId);

        // The same rendered button, submitted twice.
        var second = await NewService(dbContext).ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));

        Assert.Equal(SessionHandoffDecisionStatus.AlreadyApproved, second.Status);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(1, await dbContext.AgentSessions.CountAsync(s => s.TaskId == task.Id && s.Role == AgentSessionRole.Implementer));
        Assert.Equal(revisionAfterFirst, await CurrentRevisionAsync(dbContext, task.ProjectId));
    }

    /// <summary>
    /// The load-bearing concurrency proof. Eight contenders released together must produce exactly
    /// one session — chosen by the database, not by a preflight read.
    /// </summary>
    [Fact]
    public async Task Eight_concurrent_approvals_create_exactly_one_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seedContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(seedContext);
        var revisionBefore = await CurrentRevisionAsync(seedContext, task.ProjectId);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = new List<Task<SessionHandoffDecisionOutcome>>(8);
        var contexts = new List<FamiliarDbContext>(8);

        for (var i = 0; i < 8; i++)
        {
            var context = await database.CreateContextAsync();
            contexts.Add(context);
            contenders.Add(Task.Run(async () =>
            {
                await barrier.Task;
                return await NewService(context).ApproveAsync(
                    new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));
            }));
        }

        barrier.SetResult();
        var outcomes = await Task.WhenAll(contenders);

        Assert.Single(outcomes, outcome => outcome.Status == SessionHandoffDecisionStatus.Approved);
        Assert.All(outcomes, outcome => Assert.Contains(
            outcome.Status,
            new[] { SessionHandoffDecisionStatus.Approved, SessionHandoffDecisionStatus.AlreadyApproved }));

        var sessions = await seedContext.AgentSessions
            .AsNoTracking()
            .Where(candidate => candidate.TaskId == task.Id && candidate.Role == AgentSessionRole.Implementer)
            .ToListAsync();
        Assert.Single(sessions);

        // Every loser must report the winner's session, so a double-submit is inert rather than wrong.
        Assert.All(
            outcomes.Where(outcome => outcome.Status == SessionHandoffDecisionStatus.AlreadyApproved),
            outcome => Assert.Equal(sessions[0].Id, outcome.SessionId));

        Assert.Equal(revisionBefore + 1, await CurrentRevisionAsync(seedContext, task.ProjectId));
    }

    [Fact]
    public async Task Approval_racing_decline_produces_exactly_one_terminal_winner()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seedContext = await database.CreateContextAsync();
        await using var approveContext = await database.CreateContextAsync();
        await using var declineContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(seedContext);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken);

        var approve = Task.Run(async () =>
        {
            await barrier.Task;
            return await NewService(approveContext).ApproveAsync(request);
        });
        var decline = Task.Run(async () =>
        {
            await barrier.Task;
            return await NewService(declineContext).DeclineAsync(request);
        });

        barrier.SetResult();
        var results = await Task.WhenAll(approve, decline);

        var stored = await ReadHandoffAsync(seedContext, handoff.Id);
        Assert.Contains(stored.Status, new[] { SessionHandoffStatus.Approved, SessionHandoffStatus.Declined });

        var sessionCount = await seedContext.AgentSessions
            .CountAsync(candidate => candidate.TaskId == task.Id && candidate.Role == AgentSessionRole.Implementer);

        if (stored.Status == SessionHandoffStatus.Approved)
        {
            Assert.Equal(1, sessionCount);
            Assert.NotNull(stored.CreatedSessionId);
        }
        else
        {
            Assert.Equal(0, sessionCount);
            Assert.Null(stored.CreatedSessionId);
        }

        // Whichever lost must say so, never silently succeed.
        Assert.Single(results, result =>
            result.Status is SessionHandoffDecisionStatus.Approved or SessionHandoffDecisionStatus.Declined);
    }

    /// <summary>
    /// Handoff approval and the manual start form both create a session on one task. Only the index
    /// stands between them: the manual path has no proposal row to consume conditionally.
    /// </summary>
    [Fact]
    public async Task Approval_racing_the_manual_start_path_creates_exactly_one_started_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seedContext = await database.CreateContextAsync();
        await using var approveContext = await database.CreateContextAsync();
        await using var manualContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(seedContext);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var approve = Task.Run(async () =>
        {
            await barrier.Task;
            return await NewService(approveContext).ApproveAsync(
                new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));
        });

        var manual = Task.Run(async () =>
        {
            await barrier.Task;
            return await new WorkflowDispatchService(manualContext).StartSessionForTaskAsync(
                task.Id,
                AgentSessionRole.Reviewer,
                provider: null,
                externalSessionReference: null,
                startedUtc: DateTime.UtcNow);
        });

        barrier.SetResult();
        var approval = await approve;
        var manualOutcome = await manual;

        var started = await seedContext.AgentSessions
            .AsNoTracking()
            .Where(candidate => candidate.TaskId == task.Id && candidate.Status == AgentSessionStatus.Started)
            .ToListAsync();
        Assert.Single(started);

        // Exactly one succeeded, and the loser returned a typed outcome rather than throwing.
        var approvalWon = approval.Status == SessionHandoffDecisionStatus.Approved;
        var manualWon = manualOutcome.Status == StartSessionStatus.Started;
        Assert.True(approvalWon ^ manualWon, "exactly one path must create the session");

        if (!approvalWon)
        {
            Assert.Equal(SessionHandoffDecisionStatus.SessionAlreadyStarted, approval.Status);
        }

        if (!manualWon)
        {
            Assert.Equal(StartSessionStatus.AlreadyStarted, manualOutcome.Status);
        }
    }

    [Fact]
    public async Task A_stale_token_is_rejected_without_starting_anything()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);
        var revisionBefore = await CurrentRevisionAsync(dbContext, task.ProjectId);

        var outcome = await NewService(dbContext).ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, Guid.NewGuid()));

        Assert.Equal(SessionHandoffDecisionStatus.StaleHandoff, outcome.Status);
        Assert.Equal(SessionHandoffStatus.Pending, (await ReadHandoffAsync(dbContext, handoff.Id)).Status);
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync(s => s.TaskId == task.Id && s.Role == AgentSessionRole.Implementer));
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(dbContext, task.ProjectId));
    }

    [Fact]
    public async Task A_declined_handoff_creates_nothing_and_cannot_be_approved_afterwards()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);
        var revisionBefore = await CurrentRevisionAsync(dbContext, task.ProjectId);

        var declined = await NewService(dbContext).DeclineAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));
        Assert.Equal(SessionHandoffDecisionStatus.Declined, declined.Status);

        var stored = await ReadHandoffAsync(dbContext, handoff.Id);
        Assert.Equal(SessionHandoffStatus.Declined, stored.Status);
        Assert.Null(stored.CreatedSessionId);
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync(s => s.TaskId == task.Id && s.Role == AgentSessionRole.Implementer));
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(dbContext, task.ProjectId));

        // Terminal is terminal: neither the old token nor the new one revives it.
        Assert.Equal(
            SessionHandoffDecisionStatus.AlreadyDeclined,
            (await NewService(dbContext).ApproveAsync(
                new SessionHandoffDecisionRequest(handoff.Id, stored.ConcurrencyToken))).Status);
    }

    [Fact]
    public async Task Approval_is_refused_while_the_task_already_owns_a_started_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);

        dbContext.AgentSessions.Add(NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();

        var revisionBefore = await CurrentRevisionAsync(dbContext, task.ProjectId);

        var outcome = await NewService(dbContext).ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));

        Assert.Equal(SessionHandoffDecisionStatus.SessionAlreadyStarted, outcome.Status);

        // The handoff stays Pending with its original token, so the rendered button still works later.
        var stored = await ReadHandoffAsync(dbContext, handoff.Id);
        Assert.Equal(SessionHandoffStatus.Pending, stored.Status);
        Assert.Equal(handoff.ConcurrencyToken, stored.ConcurrencyToken);
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(dbContext, task.ProjectId));
    }

    [Fact]
    public async Task Approval_is_refused_on_a_completed_task_and_an_archived_project()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, completedTask, completedHandoff) = await SeedPendingHandoffAsync(dbContext);
        await dbContext.Tasks
            .Where(candidate => candidate.Id == completedTask.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, TaskStatus.Completed));

        Assert.Equal(
            SessionHandoffDecisionStatus.TaskClosed,
            (await NewService(dbContext).ApproveAsync(
                new SessionHandoffDecisionRequest(completedHandoff.Id, completedHandoff.ConcurrencyToken))).Status);
        Assert.Equal(SessionHandoffStatus.Pending, (await ReadHandoffAsync(dbContext, completedHandoff.Id)).Status);

        var (archivedProject, _, archivedHandoff) = await SeedPendingHandoffAsync(dbContext);
        await dbContext.Projects
            .Where(candidate => candidate.Id == archivedProject.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, ProjectStatus.Archived));

        Assert.Equal(
            SessionHandoffDecisionStatus.ProjectInactive,
            (await NewService(dbContext).ApproveAsync(
                new SessionHandoffDecisionRequest(archivedHandoff.Id, archivedHandoff.ConcurrencyToken))).Status);
        Assert.Equal(SessionHandoffStatus.Pending, (await ReadHandoffAsync(dbContext, archivedHandoff.Id)).Status);

        Assert.Equal(0, await dbContext.AgentSessions.CountAsync(s => s.Role == AgentSessionRole.Implementer));
    }

    /// <summary>
    /// A failure after the fence consume but before commit must leave no trace: the handoff stays
    /// Pending, no session exists, and the revision has not drifted.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_fence_consume_rolls_everything_back()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task, handoff) = await SeedPendingHandoffAsync(dbContext);
        var revisionBefore = await CurrentRevisionAsync(dbContext, task.ProjectId);

        var service = new SessionHandoffApprovalService(
            dbContext,
            new FailingWorkflowDispatchService(new WorkflowDispatchService(dbContext)),
            TimeProvider.System);

        var outcome = await service.ApproveAsync(
            new SessionHandoffDecisionRequest(handoff.Id, handoff.ConcurrencyToken));

        Assert.Equal(SessionHandoffDecisionStatus.Conflict, outcome.Status);

        await using var verifyContext = await database.CreateContextAsync();
        var stored = await ReadHandoffAsync(verifyContext, handoff.Id);
        Assert.Equal(SessionHandoffStatus.Pending, stored.Status);
        Assert.Equal(handoff.ConcurrencyToken, stored.ConcurrencyToken);
        Assert.Null(stored.CreatedSessionId);
        Assert.Null(stored.DecidedUtc);

        Assert.Equal(0, await verifyContext.AgentSessions.CountAsync(s => s.TaskId == task.Id && s.Role == AgentSessionRole.Implementer));
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(verifyContext, task.ProjectId));
    }

    /// <summary>
    /// Stages a genuinely unsavable session — its TaskId references a task that does not exist — so
    /// the transaction fails on a real foreign-key violation rather than an exception the service
    /// could have special-cased.
    /// </summary>
    private sealed class FailingWorkflowDispatchService(IWorkflowDispatchService inner) : IWorkflowDispatchService
    {
        public Task<bool> HasStartedSessionAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            inner.HasStartedSessionAsync(taskId, cancellationToken);

        public Task<StartSessionOutcome> StartSessionForTaskAsync(
            Guid taskId,
            AgentSessionRole role,
            string? provider,
            string? externalSessionReference,
            DateTime startedUtc,
            CancellationToken cancellationToken = default) =>
            inner.StartSessionForTaskAsync(
                taskId, role, provider, externalSessionReference, startedUtc, cancellationToken);

        public FamiliarTask CreateReadyTask(
            FamiliarProject project,
            string title,
            string requestedOutcome,
            DateTime nowUtc) =>
            inner.CreateReadyTask(project, title, requestedOutcome, nowUtc);

        public AgentSession StartSession(
            FamiliarTask task,
            FamiliarProject project,
            AgentSessionRole role,
            string? provider,
            string? externalSessionReference,
            DateTime startedUtc)
        {
            var session = inner.StartSession(task, project, role, provider, externalSessionReference, startedUtc);
            session.TaskId = Guid.NewGuid();
            return session;
        }
    }

    private static SessionHandoffApprovalService NewService(FamiliarDbContext dbContext) =>
        new(dbContext, new WorkflowDispatchService(dbContext), TimeProvider.System);

    private static Task<SessionHandoff> ReadHandoffAsync(FamiliarDbContext dbContext, Guid handoffId)
    {
        dbContext.ChangeTracker.Clear();
        return dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoffId);
    }

    private static Task<int> CurrentRevisionAsync(FamiliarDbContext dbContext, Guid projectId)
    {
        dbContext.ChangeTracker.Clear();
        return dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.ContextRevision)
            .SingleAsync();
    }

    /// <summary>A completed Planner session and the Implementer handoff it proposed.</summary>
    internal static async Task<(FamiliarProject Project, FamiliarTask Task, SessionHandoff Handoff)> SeedPendingHandoffAsync(
        FamiliarDbContext dbContext,
        AgentSessionRole sourceRole = AgentSessionRole.Planner,
        AgentSessionStatus sourceOutcome = AgentSessionStatus.Completed)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Handoff project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionHandoffApprovalServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Handoff task",
            RequestedOutcome = "Seeded for SessionHandoffApprovalServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var sourceSession = NewSession(task.Id, sourceRole, sourceOutcome);

        var proposal = SessionHandoffService.Propose(sourceRole, sourceOutcome)!.Value;

        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = sourceSession.Id,
            SourceOutcome = sourceOutcome,
            ProposedRole = proposal.Role,
            Kind = proposal.Kind,
            Status = SessionHandoffStatus.Pending,
            ObservedContextRevision = 0,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.AddRange(project, task, sourceSession, handoff);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (project, task, handoff);
    }

    private static AgentSession NewSession(Guid taskId, AgentSessionRole role, AgentSessionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        Role = role,
        Status = status,
        ContextRevisionRead = 0,
        StartedUtc = DateTime.UtcNow.AddMinutes(-5),
        CompletedUtc = status == AgentSessionStatus.Started ? null : DateTime.UtcNow.AddMinutes(-1)
    };
}
