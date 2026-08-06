using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The confirming transaction: the one place provider output can reach persisted state.
///
/// Every race here runs on a real file-backed SQLite database with independent contexts, for the
/// reason ADR-0008 and ADR-0009 both give — an in-memory provider would prove nothing about the
/// serialization this design depends on.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarActionServiceTests
{
    // ---------------------------------------------------------------- the invariant

    /// <summary>
    /// The sprint's central guarantee, stated as a test: a Pending proposal has created nothing. No
    /// task, no session, no revision change. Only a confirmation turns it into work.
    /// </summary>
    [Fact]
    public async Task A_pending_proposal_has_created_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, _) = await SeedCreateTaskProposalAsync(dbContext);

        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
        Assert.Equal(0, await RevisionAsync(dbContext, project.Id));
    }

    // ---------------------------------------------------------------- CreateTask

    [Fact]
    public async Task Confirming_create_task_creates_exactly_one_ready_task_and_no_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var revisionBefore = await RevisionAsync(dbContext, project.Id);

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.Confirmed, outcome.Status);
        Assert.NotNull(outcome.CreatedTaskId);
        Assert.Null(outcome.CreatedSessionId);

        dbContext.ChangeTracker.Clear();

        var task = await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == outcome.CreatedTaskId);
        Assert.Equal(TaskStatus.Ready, task.Status);
        Assert.Equal(project.Id, task.ProjectId);

        // No session starts, and no worker is notified.
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());

        // Task creation advances the revision once — the same effect as creating one by hand.
        Assert.Equal(revisionBefore + 1, await RevisionAsync(dbContext, project.Id));

        var stored = await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Confirmed, stored.Status);
        Assert.Equal(task.Id, stored.CreatedTaskId);
        Assert.Null(stored.CreatedSessionId);
        Assert.NotNull(stored.DecidedUtc);
        Assert.NotEqual(proposal.ConcurrencyToken, stored.ConcurrencyToken);
    }

    /// <summary>The human's edits are what get created, not the provider's original text.</summary>
    [Fact]
    public async Task The_humans_edited_title_and_outcome_are_what_get_created()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id,
            new FamiliarActionRequest(
                proposal.Id, proposal.ConcurrencyToken, "  A human wrote this  ", "  And this outcome.  "));

        Assert.Equal(FamiliarActionStatusOutcome.Confirmed, outcome.Status);

        dbContext.ChangeTracker.Clear();
        var task = await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == outcome.CreatedTaskId);

        Assert.Equal("A human wrote this", task.Title);
        Assert.Equal("And this outcome.", task.RequestedOutcome);
    }

    [Theory]
    [InlineData("", "An outcome.")]
    [InlineData("   ", "An outcome.")]
    [InlineData("A title", "")]
    public async Task An_invalid_edit_is_refused_and_creates_nothing(string title, string outcomeText)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken, title, outcomeText));

        Assert.Equal(FamiliarActionStatusOutcome.ValidationFailed, outcome.Status);
        Assert.NotNull(outcome.ValidationMessage);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());

        // The proposal is untouched and still decidable.
        var stored = await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Pending, stored.Status);
        Assert.Equal(proposal.ConcurrencyToken, stored.ConcurrencyToken);
    }

    /// <summary>
    /// CreateTask is revision-gated: the human approved content they reviewed, and if the project
    /// moved underneath them what they read is no longer what they would be creating.
    /// </summary>
    [Fact]
    public async Task Create_task_is_refused_when_the_context_revision_moved()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);

        // Something unrelated advances the project.
        var tracked = await dbContext.Projects.SingleAsync(p => p.Id == project.Id);
        tracked.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.ContextMoved, outcome.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());

        // The consume rolled back with everything else, so the proposal survives for another look.
        var stored = await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Pending, stored.Status);
    }

    // ---------------------------------------------------------------- StartPlanner

    [Fact]
    public async Task Confirming_start_planner_starts_exactly_one_session_and_creates_no_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, task, proposal) = await SeedStartPlannerProposalAsync(dbContext);
        var tasksBefore = await dbContext.Tasks.AsNoTracking().CountAsync();

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.Confirmed, outcome.Status);
        Assert.NotNull(outcome.CreatedSessionId);
        Assert.Null(outcome.CreatedTaskId);

        dbContext.ChangeTracker.Clear();

        var session = await dbContext.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == outcome.CreatedSessionId);
        Assert.Equal(AgentSessionRole.Planner, session.Role);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Equal(task.Id, session.TaskId);

        // The Familiar never chooses a worker.
        Assert.Null(session.Provider);
        Assert.Null(session.ExternalSessionReference);

        Assert.Equal(tasksBefore, await dbContext.Tasks.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// StartPlanner has no revision gate, and a test says so: the decision is "run this role now",
    /// and the session reads whatever context is current at its own start (ADR-0010).
    /// </summary>
    [Fact]
    public async Task Start_planner_has_no_revision_gate()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedStartPlannerProposalAsync(dbContext);

        var tracked = await dbContext.Projects.SingleAsync(p => p.Id == project.Id);
        tracked.IncrementContextRevision();
        tracked.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.Confirmed, outcome.Status);
    }

    [Fact]
    public async Task Start_planner_is_refused_when_the_task_already_has_a_started_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, task, proposal) = await SeedStartPlannerProposalAsync(dbContext);

        dbContext.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Implementer,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.TaskAlreadyRunning, outcome.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking()
            .Where(s => s.Role == AgentSessionRole.Planner).ToListAsync());
    }

    /// <summary>A target that left this project cannot be started from this page.</summary>
    [Fact]
    public async Task Start_planner_is_refused_when_the_target_is_not_this_projects_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, task, proposal) = await SeedStartPlannerProposalAsync(dbContext);
        var elsewhere = await SeedProjectAsync(dbContext);

        await dbContext.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ProjectId, elsewhere.Id));
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.TargetTaskInvalid, outcome.Status);
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- gates common to both

    [Fact]
    public async Task A_stale_token_changes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, Guid.NewGuid()));

        Assert.Equal(FamiliarActionStatusOutcome.StaleToken, outcome.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());

        var stored = await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Pending, stored.Status);
        Assert.Equal(proposal.ConcurrencyToken, stored.ConcurrencyToken);
    }

    [Fact]
    public async Task An_inactive_project_is_refused()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);

        await dbContext.Projects.Where(p => p.Id == project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProjectStatus.Archived));
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.ProjectInactive, outcome.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
        Assert.Equal(
            FamiliarActionStatus.Pending,
            (await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id)).Status);
    }

    /// <summary>
    /// A proposal id from another project cannot be confirmed from this page — the ownership filter
    /// is on the proposal's own denormalised column, so it needs no join and cannot be bypassed.
    /// </summary>
    [Fact]
    public async Task A_proposal_from_another_project_is_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var attacker = await SeedProjectAsync(dbContext);

        var outcome = await NewService(dbContext).ConfirmAsync(
            attacker.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        Assert.Equal(FamiliarActionStatusOutcome.NotFound, outcome.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
        Assert.Equal(
            FamiliarActionStatus.Pending,
            (await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id)).Status);
    }

    [Fact]
    public async Task An_unknown_proposal_is_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        Assert.Equal(
            FamiliarActionStatusOutcome.NotFound,
            (await NewService(dbContext).ConfirmAsync(
                project.Id, new FamiliarActionRequest(Guid.NewGuid(), Guid.NewGuid()))).Status);
    }

    // ---------------------------------------------------------------- replay and races

    [Fact]
    public async Task A_replayed_confirmation_returns_the_original_links_and_creates_nothing_new()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var request = new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken);

        var first = await NewService(dbContext).ConfirmAsync(project.Id, request);
        dbContext.ChangeTracker.Clear();
        var replay = await NewService(dbContext).ConfirmAsync(project.Id, request);

        Assert.Equal(FamiliarActionStatusOutcome.Confirmed, first.Status);
        Assert.Equal(FamiliarActionStatusOutcome.AlreadyConfirmed, replay.Status);
        Assert.Equal(first.CreatedTaskId, replay.CreatedTaskId);

        dbContext.ChangeTracker.Clear();
        Assert.Single(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
    }

    /// <summary>
    /// Two simultaneous confirmations, on independent connections: exactly one task, and a loser
    /// whose report is truthful about what happened.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_confirmations_create_exactly_one_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seed = await database.CreateContextAsync();
        var (project, _, proposal) = await SeedCreateTaskProposalAsync(seed);

        await using var first = await database.CreateContextAsync();
        await using var second = await database.CreateContextAsync();

        var request = new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken);

        var outcomes = await Task.WhenAll(
            Task.Run(() => NewService(first).ConfirmAsync(project.Id, request)),
            Task.Run(() => NewService(second).ConfirmAsync(project.Id, request)));

        var winners = outcomes.Where(o => o.Status == FamiliarActionStatusOutcome.Confirmed).ToList();
        var losers = outcomes.Where(o => o.Status != FamiliarActionStatusOutcome.Confirmed).ToList();

        Assert.Single(winners);
        Assert.Single(losers);

        // The loser tells the truth: either a real competing decision, or a retryable busy database.
        // It never claims a competitor that does not exist.
        Assert.Contains(losers[0].Status, new[]
        {
            FamiliarActionStatusOutcome.AlreadyConfirmed,
            FamiliarActionStatusOutcome.StaleToken,
            FamiliarActionStatusOutcome.DatabaseBusy
        });

        seed.ChangeTracker.Clear();
        Assert.Single(await seed.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
    }

    [Fact]
    public async Task Two_concurrent_confirmations_start_exactly_one_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seed = await database.CreateContextAsync();
        var (project, task, proposal) = await SeedStartPlannerProposalAsync(seed);

        await using var first = await database.CreateContextAsync();
        await using var second = await database.CreateContextAsync();

        var request = new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken);

        var outcomes = await Task.WhenAll(
            Task.Run(() => NewService(first).ConfirmAsync(project.Id, request)),
            Task.Run(() => NewService(second).ConfirmAsync(project.Id, request)));

        Assert.Single(outcomes, o => o.Status == FamiliarActionStatusOutcome.Confirmed);

        seed.ChangeTracker.Clear();
        Assert.Single(await seed.AgentSessions.AsNoTracking().Where(s => s.TaskId == task.Id).ToListAsync());
    }

    // ---------------------------------------------------------------- dismissal

    [Fact]
    public async Task Dismissal_creates_nothing_and_is_terminal()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var request = new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken);

        var outcome = await NewService(dbContext).DismissAsync(project.Id, request);
        Assert.Equal(FamiliarActionStatusOutcome.Dismissed, outcome.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());

        var stored = await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Dismissed, stored.Status);
        Assert.NotNull(stored.DecidedUtc);
        Assert.Null(stored.CreatedTaskId);
        Assert.NotEqual(proposal.ConcurrencyToken, stored.ConcurrencyToken);

        // Terminal: the rotated token means the original view cannot then confirm it.
        dbContext.ChangeTracker.Clear();
        var afterwards = await NewService(dbContext).ConfirmAsync(project.Id, request);
        Assert.Equal(FamiliarActionStatusOutcome.AlreadyDismissed, afterwards.Status);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
    }

    [Fact]
    public async Task A_replayed_dismissal_reports_the_original_decision()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var request = new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken);

        await NewService(dbContext).DismissAsync(project.Id, request);
        dbContext.ChangeTracker.Clear();

        Assert.Equal(
            FamiliarActionStatusOutcome.AlreadyDismissed,
            (await NewService(dbContext).DismissAsync(project.Id, request)).Status);
    }

    /// <summary>A confirmed proposal cannot then be dismissed, and the links survive.</summary>
    [Fact]
    public async Task A_confirmed_proposal_cannot_be_dismissed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var request = new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken);

        var confirmed = await NewService(dbContext).ConfirmAsync(project.Id, request);
        dbContext.ChangeTracker.Clear();

        var dismissal = await NewService(dbContext).DismissAsync(project.Id, request);

        Assert.Equal(FamiliarActionStatusOutcome.AlreadyConfirmed, dismissal.Status);
        Assert.Equal(confirmed.CreatedTaskId, dismissal.CreatedTaskId);
    }

    // ---------------------------------------------------------------- the confirmation message

    /// <summary>
    /// The message stating what was created commits with the effect it describes, so a transcript
    /// can never claim a task that does not exist.
    /// </summary>
    [Fact]
    public async Task A_confirmation_message_is_written_with_the_effect()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);

        await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken, "Wire it", "Make it resolve."));

        dbContext.ChangeTracker.Clear();

        var messages = await dbContext.FamiliarMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == proposal.ConversationId)
            .OrderBy(m => m.Sequence)
            .ToListAsync();

        var confirmation = messages[^1];
        Assert.Equal(FamiliarMessageAuthor.Familiar, confirmation.Author);
        Assert.Contains("Wire it", confirmation.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing is running on it yet", confirmation.Content, StringComparison.Ordinal);
        Assert.Equal(FamiliarMessageDelivery.Delivered, confirmation.Delivery);
    }

    /// <summary>A refused confirmation writes no message either — rollback leaves no partial effect.</summary>
    [Fact]
    public async Task A_refused_confirmation_writes_no_message()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, _, proposal) = await SeedCreateTaskProposalAsync(dbContext);
        var before = await dbContext.FamiliarMessages.AsNoTracking().CountAsync();

        var tracked = await dbContext.Projects.SingleAsync(p => p.Id == project.Id);
        tracked.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        await NewService(dbContext).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(before, await dbContext.FamiliarMessages.AsNoTracking().CountAsync());
    }

    // ---------------------------------------------------------------- database busy

    /// <summary>
    /// A busy database is a retry, not a lost race. An exclusive lock is held on another connection
    /// while a confirmation runs on an impatient one, so SQLITE_BUSY is genuinely met rather than
    /// simulated — and the outcome must not claim somebody else decided anything.
    /// </summary>
    [Fact]
    public async Task A_busy_database_is_classified_as_busy_and_never_as_a_lost_race()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seed = await database.CreateContextAsync();
        var (project, _, proposal) = await SeedCreateTaskProposalAsync(seed);

        await using var blocker = await database.CreateContextAsync();
        await using var impatient = await database.CreateImpatientContextAsync();

        await using var lockHolder = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync(
            "UPDATE FamiliarActionProposals SET UpdatedUtc = UpdatedUtc WHERE Id = {0}", proposal.Id);

        var outcome = await NewService(impatient).ConfirmAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        await lockHolder.RollbackAsync();

        Assert.Equal(FamiliarActionStatusOutcome.DatabaseBusy, outcome.Status);

        seed.ChangeTracker.Clear();
        Assert.Empty(await seed.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());

        var stored = await seed.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task A_busy_database_is_classified_on_the_dismissal_path_too()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seed = await database.CreateContextAsync();
        var (project, _, proposal) = await SeedCreateTaskProposalAsync(seed);

        await using var blocker = await database.CreateContextAsync();
        await using var impatient = await database.CreateImpatientContextAsync();

        await using var lockHolder = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync(
            "UPDATE FamiliarActionProposals SET UpdatedUtc = UpdatedUtc WHERE Id = {0}", proposal.Id);

        var outcome = await NewService(impatient).DismissAsync(
            project.Id, new FamiliarActionRequest(proposal.Id, proposal.ConcurrencyToken));

        await lockHolder.RollbackAsync();

        Assert.Equal(FamiliarActionStatusOutcome.DatabaseBusy, outcome.Status);
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarActionService NewService(FamiliarDbContext dbContext) =>
        new(dbContext, new WorkflowDispatchService(dbContext), TimeProvider.System);

    private static async Task<int> RevisionAsync(FamiliarDbContext dbContext, Guid projectId)
    {
        dbContext.ChangeTracker.Clear();
        return await dbContext.Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.ContextRevision).SingleAsync();
    }

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Action project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarActionServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    private static async Task<(FamiliarProject Project, FamiliarTask? Task, FamiliarActionProposal Proposal)>
        SeedCreateTaskProposalAsync(FamiliarDbContext dbContext)
    {
        var project = await SeedProjectAsync(dbContext);
        var (conversation, message) = await SeedConversationAsync(dbContext, project.Id);

        var proposal = NewProposal(conversation.Id, project.Id, message.Id, FamiliarActionKind.CreateTask);
        proposal.Title = "A proposed task";
        proposal.RequestedOutcome = "A proposed outcome.";
        proposal.ObservedContextRevision = project.ContextRevision;

        dbContext.FamiliarActionProposals.Add(proposal);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (project, null, proposal);
    }

    private static async Task<(FamiliarProject Project, FamiliarTask Task, FamiliarActionProposal Proposal)>
        SeedStartPlannerProposalAsync(FamiliarDbContext dbContext)
    {
        var project = await SeedProjectAsync(dbContext);
        var (conversation, message) = await SeedConversationAsync(dbContext, project.Id);

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "An existing task",
            RequestedOutcome = "Seeded for FamiliarActionServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        var proposal = NewProposal(conversation.Id, project.Id, message.Id, FamiliarActionKind.StartPlanner);
        proposal.TargetTaskId = task.Id;
        proposal.ObservedContextRevision = project.ContextRevision;

        dbContext.FamiliarActionProposals.Add(proposal);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (project, task, proposal);
    }

    private static FamiliarActionProposal NewProposal(
        Guid conversationId,
        Guid projectId,
        Guid messageId,
        FamiliarActionKind kind) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = conversationId,
        ProjectId = projectId,
        MessageId = messageId,
        Kind = kind,
        Status = FamiliarActionStatus.Pending,
        ConcurrencyToken = Guid.NewGuid(),
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static async Task<(FamiliarConversation Conversation, FamiliarMessage Message)>
        SeedConversationAsync(FamiliarDbContext dbContext, Guid projectId)
    {
        var conversation = new FamiliarConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var message = new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = FamiliarMessageAuthor.Familiar,
            Sequence = 1,
            Content = "I could do that.",
            CreatedUtc = DateTime.UtcNow,
            ProviderName = "Fake",
            ProviderModel = "fake-model-1",
            Delivery = FamiliarMessageDelivery.Delivered
        };

        dbContext.FamiliarConversations.Add(conversation);
        dbContext.FamiliarMessages.Add(message);
        await dbContext.SaveChangesAsync();

        return (conversation, message);
    }
}
