using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// The normalization the Sprint 09 migration performs before it can create
/// IX_AgentSessions_TaskId_Started (ADR-0010).
///
/// ADR-0005 tolerated more than one Started session on a task and surfaced it in the work queue
/// rather than repairing it. A unique index cannot be created over that data, and the application
/// migrates at startup, so a violating database would fail to boot. This is the test that proves the
/// repair happens, keeps the record, and produces rows EF can still read — a wrong DateTime TEXT
/// format here would be invisible until a page threw in production.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class SessionHandoffMigrationTests
{
    private const string PreviousMigration = "20260804222955_ConversationalWorkIntake";

    [Fact]
    public async Task Duplicate_started_sessions_are_normalized_before_the_index_is_created()
    {
        using var database = new TemporarySqliteDatabase();

        Guid projectId;
        Guid taskId;
        Guid survivorId;
        Guid loserId;

        await using (var before = await database.CreateContextAtMigrationAsync(PreviousMigration))
        {
            // Inserted with explicit SQL, not through EF: this database is migrated only as
            // far as PreviousMigration, and the current model describes columns that schema
            // does not have.
            var seededProjectId = Guid.NewGuid();
            await LegacyRowSeeder.InsertProjectAsync(
                before,
                seededProjectId,
                $"Migration project {Guid.NewGuid():N}",
                "Seeded for SessionHandoffMigrationTests.",
                ProjectStatus.Active,
                contextRevision: 0,
                DateTime.UtcNow);

            var task = new FamiliarTask
            {
                Id = Guid.NewGuid(),
                ProjectId = seededProjectId,
                Title = "Migration task",
                RequestedOutcome = "Seeded for SessionHandoffMigrationTests.",
                Status = TaskStatus.Ready,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            // The exact state ADR-0005 tolerated, and the reason this migration cannot simply add an
            // index: two Started sessions on one task.
            var loser = NewStartedSession(task.Id, AgentSessionRole.Planner, DateTime.UtcNow.AddHours(-2));
            var survivor = NewStartedSession(task.Id, AgentSessionRole.Implementer, DateTime.UtcNow.AddHours(-1));

            before.AddRange(task, loser, survivor);
            await before.SaveChangesAsync();

            projectId = seededProjectId;
            taskId = task.Id;
            survivorId = survivor.Id;
            loserId = loser.Id;
        }

        var revisionBefore = 0;
        await using (var probe = await database.CreateContextAtMigrationAsync(PreviousMigration))
        {
            revisionBefore = await probe.Projects
                .AsNoTracking()
                .Where(project => project.Id == projectId)
                .Select(project => project.ContextRevision)
                .SingleAsync();
        }

        // Apply the Sprint 09 migration to that database.
        await using var after = await database.CreateContextAsync();

        // The most recently started session survives; the rest are cancelled.
        var sessions = await after.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == taskId)
            .ToListAsync();

        Assert.Equal(2, sessions.Count);

        var survivorRow = Assert.Single(sessions, session => session.Id == survivorId);
        Assert.Equal(AgentSessionStatus.Started, survivorRow.Status);
        Assert.Null(survivorRow.CompletedUtc);

        var loserRow = Assert.Single(sessions, session => session.Id == loserId);
        Assert.Equal(AgentSessionStatus.Cancelled, loserRow.Status);

        // Reading CompletedUtc back through EF is the real assertion here: it proves the timestamp
        // the migration wrote is in the TEXT shape the provider parses.
        Assert.NotNull(loserRow.CompletedUtc);

        // The cancellation is recorded, not silently applied.
        var entry = Assert.Single(await after.ContextEntries
            .AsNoTracking()
            .Where(candidate => candidate.SourceSessionId == loserId)
            .ToListAsync());
        Assert.Equal(ContextEntryKind.Handoff, entry.Kind);
        Assert.Equal("Planner session cancelled", entry.Title);
        Assert.Equal(taskId, entry.TaskId);
        Assert.Equal(projectId, entry.ProjectId);
        Assert.Contains("more than one Started session", entry.Content, StringComparison.Ordinal);

        // No entry was written for the survivor: the insert and the update share one predicate.
        Assert.Empty(await after.ContextEntries
            .AsNoTracking()
            .Where(candidate => candidate.SourceSessionId == survivorId)
            .ToListAsync());

        // Context genuinely changed under the surviving session, so its packet must say so.
        var revisionAfter = await after.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.ContextRevision)
            .SingleAsync();
        Assert.Equal(revisionBefore + 1, revisionAfter);
        Assert.True(survivorRow.ContextRevisionRead < revisionAfter);

        // And the index the normalization existed to permit is present and enforcing.
        await Assert.ThrowsAnyAsync<Exception>(() => after.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "AgentSessions"
                ("Id", "TaskId", "Role", "Status", "ContextRevisionRead", "StartedUtc")
            VALUES ({Guid.NewGuid()}, {taskId}, 'Reviewer', 'Started', 0, {DateTime.UtcNow});
            """));
    }

    [Fact]
    public async Task A_database_with_no_violation_is_left_alone()
    {
        using var database = new TemporarySqliteDatabase();

        Guid taskId;
        Guid startedId;
        int revisionBefore;

        await using (var before = await database.CreateContextAtMigrationAsync(PreviousMigration))
        {
            // Inserted with explicit SQL, not through EF: this database is migrated only as
            // far as PreviousMigration, and the current model describes columns that schema
            // does not have.
            var seededProjectId = Guid.NewGuid();
            await LegacyRowSeeder.InsertProjectAsync(
                before,
                seededProjectId,
                $"Clean project {Guid.NewGuid():N}",
                "Seeded for SessionHandoffMigrationTests.",
                ProjectStatus.Active,
                contextRevision: 0,
                DateTime.UtcNow);

            var task = new FamiliarTask
            {
                Id = Guid.NewGuid(),
                ProjectId = seededProjectId,
                Title = "Clean task",
                RequestedOutcome = "Seeded for SessionHandoffMigrationTests.",
                Status = TaskStatus.Ready,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            var completed = NewStartedSession(task.Id, AgentSessionRole.Planner, DateTime.UtcNow.AddHours(-3));
            completed.Status = AgentSessionStatus.Completed;
            completed.CompletedUtc = DateTime.UtcNow.AddHours(-2);

            var started = NewStartedSession(task.Id, AgentSessionRole.Implementer, DateTime.UtcNow.AddHours(-1));

            before.AddRange(task, completed, started);
            await before.SaveChangesAsync();

            taskId = task.Id;
            startedId = started.Id;
            revisionBefore = 0;
        }

        await using var after = await database.CreateContextAsync();

        // Nothing was cancelled, nothing was written, and the revision did not move: normalization
        // must be inert on a database that never violated the invariant.
        var survivingSession = await after.AgentSessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == startedId);
        Assert.Equal(AgentSessionStatus.Started, survivingSession.Status);

        Assert.Empty(await after.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.TaskId == taskId)
            .ToListAsync());

        var storedTask = await after.Tasks.AsNoTracking().SingleAsync(candidate => candidate.Id == taskId);
        Assert.Equal(revisionBefore, await after.Projects
            .AsNoTracking()
            .Where(project => project.Id == storedTask.ProjectId)
            .Select(project => project.ContextRevision)
            .SingleAsync());
    }

    private static AgentSession NewStartedSession(Guid taskId, AgentSessionRole role, DateTime startedUtc) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        Role = role,
        Status = AgentSessionStatus.Started,
        ContextRevisionRead = 0,
        StartedUtc = startedUtc
    };
}
