using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// What is waiting on a human, gathered for the conversation.
///
/// This is the half of the loop that was missing. Sessions have produced results since Sprint 9 and
/// handoffs have been human-gated since ADR-0010, but the only place either surfaced was a task page —
/// so a conversation could start work and then go silent about it. A decision nobody is told about is
/// indistinguishable from a system that stopped.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarOpenDecisionsTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_pending_handoff_is_reported_with_what_the_session_produced()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Re-specify the anchor task");
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);

        await SeedResultAsync(dbContext, project, task, session, "Plan: three steps, smallest first");
        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);

        var decision = Assert.Single(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());

        Assert.Equal(task.Id, decision.TaskId);
        Assert.Equal("Re-specify the anchor task", decision.TaskTitle);
        Assert.Equal(AgentSessionRole.Planner, decision.SourceRole);
        Assert.Equal(AgentSessionRole.Implementer, decision.ProposedRole);

        // The result is what makes the decision answerable in place: "approve the Implementer" is not
        // a question anybody can answer without seeing what the Planner wrote.
        Assert.Equal("Plan: three steps, smallest first", decision.LastResultTitle);

        Assert.Contains("starts one Implementer session", decision.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session that produced nothing still surfaces its decision. Hiding it because there is no
    /// result to show would leave the task silently waiting forever.
    /// </summary>
    [Fact]
    public async Task A_handoff_with_no_recorded_result_is_still_reported()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project);
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);

        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);

        var decision = Assert.Single(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());

        Assert.Null(decision.LastResultTitle);
        Assert.Null(decision.LastResultEntryId);
    }

    /// <summary>A retry says so, because "another Planner" and "the next role" are different news.</summary>
    [Fact]
    public async Task A_retry_says_the_last_session_did_not_complete()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project);
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner, AgentSessionStatus.Cancelled);

        await SeedHandoffAsync(
            dbContext, task, session, AgentSessionRole.Planner,
            SessionHandoffKind.RetrySameRole, AgentSessionStatus.Cancelled);

        var decision = Assert.Single(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());

        Assert.Equal(SessionHandoffKind.RetrySameRole, decision.Kind);
        Assert.Contains("did not complete", decision.Consequence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SessionHandoffStatus.Approved)]
    [InlineData(SessionHandoffStatus.Declined)]
    [InlineData(SessionHandoffStatus.Superseded)]
    public async Task A_decided_handoff_is_not_waiting_on_anybody(SessionHandoffStatus status)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project);
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);

        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer, status: status);

        Assert.Empty(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());
    }

    /// <summary>
    /// A decision in a sensitive project never leaves the database, and neither does its task title.
    /// </summary>
    [Fact]
    public async Task A_decision_in_a_sensitive_project_is_withheld()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, isSensitive: true);
        var task = await SeedTaskAsync(dbContext, project, "A distinctive task title");
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);

        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);

        Assert.Empty(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());
    }

    /// <summary>
    /// A result flagged sensitive after it was written stops being shown, and the decision carries no
    /// result rather than the decision disappearing — the step is still waiting either way.
    /// </summary>
    [Fact]
    public async Task A_sensitive_result_is_withheld_but_its_decision_is_not()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project);
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);

        await SeedResultAsync(dbContext, project, task, session, "A distinctive result title", isSensitive: true);
        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);

        var decision = Assert.Single(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());

        Assert.Null(decision.LastResultTitle);
    }

    [Fact]
    public async Task A_decision_in_an_archived_project_is_not_waiting_on_anybody()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project);
        var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);
        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);

        var stored = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        stored.Status = ProjectStatus.Archived;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Empty(await new FamiliarOpenDecisionsService(dbContext).ReadAsync());
    }

    /// <summary>
    /// Oldest first: a decision that has waited longest is the one most likely to be holding something
    /// up, and the one a person is most likely to have forgotten.
    /// </summary>
    [Fact]
    public async Task Decisions_are_oldest_first_and_capped()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        for (var index = 0; index < FamiliarOpenDecisionsService.MaxDecisions + 3; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2}");
            var session = await SeedSessionAsync(dbContext, task, project, AgentSessionRole.Planner);
            await SeedHandoffAsync(
                dbContext, task, session, AgentSessionRole.Implementer,
                createdUtc: Now.AddMinutes(index));
        }

        var decisions = await new FamiliarOpenDecisionsService(dbContext).ReadAsync();

        Assert.Equal(FamiliarOpenDecisionsService.MaxDecisions, decisions.Count);
        Assert.Equal("Task 00", decisions[0].TaskTitle);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext, bool isSensitive = false)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = "Find Familiar",
            Purpose = "Seeded for FamiliarOpenDecisionsTests.",
            Status = ProjectStatus.Active,
            IsSensitive = isSensitive,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private static async Task<FamiliarTask> SeedTaskAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        string title = "A task")
    {
        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = title,
            RequestedOutcome = "An outcome.",
            Status = FindFamiliar.Server.Domain.TaskStatus.InProgress,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return task;
    }

    private static async Task<AgentSession> SeedSessionAsync(
        FamiliarDbContext dbContext,
        FamiliarTask task,
        FamiliarProject project,
        AgentSessionRole role,
        AgentSessionStatus status = AgentSessionStatus.Completed)
    {
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = status,
            ContextRevisionRead = project.ContextRevision,
            StartedUtc = Now,
            CompletedUtc = Now
        };

        dbContext.AgentSessions.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return session;
    }

    private static async Task SeedResultAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        FamiliarTask task,
        AgentSession session,
        string title,
        bool isSensitive = false)
    {
        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            TaskId = task.Id,
            SourceSessionId = session.Id,
            Kind = ContextEntryKind.Plan,
            Title = title,
            Content = "The body of what the session produced.",
            State = ContextEntryState.Active,
            IsSensitive = isSensitive,
            CreatedUtc = Now
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    private static async Task SeedHandoffAsync(
        FamiliarDbContext dbContext,
        FamiliarTask task,
        AgentSession session,
        AgentSessionRole proposedRole,
        SessionHandoffKind kind = SessionHandoffKind.NextRole,
        AgentSessionStatus sourceOutcome = AgentSessionStatus.Completed,
        SessionHandoffStatus status = SessionHandoffStatus.Pending,
        DateTime? createdUtc = null)
    {
        dbContext.SessionHandoffs.Add(new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = session.Id,
            SourceOutcome = sourceOutcome,
            ProposedRole = proposedRole,
            Kind = kind,
            Status = status,
            ObservedContextRevision = 0,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = createdUtc ?? Now,
            UpdatedUtc = createdUtc ?? Now
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }
}
