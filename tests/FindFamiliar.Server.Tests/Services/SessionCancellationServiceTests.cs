using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class SessionCancellationServiceTests
{
    [Fact]
    public async Task Valid_cancellation_adds_one_handoff_entry_and_saves_once()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Implementer);
        var revisionBefore = project.ContextRevision;

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var outcome = await service.CancelAsync(new SessionCancellationRequest(task.Id, session.Id, "Adapter failed before submission."));

        Assert.Equal(SessionCancellationStatus.Success, outcome.Status);
        Assert.Equal(AgentSessionRole.Implementer, outcome.Role);

        var entries = dbContext.ContextEntries.Where(entry => entry.SourceSessionId == session.Id).ToList();
        Assert.Single(entries);
        Assert.Equal(ContextEntryKind.Handoff, entries[0].Kind);
        Assert.Equal("Adapter failed before submission.", entries[0].Content);
        Assert.Equal(project.Id, entries[0].ProjectId);
        Assert.Equal(task.Id, entries[0].TaskId);

        var refreshedSession = dbContext.AgentSessions.Single(candidate => candidate.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Cancelled, refreshedSession.Status);
        Assert.NotNull(refreshedSession.CompletedUtc);

        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore + 1, refreshedProject.ContextRevision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_reason_fails_validation_and_writes_nothing(string? reason)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);
        var revisionBefore = project.ContextRevision;

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var outcome = await service.CancelAsync(new SessionCancellationRequest(task.Id, session.Id, reason));

        Assert.Equal(SessionCancellationStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
        var refreshedSession = dbContext.AgentSessions.Single(candidate => candidate.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Started, refreshedSession.Status);
        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Oversized_reason_fails_validation_and_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var outcome = await service.CancelAsync(new SessionCancellationRequest(
            task.Id, session.Id, new string('x', SessionCancellationService.ReasonMaxLength + 1)));

        Assert.Equal(SessionCancellationStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Unknown_session_returns_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, _) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var outcome = await service.CancelAsync(new SessionCancellationRequest(task.Id, Guid.NewGuid(), "Reason."));

        Assert.Equal(SessionCancellationStatus.NotFound, outcome.Status);
    }

    [Fact]
    public async Task Session_belonging_to_sibling_task_returns_not_found_and_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, _, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var siblingTask = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Sibling task",
            RequestedOutcome = "Seeded for SessionCancellationServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        dbContext.Tasks.Add(siblingTask);
        await dbContext.SaveChangesAsync();

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var outcome = await service.CancelAsync(new SessionCancellationRequest(siblingTask.Id, session.Id, "Reason."));

        Assert.Equal(SessionCancellationStatus.NotFound, outcome.Status);
        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Non_started_session_is_rejected_and_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        session.Status = AgentSessionStatus.Completed;
        session.CompletedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        var revisionBefore = project.ContextRevision;

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var outcome = await service.CancelAsync(new SessionCancellationRequest(task.Id, session.Id, "Reason."));

        Assert.Equal(SessionCancellationStatus.NotStarted, outcome.Status);
        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Replaying_after_success_is_rejected_and_creates_no_duplicate_handoff()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var first = await service.CancelAsync(new SessionCancellationRequest(task.Id, session.Id, "First cancellation."));
        Assert.Equal(SessionCancellationStatus.Success, first.Status);

        var revisionAfterFirst = dbContext.Projects.Single(candidate => candidate.Id == project.Id).ContextRevision;

        var replay = await service.CancelAsync(new SessionCancellationRequest(task.Id, session.Id, "Replay cancellation."));

        Assert.Equal(SessionCancellationStatus.NotStarted, replay.Status);
        Assert.Single(dbContext.ContextEntries.Where(entry => entry.SourceSessionId == session.Id));
        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionAfterFirst, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Stale_claim_generation_cannot_cancel_the_current_owner()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);
        session.ClaimId = Guid.NewGuid();
        session.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(5);
        await dbContext.SaveChangesAsync();

        var service = new SessionCancellationService(dbContext, new SessionHandoffService(dbContext));
        var stale = await service.CancelAsync(new SessionCancellationRequest(
            task.Id,
            session.Id,
            "Stale cancellation.",
            Guid.NewGuid(),
            RequireClaimOwnership: true));

        Assert.Equal(SessionCancellationStatus.ClaimLost, stale.Status);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Empty(dbContext.ContextEntries.Where(entry => entry.SourceSessionId == session.Id));
    }

    private static async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(
        Data.FamiliarDbContext dbContext, AgentSessionRole role)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionCancellationServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionCancellationServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        };

        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }
}
