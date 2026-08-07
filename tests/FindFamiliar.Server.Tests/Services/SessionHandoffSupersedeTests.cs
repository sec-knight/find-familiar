using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Retiring a decision that something newer has settled.
///
/// This exists because of a defect found by using the system rather than by testing it. Closing a
/// task by hand left its pending handoff Pending forever: it could not be approved — the approval
/// service refuses a Completed task — and nothing retired it, so it sat in every "waiting for you"
/// list being asked about and never answerable. Two of them had been doing exactly that for a day
/// before the conversation started surfacing decisions and made them visible.
///
/// <see cref="SessionHandoffStatus.Superseded"/> is precisely the state for it, and was already
/// documented as "a newer terminal event on the same task replaced this decision point". Closing the
/// task is such an event. The rule was right; nothing was calling it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class SessionHandoffSupersedeTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_pending_handoff_is_retired()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (taskId, handoffId) = await SeedAsync(dbContext);

        var retired = await new SessionHandoffService(dbContext).SupersedePendingAsync(taskId, Now);

        Assert.Equal(1, retired);

        var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoffId);
        Assert.Equal(SessionHandoffStatus.Superseded, handoff.Status);
        Assert.Equal(Now, handoff.UpdatedUtc);

        // Not a human decision, so it is not recorded as one. DecidedUtc stays null: nobody decided
        // this, it stopped applying.
        Assert.Null(handoff.DecidedUtc);
    }

    /// <summary>
    /// A decision a human already made is not rewritten. Superseding a Declined handoff would erase
    /// the record that somebody chose, and the transcript would stop matching what happened.
    /// </summary>
    [Theory]
    [InlineData(SessionHandoffStatus.Approved)]
    [InlineData(SessionHandoffStatus.Declined)]
    public async Task A_decided_handoff_is_left_alone(SessionHandoffStatus decided)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (taskId, handoffId) = await SeedAsync(dbContext, decided);

        Assert.Equal(0, await new SessionHandoffService(dbContext).SupersedePendingAsync(taskId, Now));

        Assert.Equal(
            decided,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoffId)).Status);
    }

    /// <summary>Another task's decision is not this task's business.</summary>
    [Fact]
    public async Task A_handoff_on_another_task_is_untouched()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (_, closing) = await SeedAsync(dbContext, project: project);
        var (_, other) = await SeedAsync(dbContext, project: project);

        await new SessionHandoffService(dbContext).SupersedePendingAsync(
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == closing)).TaskId,
            Now);

        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == other)).Status);
    }

    [Fact]
    public async Task Retiring_nothing_is_not_an_error()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        Assert.Equal(0, await new SessionHandoffService(dbContext).SupersedePendingAsync(Guid.NewGuid(), Now));
    }

    /// <summary>
    /// The whole point, stated as the loop sees it: after a task closes, nothing is waiting on a
    /// human about it.
    /// </summary>
    [Fact]
    public async Task A_closed_task_leaves_nothing_waiting_on_a_human()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (taskId, _) = await SeedAsync(dbContext);

        await new SessionHandoffService(dbContext).SupersedePendingAsync(taskId, Now);

        Assert.Empty(await dbContext.SessionHandoffs
            .AsNoTracking()
            .Where(handoff => handoff.TaskId == taskId && handoff.Status == SessionHandoffStatus.Pending)
            .ToListAsync());
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Supersede project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionHandoffSupersedeTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private static async Task<(Guid TaskId, Guid HandoffId)> SeedAsync(
        FamiliarDbContext dbContext,
        SessionHandoffStatus status = SessionHandoffStatus.Pending,
        FamiliarProject? project = null)
    {
        project ??= await SeedProjectAsync(dbContext);

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "A task somebody finished by hand",
            RequestedOutcome = "An outcome.",
            Status = FindFamiliar.Server.Domain.TaskStatus.InProgress,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Completed,
            ContextRevisionRead = 0,
            StartedUtc = Now,
            CompletedUtc = Now
        };

        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = session.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Implementer,
            Kind = SessionHandoffKind.NextRole,
            Status = status,
            ObservedContextRevision = 0,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Tasks.Add(task);
        dbContext.AgentSessions.Add(session);
        dbContext.SessionHandoffs.Add(handoff);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (task.Id, handoff.Id);
    }
}
