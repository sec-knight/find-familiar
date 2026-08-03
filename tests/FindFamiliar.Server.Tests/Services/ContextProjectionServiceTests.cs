using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class ContextProjectionServiceTests
{
    [Fact]
    public async Task Projection_filters_separates_orders_and_carries_provenance()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var targetProject = NewProject("Target project");
        var otherProject = NewProject("Other project");

        var targetTask = NewTask(targetProject.Id, "Target task");
        var siblingTask = NewTask(targetProject.Id, "Sibling task");

        var now = DateTime.UtcNow;

        var activeProjectEntry = NewEntry(targetProject.Id, taskId: null, ContextEntryState.Active, "Active project entry", now.AddMinutes(-30));
        var inactiveProjectEntry = NewEntry(targetProject.Id, taskId: null, ContextEntryState.Superseded, "Inactive project entry", now.AddMinutes(-29));
        var activeTargetTaskEntry = NewEntry(targetProject.Id, targetTask.Id, ContextEntryState.Active, "Active target-task entry", now.AddMinutes(-20));
        var siblingTaskEntry = NewEntry(targetProject.Id, siblingTask.Id, ContextEntryState.Active, "Sibling task entry", now.AddMinutes(-10));
        var otherProjectEntry = NewEntry(otherProject.Id, taskId: null, ContextEntryState.Active, "Other project entry", now.AddMinutes(-5));

        var earlierSession = NewSession(targetTask.Id, AgentSessionRole.Planner, now.AddHours(-2));
        var laterSession = NewSession(targetTask.Id, AgentSessionRole.Implementer, now.AddHours(-1));
        var siblingTaskSession = NewSession(siblingTask.Id, AgentSessionRole.Reviewer, now.AddHours(-1));

        activeTargetTaskEntry.SourceSessionId = earlierSession.Id;

        dbContext.AddRange(
            targetProject, otherProject, targetTask, siblingTask,
            activeProjectEntry, inactiveProjectEntry, activeTargetTaskEntry, siblingTaskEntry, otherProjectEntry,
            earlierSession, laterSession, siblingTaskSession);
        await dbContext.SaveChangesAsync();

        var service = new ContextProjectionService(dbContext);
        var document = await service.GetTaskContextAsync(targetTask.Id);

        Assert.NotNull(document);
        Assert.Single(document.ProjectEntries);
        Assert.Equal(activeProjectEntry.Id, document.ProjectEntries[0].Id);

        Assert.Single(document.TaskEntries);
        Assert.Equal(activeTargetTaskEntry.Id, document.TaskEntries[0].Id);
        Assert.Equal(earlierSession.Id, document.TaskEntries[0].SourceSessionId);

        Assert.Equal(2, document.Sessions.Count);
        Assert.Equal(earlierSession.Id, document.Sessions[0].Id);
        Assert.Equal(laterSession.Id, document.Sessions[1].Id);
        Assert.All(document.Sessions, session => Assert.NotEqual(siblingTaskSession.Id, session.Id));
    }

    [Fact]
    public async Task Entries_and_sessions_are_ordered_chronologically()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Ordering project");
        var task = NewTask(project.Id, "Ordering task");

        var now = DateTime.UtcNow;
        var laterEntry = NewEntry(project.Id, task.Id, ContextEntryState.Active, "Later entry", now.AddMinutes(-1));
        var earlierEntry = NewEntry(project.Id, task.Id, ContextEntryState.Active, "Earlier entry", now.AddMinutes(-10));

        var laterSession = NewSession(task.Id, AgentSessionRole.Reviewer, now.AddMinutes(-1));
        var earlierSession = NewSession(task.Id, AgentSessionRole.Planner, now.AddMinutes(-10));

        dbContext.AddRange(project, task, laterEntry, earlierEntry, laterSession, earlierSession);
        await dbContext.SaveChangesAsync();

        var service = new ContextProjectionService(dbContext);
        var document = await service.GetTaskContextAsync(task.Id);

        Assert.NotNull(document);
        Assert.Equal([earlierEntry.Id, laterEntry.Id], document.TaskEntries.Select(entry => entry.Id));
        Assert.Equal([earlierSession.Id, laterSession.Id], document.Sessions.Select(session => session.Id));
    }

    [Fact]
    public async Task Unknown_task_id_returns_null()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var service = new ContextProjectionService(dbContext);
        var document = await service.GetTaskContextAsync(Guid.NewGuid());

        Assert.Null(document);
    }

    private static FamiliarProject NewProject(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{name} {Guid.NewGuid():N}",
        Purpose = "Seeded for ContextProjectionServiceTests.",
        Status = ProjectStatus.Active,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static FamiliarTask NewTask(Guid projectId, string title) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Title = title,
        RequestedOutcome = "Seeded for ContextProjectionServiceTests.",
        Status = TaskStatus.Ready,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static ContextEntry NewEntry(Guid projectId, Guid? taskId, ContextEntryState state, string title, DateTime createdUtc) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        TaskId = taskId,
        Kind = ContextEntryKind.Goal,
        Title = title,
        Content = $"Content for {title}.",
        State = state,
        CreatedUtc = createdUtc
    };

    private static AgentSession NewSession(Guid taskId, AgentSessionRole role, DateTime startedUtc) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        Role = role,
        Status = AgentSessionStatus.Started,
        ContextRevisionRead = 0,
        StartedUtc = startedUtc
    };
}
