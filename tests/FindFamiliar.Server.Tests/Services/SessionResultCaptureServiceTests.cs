using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class SessionResultCaptureServiceTests
{
    [Theory]
    [InlineData(AgentSessionRole.Planner, ContextEntryKind.Plan)]
    [InlineData(AgentSessionRole.Implementer, ContextEntryKind.Implementation)]
    [InlineData(AgentSessionRole.Reviewer, ContextEntryKind.Review)]
    public async Task Valid_capture_creates_exactly_four_entries_maps_role_and_saves_once(
        AgentSessionRole role,
        ContextEntryKind expectedArtifactKind)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, task, session) = await SeedStartedSessionAsync(dbContext, role);
        var revisionBefore = project.ContextRevision;

        var service = new SessionResultCaptureService(dbContext);
        var outcome = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id,
            session.Id,
            "The exact prompt.",
            "A bounded raw output excerpt.",
            "A concise summary.",
            "Artifact title",
            "Artifact content."));

        Assert.Equal(SessionResultCaptureStatus.Success, outcome.Status);
        Assert.Equal(role, outcome.Role);

        var entries = dbContext.ContextEntries.Where(entry => entry.SourceSessionId == session.Id).ToList();
        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry => Assert.Equal(project.Id, entry.ProjectId));
        Assert.All(entries, entry => Assert.Equal(task.Id, entry.TaskId));
        Assert.All(entries, entry => Assert.Equal(ContextEntryState.Active, entry.State));
        Assert.Contains(entries, entry => entry.Kind == ContextEntryKind.Prompt && entry.Content == "The exact prompt.");
        Assert.Contains(entries, entry => entry.Kind == ContextEntryKind.RawOutput);
        Assert.Contains(entries, entry => entry.Kind == ContextEntryKind.Summary);
        Assert.Contains(entries, entry => entry.Kind == expectedArtifactKind && entry.Title == "Artifact title");

        var refreshedSession = dbContext.AgentSessions.Single(candidate => candidate.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Completed, refreshedSession.Status);
        Assert.NotNull(refreshedSession.CompletedUtc);

        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore + 1, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Content_fields_are_trimmed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionResultCaptureService(dbContext);
        await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id,
            session.Id,
            "  padded prompt  ",
            "  padded raw output  ",
            "  padded summary  ",
            "  padded title  ",
            "  padded artifact  "));

        var artifact = dbContext.ContextEntries.Single(entry => entry.SourceSessionId == session.Id && entry.Kind == ContextEntryKind.Plan);
        Assert.Equal("padded title", artifact.Title);
        Assert.Equal("padded artifact", artifact.Content);
    }

    [Theory]
    [InlineData(null, "A raw output.", "A summary.", "A title", "Artifact content.")]
    [InlineData("A prompt.", null, "A summary.", "A title", "Artifact content.")]
    [InlineData("A prompt.", "A raw output.", null, "A title", "Artifact content.")]
    [InlineData("A prompt.", "A raw output.", "A summary.", null, "Artifact content.")]
    [InlineData("A prompt.", "A raw output.", "A summary.", "A title", null)]
    [InlineData("   ", "A raw output.", "A summary.", "A title", "Artifact content.")]
    public async Task Missing_or_blank_required_field_fails_validation_and_writes_nothing(
        string? prompt, string? rawOutput, string? summary, string? artifactTitle, string? artifactContent)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);
        var revisionBefore = project.ContextRevision;

        var service = new SessionResultCaptureService(dbContext);
        var outcome = await service.CaptureAsync(
            new SessionResultCaptureRequest(task.Id, session.Id, prompt, rawOutput, summary, artifactTitle, artifactContent));

        Assert.Equal(SessionResultCaptureStatus.ValidationFailed, outcome.Status);
        Assert.NotEmpty(outcome.ValidationErrors!);

        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
        var refreshedSession = dbContext.AgentSessions.Single(candidate => candidate.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Started, refreshedSession.Status);
        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Oversized_field_fails_validation_and_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionResultCaptureService(dbContext);
        var outcome = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id,
            session.Id,
            "A prompt.",
            new string('x', SessionResultCaptureService.LongFieldMaxLength + 1),
            "A summary.",
            "A title",
            "Artifact content."));

        Assert.Equal(SessionResultCaptureStatus.ValidationFailed, outcome.Status);
        Assert.True(outcome.ValidationErrors!.ContainsKey("RawOutput"));
        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Unknown_session_returns_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, _) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionResultCaptureService(dbContext);
        var outcome = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id, Guid.NewGuid(), "Prompt.", "Raw output.", "Summary.", "Title", "Content."));

        Assert.Equal(SessionResultCaptureStatus.NotFound, outcome.Status);
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
            RequestedOutcome = "Seeded for SessionResultCaptureServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        dbContext.Tasks.Add(siblingTask);
        await dbContext.SaveChangesAsync();

        var service = new SessionResultCaptureService(dbContext);
        var outcome = await service.CaptureAsync(new SessionResultCaptureRequest(
            siblingTask.Id, session.Id, "Prompt.", "Raw output.", "Summary.", "Title", "Content."));

        Assert.Equal(SessionResultCaptureStatus.NotFound, outcome.Status);
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

        var service = new SessionResultCaptureService(dbContext);
        var outcome = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id, session.Id, "Prompt.", "Raw output.", "Summary.", "Title", "Content."));

        Assert.Equal(SessionResultCaptureStatus.NotStarted, outcome.Status);
        Assert.Equal(0, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Replaying_after_success_is_rejected_and_creates_no_duplicates()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (project, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);

        var service = new SessionResultCaptureService(dbContext);
        var first = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id, session.Id, "Prompt.", "Raw output.", "Summary.", "Title", "Content."));
        Assert.Equal(SessionResultCaptureStatus.Success, first.Status);

        var revisionAfterFirst = dbContext.Projects.Single(candidate => candidate.Id == project.Id).ContextRevision;

        var replay = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id, session.Id, "Prompt.", "Raw output.", "Summary.", "Replay title", "Replay content."));

        Assert.Equal(SessionResultCaptureStatus.NotStarted, replay.Status);
        Assert.Equal(4, dbContext.ContextEntries.Count(entry => entry.SourceSessionId == session.Id));
        var refreshedProject = dbContext.Projects.Single(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionAfterFirst, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Stale_or_expired_claim_generation_cannot_capture_a_result()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var (_, task, session) = await SeedStartedSessionAsync(dbContext, AgentSessionRole.Planner);
        var activeClaimId = Guid.NewGuid();
        session.ClaimId = activeClaimId;
        session.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(5);
        await dbContext.SaveChangesAsync();

        var service = new SessionResultCaptureService(dbContext);
        var stale = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id,
            session.Id,
            "Prompt.",
            "Raw output.",
            "Summary.",
            "Title",
            "Content.",
            Guid.NewGuid(),
            RequireClaimOwnership: true));

        Assert.Equal(SessionResultCaptureStatus.ClaimLost, stale.Status);
        Assert.Empty(dbContext.ContextEntries.Where(entry => entry.SourceSessionId == session.Id));

        session.ClaimExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        await dbContext.SaveChangesAsync();
        var expired = await service.CaptureAsync(new SessionResultCaptureRequest(
            task.Id,
            session.Id,
            "Prompt.",
            "Raw output.",
            "Summary.",
            "Title",
            "Content.",
            activeClaimId,
            RequireClaimOwnership: true));

        Assert.Equal(SessionResultCaptureStatus.ClaimLost, expired.Status);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
    }

    [Fact]
    public async Task Concurrent_result_submissions_commit_exactly_once()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seedContext = await database.CreateContextAsync();
        var (_, task, session) = await SeedStartedSessionAsync(seedContext, AgentSessionRole.Planner);
        var claimId = Guid.NewGuid();
        session.ClaimId = claimId;
        session.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(5);
        await seedContext.SaveChangesAsync();

        await using var firstContext = await database.CreateContextAsync();
        await using var secondContext = await database.CreateContextAsync();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<SessionResultCaptureOutcome> CaptureAsync(Data.FamiliarDbContext context, string title)
        {
            await barrier.Task;
            return await new SessionResultCaptureService(context).CaptureAsync(new SessionResultCaptureRequest(
                task.Id,
                session.Id,
                "Prompt.",
                "Raw output.",
                "Summary.",
                title,
                "Content.",
                claimId,
                RequireClaimOwnership: true));
        }

        var first = CaptureAsync(firstContext, "First");
        var second = CaptureAsync(secondContext, "Second");
        barrier.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, outcome => outcome.Status == SessionResultCaptureStatus.Success);
        Assert.Single(
            outcomes,
            outcome => outcome.Status is SessionResultCaptureStatus.ClaimLost or SessionResultCaptureStatus.NotStarted);

        await using var verifyContext = await database.CreateContextAsync();
        Assert.Equal(4, await verifyContext.ContextEntries.CountAsync(entry => entry.SourceSessionId == session.Id));
        Assert.Equal(
            AgentSessionStatus.Completed,
            (await verifyContext.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id)).Status);
    }

    private static async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(
        Data.FamiliarDbContext dbContext, AgentSessionRole role)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionResultCaptureServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionResultCaptureServiceTests.",
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
