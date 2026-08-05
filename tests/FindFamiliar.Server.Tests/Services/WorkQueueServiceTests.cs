using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class WorkQueueServiceTests
{
    [Fact]
    public async Task Task_with_no_sessions_recommends_starting_a_planner()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("No sessions");
        var task = NewTask(project.Id, "No sessions task");
        dbContext.AddRange(project, task);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.StartPlanner, item.ActionKind);
        Assert.Null(item.ActiveSessionId);
        Assert.Equal(0, item.StartedSessionCount);
    }

    [Fact]
    public async Task Task_with_one_Started_session_recommends_continuing_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("One started");
        var task = NewTask(project.Id, "One started task");
        var session = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Started, DateTime.UtcNow.AddMinutes(-5));
        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.ContinueSession, item.ActionKind);
        Assert.Equal(session.Id, item.ActiveSessionId);
        Assert.Equal(AgentSessionRole.Implementer, item.ActiveSessionRole);
        Assert.Equal(1, item.StartedSessionCount);
    }

    [Fact]
    public async Task Task_with_multiple_Started_sessions_needs_attention()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        // IX_AgentSessions_TaskId_Started makes this state unreachable through the application, which
        // is the point of ADR-0010. It remains reachable in a database restored from before that
        // migration, so dropping the index here reproduces that database faithfully — NeedsAttention
        // exists precisely to surface data the application can no longer create. Safe to drop because
        // TemporarySqliteDatabase is per-test and discarded afterwards.
        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_AgentSessions_TaskId_Started\";");

        var project = NewProject("Multiple started");
        var task = NewTask(project.Id, "Multiple started task");
        var sessionA = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started, DateTime.UtcNow.AddMinutes(-10));
        var sessionB = NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Started, DateTime.UtcNow.AddMinutes(-5));
        dbContext.AddRange(project, task, sessionA, sessionB);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.NeedsAttention, item.ActionKind);
        Assert.Null(item.ActiveSessionId);
        Assert.Equal(2, item.StartedSessionCount);
    }

    [Fact]
    public async Task Task_whose_latest_terminal_session_was_Cancelled_recommends_retrying_that_role()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Cancelled retry");
        var task = NewTask(project.Id, "Cancelled retry task");
        var cancelled = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Cancelled, DateTime.UtcNow.AddMinutes(-10));
        cancelled.CompletedUtc = DateTime.UtcNow.AddMinutes(-5);
        dbContext.AddRange(project, task, cancelled);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.RetryRole, item.ActionKind);
        Assert.Contains("Implementer", item.ActionLabel);
    }

    [Fact]
    public async Task Completed_Planner_recommends_starting_an_implementer()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Completed planner");
        var task = NewTask(project.Id, "Completed planner task");
        var planner = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed, DateTime.UtcNow.AddMinutes(-10));
        planner.CompletedUtc = DateTime.UtcNow.AddMinutes(-5);
        dbContext.AddRange(project, task, planner);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.StartImplementer, item.ActionKind);
    }

    [Fact]
    public async Task Completed_Implementer_recommends_starting_a_reviewer()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Completed implementer");
        var task = NewTask(project.Id, "Completed implementer task");
        var planner = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed, DateTime.UtcNow.AddMinutes(-20));
        planner.CompletedUtc = DateTime.UtcNow.AddMinutes(-15);
        var implementer = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Completed, DateTime.UtcNow.AddMinutes(-10));
        implementer.CompletedUtc = DateTime.UtcNow.AddMinutes(-5);
        dbContext.AddRange(project, task, planner, implementer);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.StartReviewer, item.ActionKind);
    }

    [Fact]
    public async Task Completed_Reviewer_requires_a_human_decision()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Completed reviewer");
        var task = NewTask(project.Id, "Completed reviewer task");
        var reviewer = NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Completed, DateTime.UtcNow.AddMinutes(-5));
        reviewer.CompletedUtc = DateTime.UtcNow;
        dbContext.AddRange(project, task, reviewer);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.HumanDecision, item.ActionKind);
    }

    [Fact]
    public async Task Completed_tasks_are_excluded_from_the_active_queue()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Completed task");
        var task = NewTask(project.Id, "Completed task", TaskStatus.Completed);
        dbContext.AddRange(project, task);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        Assert.Empty(items);
    }

    [Fact]
    public async Task Cancelled_sessions_do_not_count_as_completed_role_progress()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Cancelled not progress");
        var task = NewTask(project.Id, "Cancelled not progress task");
        var plannerCompleted = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed, DateTime.UtcNow.AddMinutes(-20));
        plannerCompleted.CompletedUtc = DateTime.UtcNow.AddMinutes(-15);
        var implementerCancelled = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Cancelled, DateTime.UtcNow.AddMinutes(-10));
        implementerCancelled.CompletedUtc = DateTime.UtcNow.AddMinutes(-5);
        dbContext.AddRange(project, task, plannerCompleted, implementerCancelled);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        var item = Assert.Single(items);
        Assert.Equal(WorkQueueActionKind.RetryRole, item.ActionKind);
        Assert.Contains("Implementer", item.ActionLabel);
    }

    [Fact]
    public async Task Queue_orders_by_most_recently_updated_task_then_stable_id()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = NewProject("Ordering");
        var older = NewTask(project.Id, "Older task");
        older.UpdatedUtc = DateTime.UtcNow.AddHours(-2);
        var newer = NewTask(project.Id, "Newer task");
        newer.UpdatedUtc = DateTime.UtcNow.AddMinutes(-1);
        dbContext.AddRange(project, older, newer);
        await dbContext.SaveChangesAsync();

        var service = new WorkQueueService(dbContext);
        var items = await service.GetActiveQueueAsync();

        Assert.Equal([newer.Id, older.Id], items.Select(item => item.TaskId));
    }

    private static FamiliarProject NewProject(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{name} {Guid.NewGuid():N}",
        Purpose = "Seeded for WorkQueueServiceTests.",
        Status = ProjectStatus.Active,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static FamiliarTask NewTask(Guid projectId, string title, TaskStatus status = TaskStatus.Ready) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Title = title,
        RequestedOutcome = "Seeded for WorkQueueServiceTests.",
        Status = status,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static AgentSession NewSession(Guid taskId, AgentSessionRole role, AgentSessionStatus status, DateTime startedUtc) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        Role = role,
        Status = status,
        ContextRevisionRead = 0,
        StartedUtc = startedUtc
    };
}
