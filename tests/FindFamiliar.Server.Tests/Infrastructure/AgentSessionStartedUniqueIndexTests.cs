using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// IX_AgentSessions_TaskId_Started, asserted against the database itself rather than through any
/// service (ADR-0010).
///
/// This index is what actually guarantees one Started session per task across every write path. Its
/// filter is the SQL literal <c>"Status" = 'Started'</c>, which matches only because
/// <c>AgentSession.Status</c> is stored via <c>HasConversion&lt;string&gt;()</c>. If that conversion
/// were ever removed the filter would silently stop matching, the index would quietly cover nothing,
/// and every service check that calls itself a "pre-check" would become the only enforcement left.
/// These tests fail loudly in that case.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class AgentSessionStartedUniqueIndexTests
{
    [Fact]
    public async Task A_second_started_session_on_one_task_is_rejected_by_the_database()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task) = await SeedTaskAsync(dbContext);
        await InsertSessionAsync(dbContext, task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started);

        // Raw SQL deliberately: this asserts the database's own guarantee, not a service's.
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertSessionAsync(dbContext, task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Started));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData(AgentSessionStatus.Completed)]
    [InlineData(AgentSessionStatus.Cancelled)]
    public async Task A_terminal_session_alongside_a_started_one_is_allowed(AgentSessionStatus terminalStatus)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (_, task) = await SeedTaskAsync(dbContext);

        // The ordinary shape of a task that has been worked on: finished attempts, one live session.
        await InsertSessionAsync(dbContext, task.Id, AgentSessionRole.Planner, terminalStatus);
        await InsertSessionAsync(dbContext, task.Id, AgentSessionRole.Implementer, terminalStatus);
        await InsertSessionAsync(dbContext, task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Started);

        Assert.Equal(3, await dbContext.AgentSessions.CountAsync(session => session.TaskId == task.Id));
    }

    [Fact]
    public async Task Started_sessions_on_different_tasks_are_allowed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (project, first) = await SeedTaskAsync(dbContext);
        var second = await SeedTaskAsync(dbContext, project);

        await InsertSessionAsync(dbContext, first.Id, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await InsertSessionAsync(dbContext, second.Task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started);

        Assert.Equal(2, await dbContext.AgentSessions.CountAsync(session => session.Status == AgentSessionStatus.Started));
    }

    /// <summary>SQLITE_CONSTRAINT_UNIQUE.</summary>
    private const int SqliteConstraintUnique = 2067;

    /// <summary>
    /// Inserts through raw SQL on purpose. Going around EF is what makes this an assertion about the
    /// database's own guarantee rather than about any service's checks. Values are passed as typed
    /// parameters so EF maps them to the same storage shape it writes itself.
    /// </summary>
    private static Task InsertSessionAsync(
        FamiliarDbContext dbContext,
        Guid taskId,
        AgentSessionRole role,
        AgentSessionStatus status) =>
        status == AgentSessionStatus.Started
            ? dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "AgentSessions"
                    ("Id", "TaskId", "Role", "Status", "ContextRevisionRead", "StartedUtc", "CompletedUtc")
                VALUES ({Guid.NewGuid()}, {taskId}, {role.ToString()}, {status.ToString()}, 0, {DateTime.UtcNow}, NULL);
                """)
            : dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "AgentSessions"
                    ("Id", "TaskId", "Role", "Status", "ContextRevisionRead", "StartedUtc", "CompletedUtc")
                VALUES ({Guid.NewGuid()}, {taskId}, {role.ToString()}, {status.ToString()}, 0, {DateTime.UtcNow}, {DateTime.UtcNow});
                """);

    private static async Task<(FamiliarProject Project, FamiliarTask Task)> SeedTaskAsync(
        FamiliarDbContext dbContext,
        FamiliarProject? existingProject = null)
    {
        var project = existingProject ?? new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Index project {Guid.NewGuid():N}",
            Purpose = "Seeded for AgentSessionStartedUniqueIndexTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Index task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for AgentSessionStartedUniqueIndexTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        if (existingProject is null)
        {
            dbContext.Add(project);
        }

        dbContext.Add(task);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (project, task);
    }
}
