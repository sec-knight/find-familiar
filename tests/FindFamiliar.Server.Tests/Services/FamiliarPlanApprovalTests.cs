using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Approving a plan in the conversation — the human gate Sprint 13 exists to move.
///
/// Two properties carry the slice, and everything here is one of them:
///
/// - <b>approval creates exactly the approved items and starts exactly one session.</b> Not one
///   session per item: a plan written before any of it ran is a guess, and the first result is the
///   best evidence about whether the second step is still right (ADR-0014 §4);
/// - <b>nothing is created without a consumed proposal row and a gate re-checked inside the
///   committing transaction.</b> A stale token, a moved project or an inactive one creates nothing at
///   all — not a partial plan.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarPlanApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approving_creates_every_included_task_and_starts_one_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner, null, AgentSessionRole.Implementer]);

        var outcome = await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.Approved, outcome.Status);
        Assert.Equal(3, outcome.CreatedTaskCount);
        Assert.Equal(AgentSessionRole.Planner, outcome.StartedRole);

        var tasks = await dbContext.Tasks.AsNoTracking().OrderBy(task => task.CreatedUtc).ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.All(tasks, task => Assert.Equal(project.Id, task.ProjectId));

        // Exactly one session, on the first included item that named a role.
        var session = Assert.Single(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
        Assert.Equal(AgentSessionRole.Planner, session.Role);
        Assert.Equal(outcome.StartedSessionId, session.Id);

        // The Familiar never chooses a worker.
        Assert.Null(session.Provider);
        Assert.Null(session.ExternalSessionReference);
    }

    /// <summary>
    /// A plan whose only session is on a later item still starts that one, and only that one. The rule
    /// is "the first included item with a role", not "the first item".
    /// </summary>
    [Fact]
    public async Task The_started_session_is_the_first_included_item_that_names_a_role()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null, AgentSessionRole.Reviewer, AgentSessionRole.Planner]);

        var outcome = await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        Assert.Equal(AgentSessionRole.Reviewer, outcome.StartedRole);
        Assert.Equal(AgentSessionRole.Reviewer, (await dbContext.AgentSessions.AsNoTracking().SingleAsync()).Role);
    }

    [Fact]
    public async Task A_plan_that_names_no_role_creates_tasks_and_starts_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null, null]);

        var outcome = await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.Approved, outcome.Status);
        Assert.Equal(2, outcome.CreatedTaskCount);
        Assert.Null(outcome.StartedSessionId);
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- the human's edits

    /// <summary>
    /// The human's wording is what gets created, not the model's. An itemised approval that shipped
    /// the draft regardless would be a form that did nothing.
    /// </summary>
    [Fact]
    public async Task The_humans_edits_are_what_get_created()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null]);
        var item = plan.Items.Single();

        await NewService(dbContext).ApproveAsync(
            plan.ChatId,
            new FamiliarPlanDecisionRequest(
                plan.Id,
                plan.ConcurrencyToken,
                [new FamiliarPlanItemDecision(item.Id, true, "My own title", "My own outcome.")]));

        var task = await dbContext.Tasks.AsNoTracking().SingleAsync();
        Assert.Equal("My own title", task.Title);
        Assert.Equal("My own outcome.", task.RequestedOutcome);

        // The row records what was created, so the transcript shows the human's version afterwards.
        var stored = await dbContext.FamiliarPlanItems.AsNoTracking().SingleAsync();
        Assert.Equal("My own title", stored.Title);
        Assert.Equal(task.Id, stored.CreatedTaskId);
    }

    [Fact]
    public async Task An_excluded_item_creates_nothing_and_is_recorded_as_excluded()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner, null]);
        var items = plan.Items.OrderBy(item => item.Position).ToList();

        var outcome = await NewService(dbContext).ApproveAsync(
            plan.ChatId,
            new FamiliarPlanDecisionRequest(
                plan.Id,
                plan.ConcurrencyToken,
                [
                    new FamiliarPlanItemDecision(items[0].Id, false),
                    new FamiliarPlanItemDecision(items[1].Id, true)
                ]));

        Assert.Equal(1, outcome.CreatedTaskCount);

        // The excluded item named the only session, so excluding it started nothing.
        Assert.Null(outcome.StartedSessionId);
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());

        var excluded = await dbContext.FamiliarPlanItems.AsNoTracking().SingleAsync(item => item.Id == items[0].Id);
        Assert.False(excluded.IsIncluded);
        Assert.Null(excluded.CreatedTaskId);

        Assert.Equal("Item 1", (await dbContext.Tasks.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task Excluding_everything_creates_nothing_and_leaves_the_plan_pending()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null, null]);

        var outcome = await NewService(dbContext).ApproveAsync(
            plan.ChatId,
            new FamiliarPlanDecisionRequest(
                plan.Id,
                plan.ConcurrencyToken,
                plan.Items.Select(item => new FamiliarPlanItemDecision(item.Id, false)).ToList()));

        Assert.Equal(FamiliarPlanOutcomeStatus.NothingIncluded, outcome.Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());

        // Still decidable: the person can include something, or decline.
        Assert.Equal(
            FamiliarPlanStatus.Pending,
            (await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_included_item_with_no_title_creates_nothing(string title)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null]);

        var outcome = await NewService(dbContext).ApproveAsync(
            plan.ChatId,
            new FamiliarPlanDecisionRequest(
                plan.Id,
                plan.ConcurrencyToken,
                [new FamiliarPlanItemDecision(plan.Items.Single().Id, true, title)]));

        Assert.Equal(FamiliarPlanOutcomeStatus.ValidationFailed, outcome.Status);
        Assert.NotNull(outcome.ValidationMessage);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// An item the form did not mention keeps its drafted state. A checkbox that failed to post must
    /// not silently drop work the person believed they were approving.
    /// </summary>
    [Fact]
    public async Task An_item_the_request_omits_keeps_what_was_drafted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null, null]);

        var outcome = await NewService(dbContext).ApproveAsync(
            plan.ChatId,
            new FamiliarPlanDecisionRequest(plan.Id, plan.ConcurrencyToken, []));

        Assert.Equal(2, outcome.CreatedTaskCount);
    }

    // ---------------------------------------------------------------- the gates

    /// <summary>
    /// The token is the fence. A plan decided or redrawn between rendering and clicking is refused,
    /// and refusing creates nothing at all rather than part of a plan.
    /// </summary>
    [Fact]
    public async Task A_stale_token_creates_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner, null]);

        var outcome = await NewService(dbContext).ApproveAsync(
            plan.ChatId,
            new FamiliarPlanDecisionRequest(
                plan.Id,
                Guid.NewGuid(),
                plan.Items.Select(item => new FamiliarPlanItemDecision(item.Id, true)).ToList()));

        Assert.Equal(FamiliarPlanOutcomeStatus.StaleToken, outcome.Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A plan is content a person read and approved, which is the case ADR-0009's revision gate
    /// protects. If the project moved underneath them, what they read is no longer what would be
    /// created.
    /// </summary>
    [Fact]
    public async Task A_project_that_moved_since_drafting_creates_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner]);

        var stored = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        stored.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.ContextMoved, outcome.Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());

        // The consume is rolled back with the effects, so the plan is still decidable.
        Assert.Equal(
            FamiliarPlanStatus.Pending,
            (await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(ProjectStatus.Archived)]
    public async Task An_inactive_project_creates_nothing(ProjectStatus status)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner]);

        var stored = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        stored.Status = status;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var outcome = await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.ProjectInactive, outcome.Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A plan id from another conversation cannot be decided from this one, so ownership does not
    /// depend on which page posted.
    /// </summary>
    [Fact]
    public async Task A_plan_from_another_conversation_is_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null]);

        var outcome = await NewService(dbContext).ApproveAsync(Guid.NewGuid(), Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.NotFound, outcome.Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- replay and decline

    /// <summary>
    /// A resubmitted approval reports what the first one created rather than a second copy, which is
    /// what makes a double-click and a refresh both harmless.
    /// </summary>
    [Fact]
    public async Task A_replayed_approval_creates_nothing_a_second_time()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner, null]);

        await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        var replay = await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.AlreadyApproved, replay.Status);
        Assert.Equal(2, replay.CreatedTaskCount);
        Assert.Equal(2, await dbContext.Tasks.AsNoTracking().CountAsync());
        Assert.Single(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Declining_creates_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [AgentSessionRole.Planner, null]);

        var outcome = await NewService(dbContext).DeclineAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.Declined, outcome.Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());

        var stored = await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync();
        Assert.Equal(FamiliarPlanStatus.Declined, stored.Status);
        Assert.NotNull(stored.DecidedUtc);
        Assert.NotEqual(plan.ConcurrencyToken, stored.ConcurrencyToken);
    }

    [Fact]
    public async Task An_approved_plan_cannot_then_be_declined()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null]);

        await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        var outcome = await NewService(dbContext).DeclineAsync(plan.ChatId, Decide(plan));

        Assert.Equal(FamiliarPlanOutcomeStatus.AlreadyApproved, outcome.Status);
        Assert.Single(await dbContext.Tasks.AsNoTracking().ToListAsync());
    }

    /// <summary>The token rotates on every transition, so a decision cannot be replayed by token.</summary>
    [Fact]
    public async Task The_token_rotates_when_a_plan_is_decided()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var plan = await SeedPlanAsync(dbContext, project, [null]);

        await NewService(dbContext).ApproveAsync(plan.ChatId, Decide(plan));

        var stored = await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync();
        Assert.NotEqual(plan.ConcurrencyToken, stored.ConcurrencyToken);
        Assert.Equal(FamiliarPlanStatus.Approved, stored.Status);
        Assert.NotNull(stored.DecidedUtc);
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarPlanApprovalService NewService(FamiliarDbContext dbContext) =>
        new(dbContext, new WorkflowDispatchService(dbContext), new TestTimeProvider(Now));

    private static FamiliarPlanDecisionRequest Decide(FamiliarPlanProposal plan) =>
        new(plan.Id,
            plan.ConcurrencyToken,
            plan.Items.Select(item => new FamiliarPlanItemDecision(item.Id, true)).ToList());

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = "Find Familiar",
            Purpose = "Seeded for FamiliarPlanApprovalTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        // Moved off zero so the revision gate is a real assertion rather than one a default satisfies.
        project.IncrementContextRevision();

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private static async Task<FamiliarPlanProposal> SeedPlanAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        IReadOnlyList<AgentSessionRole?> roles)
    {
        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Planning",
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        var turn = new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Sequence = 1,
            State = FamiliarChatTurnState.Completed,
            UserText = "plan it",
            RequestedPlan = true,
            Output = "Here is what I would do.",
            CreatedUtc = Now.UtcDateTime,
            CompletedUtc = Now.UtcDateTime
        };

        var plan = new FamiliarPlanProposal
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            TurnId = turn.Id,
            ProjectId = project.Id,
            Status = FamiliarPlanStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = project.ContextRevision,
            Summary = "A plan.",
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime,
            Items = roles
                .Select((role, position) => new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = position,
                    Title = $"Item {position}",
                    RequestedOutcome = $"Outcome {position}.",
                    Role = role,
                    IsIncluded = true
                })
                .ToList()
        };

        dbContext.FamiliarChats.Add(chat);
        dbContext.FamiliarChatTurns.Add(turn);
        dbContext.FamiliarPlanProposals.Add(plan);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return plan;
    }
}
