using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Approval correctness and concurrency.
///
/// Every test here runs against a real file-backed SQLite database. Concurrency tests additionally
/// use independent <see cref="FamiliarDbContext"/> instances over separate connections, because an
/// in-memory provider shares one store and one change tracker and therefore proves nothing about
/// the serialization behavior this design depends on.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class WorkApprovalServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static WorkApprovalService CreateService(FamiliarDbContext dbContext) =>
        new(dbContext, new WorkflowDispatchService(dbContext), new TestTimeProvider(FixedNow));

    [Fact]
    public async Task Approval_creates_exactly_one_ready_task_and_one_started_planner_session()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var revisionBefore = await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id);

        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(
            dbContext,
            project.Id,
            "Review the intake slice",
            "Review the conversational intake slice and list the smallest follow-up work.");

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.Approved, outcome.Status);

        var task = await dbContext.Tasks.AsNoTracking().SingleAsync();
        Assert.Equal(outcome.TaskId, task.Id);
        Assert.Equal(project.Id, task.ProjectId);
        Assert.Equal(TaskStatus.Ready, task.Status);
        Assert.Equal("Review the intake slice", task.Title);
        Assert.Equal(
            "Review the conversational intake slice and list the smallest follow-up work.",
            task.RequestedOutcome);

        var session = await dbContext.AgentSessions.AsNoTracking().SingleAsync();
        Assert.Equal(outcome.SessionId, session.Id);
        Assert.Equal(task.Id, session.TaskId);
        Assert.Equal(AgentSessionRole.Planner, session.Role);
        Assert.Equal(AgentSessionStatus.Started, session.Status);

        // Approval never chooses a provider: that stays a worker/runner concern.
        Assert.Null(session.Provider);
        Assert.Null(session.ExternalSessionReference);
        Assert.Null(session.ClaimedByWorkerId);

        // Task creation and session start each advance the revision exactly once, as the manual
        // pages already do, and the session records the revision visible at its own start.
        var revisionAfter = await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id);
        Assert.Equal(revisionBefore + 2, revisionAfter);
        Assert.Equal(revisionAfter, session.ContextRevisionRead);
    }

    [Fact]
    public async Task Approval_marks_both_records_approved_and_stores_the_links()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        var after = await WorkProposalServiceTests.ReadProposalAsync(dbContext, conversationId);

        Assert.Equal(ConversationStatus.Approved, conversation.Status);
        Assert.Equal(WorkProposalStatus.Approved, after.Status);
        Assert.Equal(outcome.TaskId, conversation.ApprovedTaskId);
        Assert.Equal(outcome.SessionId, conversation.ApprovedSessionId);
        Assert.Equal(outcome.TaskId, after.CreatedTaskId);
        Assert.Equal(outcome.SessionId, after.CreatedSessionId);
        Assert.NotEqual(proposal.ConcurrencyToken, after.ConcurrencyToken);

        var messages = await WorkProposalServiceTests.ReadMessagesAsync(dbContext, conversationId);
        Assert.Contains("Approved.", messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("No later role starts on its own", messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sequential_replay_returns_the_original_links_and_creates_nothing_more()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        var service = CreateService(dbContext);
        var first = await service.ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));
        var revisionAfterFirst = await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id);

        // Replaying the same token, and replaying with the rotated token, must both be inert.
        var replayWithOldToken = await service.ApproveAsync(
            new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));
        var currentToken = (await WorkProposalServiceTests.ReadProposalAsync(dbContext, conversationId))
            .ConcurrencyToken;
        var replayWithNewToken = await service.ApproveAsync(new WorkApprovalRequest(conversationId, currentToken));

        Assert.Equal(WorkApprovalStatus.AlreadyApproved, replayWithOldToken.Status);
        Assert.Equal(WorkApprovalStatus.AlreadyApproved, replayWithNewToken.Status);
        Assert.Equal(first.TaskId, replayWithOldToken.TaskId);
        Assert.Equal(first.SessionId, replayWithNewToken.SessionId);

        Assert.Equal(1, await dbContext.Tasks.CountAsync());
        Assert.Equal(1, await dbContext.AgentSessions.CountAsync());
        Assert.Equal(revisionAfterFirst, await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id));
    }

    [Fact]
    public async Task A_stale_token_cannot_approve()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        await new WorkProposalService(dbContext, new TestTimeProvider(FixedNow)).ReviseAsync(
            new ProposalRevisionRequest(
                conversationId,
                proposal.ConcurrencyToken,
                project.Id,
                "A newer title",
                "A newer outcome."));

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.StaleProposal, outcome.Status);
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
    }

    [Fact]
    public async Task A_stale_project_context_blocks_approval_and_creates_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        // The project's context moved after the user reviewed the proposal.
        var tracked = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        tracked.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var revisionBefore = await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id);

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.StaleContext, outcome.Status);
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
        Assert.Equal(revisionBefore, await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id));

        // The proposal stayed Pending: a blocked approval consumes nothing.
        var after = await WorkProposalServiceTests.ReadProposalAsync(dbContext, conversationId);
        Assert.Equal(WorkProposalStatus.Pending, after.Status);
        Assert.Equal(proposal.ConcurrencyToken, after.ConcurrencyToken);
    }

    [Fact]
    public async Task Refreshing_after_a_context_change_makes_approval_possible_again()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        var tracked = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        tracked.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        await new WorkProposalService(dbContext, new TestTimeProvider(FixedNow))
            .RefreshContextAsync(new ProposalActionRequest(conversationId, proposal.ConcurrencyToken));

        var refreshed = await WorkProposalServiceTests.ReadProposalAsync(dbContext, conversationId);
        var revisionBeforeApproval = await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id);

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, refreshed.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.Approved, outcome.Status);
        Assert.Equal(
            revisionBeforeApproval + 2,
            await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id));
    }

    [Fact]
    public async Task An_unresolved_project_cannot_be_approved()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, projectId: null);

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.ValidationFailed, outcome.Status);
        Assert.True(outcome.ValidationErrors!.ContainsKey(WorkApprovalService.ProjectField));
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
    }

    [Fact]
    public async Task An_archived_project_cannot_be_approved()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        var tracked = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        tracked.Status = ProjectStatus.Archived;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
    }

    [Fact]
    public async Task A_rejected_proposal_cannot_be_approved()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        await new WorkProposalService(dbContext, new TestTimeProvider(FixedNow))
            .RejectAsync(new ProposalActionRequest(conversationId, proposal.ConcurrencyToken));

        var currentToken = (await WorkProposalServiceTests.ReadProposalAsync(dbContext, conversationId))
            .ConcurrencyToken;

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, currentToken));

        Assert.Equal(WorkApprovalStatus.AlreadyRejected, outcome.Status);
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
    }

    [Fact]
    public async Task An_approved_proposal_cannot_be_rejected_or_revised_afterwards()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        var approved = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        var currentToken = (await WorkProposalServiceTests.ReadProposalAsync(dbContext, conversationId))
            .ConcurrencyToken;
        var proposals = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var reject = await proposals.RejectAsync(new ProposalActionRequest(conversationId, currentToken));
        var revise = await proposals.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            currentToken,
            project.Id,
            "Trying to change approved work",
            "This must not be applied."));

        Assert.Equal(ProposalActionStatus.AlreadyTerminal, reject.Status);
        Assert.Equal(ProposalActionStatus.AlreadyTerminal, revise.Status);

        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        Assert.Equal(ConversationStatus.Approved, conversation.Status);
        Assert.Equal(approved.TaskId, conversation.ApprovedTaskId);
        Assert.Equal(1, await dbContext.Tasks.CountAsync());
    }

    /// <summary>
    /// The sprint's core concurrency claim. Eight callers, eight independent contexts and
    /// connections, released together, all presenting the same valid token. Exactly one may win.
    /// </summary>
    [Fact]
    public async Task Eight_concurrent_approvals_create_exactly_one_task_and_one_session()
    {
        using var database = new TemporarySqliteDatabase();
        var seedContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(seedContext, "Find Familiar");
        var revisionBefore = await WorkProposalServiceTests.CurrentRevisionAsync(seedContext, project.Id);
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(seedContext, project.Id);

        const int contenders = 8;
        var services = new List<WorkApprovalService>();
        for (var index = 0; index < contenders; index++)
        {
            services.Add(CreateService(await database.CreateContextAsync()));
        }

        // A barrier rather than a sleep: every contender is inside ApproveAsync before any of them
        // can reach the conditional consume, so the overlap is real and the test is not timing-luck.
        using var releaseAll = new SemaphoreSlim(0, contenders);
        var ready = new CountdownEvent(contenders);

        var attempts = services.Select(service => Task.Run(async () =>
        {
            ready.Signal();
            await releaseAll.WaitAsync();
            return await service.ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));
        })).ToList();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "Contenders did not all reach the start line.");
        releaseAll.Release(contenders);

        var outcomes = await Task.WhenAll(attempts);

        // Exactly one winner, and every loser received a coherent, honest answer.
        //
        // DatabaseBusy joined this set in Sprint 10. Under eight-way contention a loser can meet
        // SQLITE_BUSY while acquiring the transaction, and that is not a lost race — nobody beat it,
        // the lock was simply unavailable. Before the fix that contender was either reported as a
        // lost race or escaped as an unhandled exception; reporting it accurately is the corrected
        // behaviour, not a relaxation. The invariants that matter are asserted unchanged below:
        // exactly one Approved, exactly one task, exactly one session.
        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == WorkApprovalStatus.Approved));

        // StaleContext is permitted here, reluctantly, and it is a pre-existing imprecision rather
        // than a Sprint 10 change. ApproveAsync's preflight reads the proposal and the project in two
        // separate statements. A contender that reads the proposal while it is still Pending, and
        // then reads the project after the winner's commit has advanced ContextRevision by two, is
        // told its context went stale — when what actually happened is that someone else approved.
        // Nothing is created either way, so no guarantee is broken, but the message points the user
        // at a refresh rather than at the approval that won. Recorded for the owner as a follow-up;
        // fixing it means changing Sprint 08 contention semantics, which this review deliberately
        // did not broaden into.
        var permitted = new[]
        {
            WorkApprovalStatus.Approved,
            WorkApprovalStatus.AlreadyApproved,
            WorkApprovalStatus.Conflict,
            WorkApprovalStatus.DatabaseBusy,
            WorkApprovalStatus.StaleContext
        };

        var unexpected = outcomes.Where(outcome => !permitted.Contains(outcome.Status)).ToList();
        Assert.True(
            unexpected.Count == 0,
            $"Unexpected contention outcomes: [{string.Join(", ", unexpected.Select(o => o.Status))}]. "
            + $"All outcomes: [{string.Join(", ", outcomes.Select(o => o.Status))}]");

        // No loser may carry the winner's links. A busy, conflicted or stale-context contender
        // created nothing and must not describe work it did not do.
        Assert.All(
            outcomes.Where(outcome =>
                outcome.Status is WorkApprovalStatus.Conflict
                    or WorkApprovalStatus.DatabaseBusy
                    or WorkApprovalStatus.StaleContext),
            outcome =>
            {
                Assert.Null(outcome.TaskId);
                Assert.Null(outcome.SessionId);
            });

        var winner = outcomes.Single(outcome => outcome.Status == WorkApprovalStatus.Approved);

        var verifyContext = await database.CreateContextAsync();
        Assert.Equal(1, await verifyContext.Tasks.CountAsync());
        Assert.Equal(1, await verifyContext.AgentSessions.CountAsync());

        var task = await verifyContext.Tasks.AsNoTracking().SingleAsync();
        var session = await verifyContext.AgentSessions.AsNoTracking().SingleAsync();
        Assert.Equal(winner.TaskId, task.Id);
        Assert.Equal(winner.SessionId, session.Id);

        // Every caller that reported AlreadyApproved must point at the same single pair of links.
        foreach (var replay in outcomes.Where(outcome => outcome.Status == WorkApprovalStatus.AlreadyApproved))
        {
            Assert.Equal(winner.TaskId, replay.TaskId);
            Assert.Equal(winner.SessionId, replay.SessionId);
        }

        var conversation = await verifyContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        Assert.Equal(ConversationStatus.Approved, conversation.Status);
        Assert.Equal(task.Id, conversation.ApprovedTaskId);
        Assert.Equal(session.Id, conversation.ApprovedSessionId);

        // Two increments in total, not sixteen: the losers rolled back completely.
        Assert.Equal(
            revisionBefore + 2,
            await WorkProposalServiceTests.CurrentRevisionAsync(verifyContext, project.Id));
        Assert.Equal(session.ContextRevisionRead, revisionBefore + 2);
    }

    /// <summary>Approval racing rejection: one terminal winner, and the loser changes nothing.</summary>
    [Fact]
    public async Task Approval_racing_rejection_produces_exactly_one_terminal_outcome()
    {
        using var database = new TemporarySqliteDatabase();
        var seedContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(seedContext, "Find Familiar");
        var revisionBefore = await WorkProposalServiceTests.CurrentRevisionAsync(seedContext, project.Id);
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(seedContext, project.Id);

        var approvalService = CreateService(await database.CreateContextAsync());
        var rejectionService = new WorkProposalService(
            await database.CreateContextAsync(),
            new TestTimeProvider(FixedNow));

        using var releaseAll = new SemaphoreSlim(0, 2);
        var ready = new CountdownEvent(2);

        var approveTask = Task.Run(async () =>
        {
            ready.Signal();
            await releaseAll.WaitAsync();
            return await approvalService.ApproveAsync(
                new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));
        });

        var rejectTask = Task.Run(async () =>
        {
            ready.Signal();
            await releaseAll.WaitAsync();
            return await rejectionService.RejectAsync(
                new ProposalActionRequest(conversationId, proposal.ConcurrencyToken));
        });

        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "Contenders did not all reach the start line.");
        releaseAll.Release(2);

        var approval = await approveTask;
        var rejection = await rejectTask;

        var verifyContext = await database.CreateContextAsync();
        var conversation = await verifyContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);

        var approvalWon = approval.Status == WorkApprovalStatus.Approved;
        var rejectionWon = rejection.Status == ProposalActionStatus.Success;

        Assert.True(approvalWon ^ rejectionWon, "Exactly one of approval and rejection must win.");

        if (approvalWon)
        {
            Assert.Equal(ConversationStatus.Approved, conversation.Status);
            Assert.Equal(1, await verifyContext.Tasks.CountAsync());
            Assert.Equal(1, await verifyContext.AgentSessions.CountAsync());
            Assert.NotNull(conversation.ApprovedTaskId);
            Assert.Equal(
                revisionBefore + 2,
                await WorkProposalServiceTests.CurrentRevisionAsync(verifyContext, project.Id));
        }
        else
        {
            // A rejected conversation must hold no work links and must have created nothing.
            Assert.Equal(ConversationStatus.Rejected, conversation.Status);
            Assert.Equal(0, await verifyContext.Tasks.CountAsync());
            Assert.Equal(0, await verifyContext.AgentSessions.CountAsync());
            Assert.Null(conversation.ApprovedTaskId);
            Assert.Null(conversation.ApprovedSessionId);
            Assert.Equal(
                revisionBefore,
                await WorkProposalServiceTests.CurrentRevisionAsync(verifyContext, project.Id));
        }
    }

    /// <summary>A revision racing approval must never leave approved data describing the loser.</summary>
    [Fact]
    public async Task Revision_racing_approval_cannot_corrupt_the_approved_data()
    {
        using var database = new TemporarySqliteDatabase();
        var seedContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(seedContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(
            seedContext,
            project.Id,
            "The reviewed title",
            "The reviewed outcome.");

        var approvalService = CreateService(await database.CreateContextAsync());
        var revisionService = new WorkProposalService(
            await database.CreateContextAsync(),
            new TestTimeProvider(FixedNow));

        using var releaseAll = new SemaphoreSlim(0, 2);
        var ready = new CountdownEvent(2);

        var approveTask = Task.Run(async () =>
        {
            ready.Signal();
            await releaseAll.WaitAsync();
            return await approvalService.ApproveAsync(
                new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));
        });

        var reviseTask = Task.Run(async () =>
        {
            ready.Signal();
            await releaseAll.WaitAsync();
            return await revisionService.ReviseAsync(new ProposalRevisionRequest(
                conversationId,
                proposal.ConcurrencyToken,
                project.Id,
                "The racing title",
                "The racing outcome."));
        });

        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "Contenders did not all reach the start line.");
        releaseAll.Release(2);

        var approval = await approveTask;
        var revision = await reviseTask;

        Assert.False(
            approval.Status == WorkApprovalStatus.Approved && revision.Status == ProposalActionStatus.Success,
            "A revision must not succeed against a proposal the same token already approved.");

        var verifyContext = await database.CreateContextAsync();
        var tasks = await verifyContext.Tasks.AsNoTracking().ToListAsync();
        Assert.True(tasks.Count <= 1);

        if (approval.Status == WorkApprovalStatus.Approved)
        {
            // The created task must describe what the user actually reviewed and approved.
            Assert.Equal("The reviewed title", tasks[0].Title);
            Assert.Equal("The reviewed outcome.", tasks[0].RequestedOutcome);
            Assert.Equal(1, await verifyContext.AgentSessions.CountAsync());
        }
        else
        {
            Assert.Empty(tasks);
            Assert.Equal(0, await verifyContext.AgentSessions.CountAsync());
        }
    }

    /// <summary>
    /// A locked database during approval must report DatabaseBusy, not a lost race.
    ///
    /// Until Sprint 10 every SqliteException here became a lost approval race. The busy fix corrected
    /// the classification, but its only coverage was on the predicate; this exercises the whole
    /// approval path against a genuine exclusive lock held on a second connection.
    ///
    /// The lock is taken before ApproveAsync runs, so it is met while acquiring the transaction —
    /// the most likely moment on a contended database, and the one that previously escaped as an
    /// unhandled exception because the transaction is acquired outside the dispatch try block.
    /// </summary>
    [Fact]
    public async Task Approving_a_proposal_against_a_locked_database_reports_busy_rather_than_a_race()
    {
        using var database = new TemporarySqliteDatabase();
        var seedContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(seedContext, "Find Familiar");
        var revisionBefore = await WorkProposalServiceTests.CurrentRevisionAsync(seedContext, project.Id);
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(seedContext, project.Id);

        await using var impatient = await database.CreateImpatientContextAsync();

        await using var blocker = new SqliteConnection($"{database.ConnectionString};Pooling=False");
        await blocker.OpenAsync();
        await using (var exclusive = blocker.CreateCommand())
        {
            exclusive.CommandText = "BEGIN EXCLUSIVE;";
            await exclusive.ExecuteNonQueryAsync();
        }

        var outcome = await CreateService(impatient)
            .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(WorkApprovalStatus.DatabaseBusy, outcome.Status);

        // Nobody won, so nothing may claim a winner.
        Assert.NotEqual(WorkApprovalStatus.Conflict, outcome.Status);
        Assert.NotEqual(WorkApprovalStatus.AlreadyApproved, outcome.Status);
        Assert.Null(outcome.TaskId);
        Assert.Null(outcome.SessionId);

        await using (var release = blocker.CreateCommand())
        {
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync();
        }

        // No work was mutated: no task, no session, no revision drift, proposal still Pending.
        var verifyContext = await database.CreateContextAsync();
        Assert.Equal(0, await verifyContext.Tasks.CountAsync());
        Assert.Equal(0, await verifyContext.AgentSessions.CountAsync());
        Assert.Equal(
            revisionBefore,
            await WorkProposalServiceTests.CurrentRevisionAsync(verifyContext, project.Id));

        var after = await WorkProposalServiceTests.ReadProposalAsync(verifyContext, conversationId);
        Assert.Equal(WorkProposalStatus.Pending, after.Status);
        Assert.Equal(proposal.ConcurrencyToken, after.ConcurrencyToken);
        Assert.Null(after.CreatedTaskId);
        Assert.Null(after.CreatedSessionId);

        var conversation = await verifyContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        Assert.Equal(ConversationStatus.AwaitingApproval, conversation.Status);
        Assert.Null(conversation.ApprovedTaskId);
    }

    /// <summary>
    /// A non-busy database failure must stay a generic conflict rather than being reported as busy.
    /// The failure is real: a BEFORE INSERT trigger aborts the session insert, surfacing as
    /// SQLITE_CONSTRAINT_TRIGGER.
    /// </summary>
    [Fact]
    public async Task A_non_busy_failure_during_proposal_approval_stays_a_generic_conflict()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER "ReviewProbeAbortsWorkSessionInsert"
            BEFORE INSERT ON "AgentSessions"
            BEGIN
                SELECT RAISE(ABORT, 'review probe: not a lock');
            END;
            """);

        try
        {
            var outcome = await CreateService(dbContext)
                .ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

            Assert.Equal(WorkApprovalStatus.Conflict, outcome.Status);
            Assert.NotEqual(WorkApprovalStatus.DatabaseBusy, outcome.Status);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS \"ReviewProbeAbortsWorkSessionInsert\";");
        }

        var verifyContext = await database.CreateContextAsync();
        Assert.Equal(0, await verifyContext.AgentSessions.CountAsync());

        var after = await WorkProposalServiceTests.ReadProposalAsync(verifyContext, conversationId);
        Assert.Equal(WorkProposalStatus.Pending, after.Status);
        Assert.Null(after.CreatedSessionId);
    }

    /// <summary>
    /// Rollback proof. The dispatch seam is decorated so the transaction fails at SaveChanges —
    /// after the proposal has already been conditionally consumed and after both context-revision
    /// increments have been staged. That is precisely the window where a partial dispatch would be
    /// most damaging: a consumed proposal with a task but no session, or a moved revision with no
    /// work at all.
    /// </summary>
    [Fact]
    public async Task A_failure_after_consuming_the_proposal_leaves_no_partial_work()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var revisionBefore = await WorkProposalServiceTests.CurrentRevisionAsync(dbContext, project.Id);
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, project.Id);

        var failureContext = await database.CreateContextAsync();
        var service = new WorkApprovalService(
            failureContext,
            new FailingWorkflowDispatchService(new WorkflowDispatchService(failureContext)),
            new TestTimeProvider(FixedNow));

        var outcome = await service.ApproveAsync(new WorkApprovalRequest(conversationId, proposal.ConcurrencyToken));

        Assert.NotEqual(WorkApprovalStatus.Approved, outcome.Status);

        var verifyContext = await database.CreateContextAsync();

        // Nothing survived: no task, no session, no revision drift, and the proposal is still Pending.
        Assert.Equal(0, await verifyContext.Tasks.CountAsync());
        Assert.Equal(0, await verifyContext.AgentSessions.CountAsync());
        Assert.Equal(
            revisionBefore,
            await WorkProposalServiceTests.CurrentRevisionAsync(verifyContext, project.Id));

        var after = await WorkProposalServiceTests.ReadProposalAsync(verifyContext, conversationId);
        Assert.Equal(WorkProposalStatus.Pending, after.Status);
        Assert.Equal(proposal.ConcurrencyToken, after.ConcurrencyToken);
        Assert.Null(after.CreatedTaskId);
        Assert.Null(after.CreatedSessionId);

        var conversation = await verifyContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        Assert.Equal(ConversationStatus.AwaitingApproval, conversation.Status);
        Assert.Null(conversation.ApprovedTaskId);
    }

    [Fact]
    public async Task An_unknown_conversation_is_reported_as_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();

        var outcome = await CreateService(dbContext)
            .ApproveAsync(new WorkApprovalRequest(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(WorkApprovalStatus.NotFound, outcome.Status);
    }

    /// <summary>
    /// Stages a genuinely unsavable task — its ProjectId references a project that does not exist —
    /// so the approval transaction fails on a real foreign-key violation rather than an exception
    /// the service could have special-cased.
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
            DateTime nowUtc)
        {
            var task = inner.CreateReadyTask(project, title, requestedOutcome, nowUtc);
            task.ProjectId = Guid.NewGuid();
            return task;
        }

        public AgentSession StartSession(
            FamiliarTask task,
            FamiliarProject project,
            AgentSessionRole role,
            string? provider,
            string? externalSessionReference,
            DateTime startedUtc) =>
            inner.StartSession(task, project, role, provider, externalSessionReference, startedUtc);
    }
}
