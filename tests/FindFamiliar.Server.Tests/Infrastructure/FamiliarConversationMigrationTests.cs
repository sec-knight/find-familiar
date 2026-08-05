using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// The FamiliarConversations migration, asserted against real SQLite files.
///
/// This migration is purely additive, and "purely" is the claim worth testing: it must create four
/// tables and touch nothing else. The application migrates at startup against a database holding
/// every project, task, session and handoff this system has recorded, so a stray ALTER or UPDATE here
/// would rewrite production history on the next boot. Comparing sqlite_master and every existing row
/// across the migration is what makes that claim checkable rather than merely intended.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarConversationMigrationTests
{
    /// <summary>The last migration before this one, i.e. the schema Sprint 10 shipped and accepted.</summary>
    private const string Sprint10Baseline = "20260805011808_SessionHandoffsAndStartedSessionUniqueness";

    private static readonly string[] NewTables =
    [
        "FamiliarConversations",
        "FamiliarMessages",
        "FamiliarEvidence",
        "FamiliarActionProposals"
    ];

    [Fact]
    public async Task The_four_tables_are_created_on_a_fresh_database()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var tables = SqliteSchemaReader.TableNames(database.ConnectionString);

        foreach (var table in NewTables)
        {
            Assert.Contains(table, tables);
        }

        // And they are usable through EF, not merely present: a table whose columns EF cannot map
        // would still satisfy a name check.
        Assert.Empty(await dbContext.FamiliarConversations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarActionProposals.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task No_conversation_row_is_created_for_an_existing_project()
    {
        using var database = new TemporarySqliteDatabase();

        await using (var before = await database.CreateContextAtMigrationAsync(Sprint10Baseline))
        {
            await SeedSprint10ProjectAsync(before);
        }

        await using var after = await database.CreateContextAsync();

        // Nothing is backfilled. A project with no conversation has no conversation, and a row
        // created by a migration would be a conversation nobody started.
        Assert.Empty(await after.FamiliarConversations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Applying_it_to_a_sprint_10_database_alters_no_existing_table_and_touches_no_row()
    {
        using var database = new TemporarySqliteDatabase();

        Sprint10Fixture fixture;
        await using (var before = await database.CreateContextAtMigrationAsync(Sprint10Baseline))
        {
            fixture = await SeedSprint10ProjectAsync(before);
        }

        var schemaBefore = SqliteSchemaReader.Definitions(database.ConnectionString);
        var rowsBefore = await ReadEveryExistingRowAsync(database, Sprint10Baseline);

        await using var after = await database.CreateContextAsync();

        // Every table and index that existed before still exists, with byte-identical DDL. This is
        // the assertion that would fail if a future edit to this migration recreated a table to add
        // a column — SQLite's table rebuild is exactly how an "additive" change stops being one.
        var schemaAfter = SqliteSchemaReader.Definitions(database.ConnectionString);
        foreach (var (name, sql) in schemaBefore)
        {
            Assert.True(schemaAfter.ContainsKey(name), $"{name} disappeared across the migration.");
            Assert.Equal(sql, schemaAfter[name]);
        }

        var rowsAfter = await ReadEveryExistingRowAsync(database, targetMigration: null);
        Assert.Equal(rowsBefore, rowsAfter);

        // Spot-check through EF as well, so a row that survived in a shape EF can no longer read
        // would still fail here.
        var project = await after.Projects.AsNoTracking().SingleAsync(candidate => candidate.Id == fixture.ProjectId);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(fixture.ContextRevision, project.ContextRevision);

        var handoff = await after.SessionHandoffs.AsNoTracking().SingleAsync();
        Assert.Equal(SessionHandoffStatus.Pending, handoff.Status);

        foreach (var table in NewTables)
        {
            Assert.Contains(table, SqliteSchemaReader.TableNames(database.ConnectionString));
        }
    }

    [Fact]
    public async Task Rolling_it_back_restores_the_sprint_10_schema_exactly()
    {
        using var database = new TemporarySqliteDatabase();

        await using (var before = await database.CreateContextAtMigrationAsync(Sprint10Baseline))
        {
            await SeedSprint10ProjectAsync(before);
        }

        var schemaBefore = SqliteSchemaReader.Definitions(database.ConnectionString);
        var rowsBefore = await ReadEveryExistingRowAsync(database, Sprint10Baseline);

        // Up …
        await using (var migrated = await database.CreateContextAsync())
        {
            Assert.Contains("FamiliarMessages", SqliteSchemaReader.TableNames(database.ConnectionString));
            await migrated.DisposeAsync();
        }

        // … then Down. Dropping the four tables must leave nothing behind: an index surviving a Down
        // is how a later re-apply fails on a database nobody thought to inspect.
        await using (var rolledBack = await database.CreateContextAtMigrationAsync(Sprint10Baseline))
        {
            await rolledBack.DisposeAsync();
        }

        var schemaAfter = SqliteSchemaReader.Definitions(database.ConnectionString);
        Assert.Equal(
            schemaBefore.Keys.OrderBy(key => key, StringComparer.Ordinal),
            schemaAfter.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var (name, sql) in schemaBefore)
        {
            Assert.Equal(sql, schemaAfter[name]);
        }

        Assert.Equal(rowsBefore, await ReadEveryExistingRowAsync(database, Sprint10Baseline));
    }

    /// <summary>
    /// Every row of every Sprint 10 table, rendered as ordered text so two readings can be compared
    /// for equality. Reading through EF keeps the comparison in the shapes the application uses.
    /// </summary>
    private static async Task<List<string>> ReadEveryExistingRowAsync(
        TemporarySqliteDatabase database,
        string? targetMigration)
    {
        await using var dbContext = targetMigration is null
            ? await database.CreateContextAsync()
            : await database.CreateContextAtMigrationAsync(targetMigration);

        var rows = new List<string>();

        rows.AddRange((await dbContext.Projects.AsNoTracking().ToListAsync())
            .Select(project => $"Project {project.Id} {project.Name} {project.Status} {project.ContextRevision} {project.Purpose} {project.CreatedUtc:O} {project.UpdatedUtc:O}"));
        rows.AddRange((await dbContext.Tasks.AsNoTracking().ToListAsync())
            .Select(task => $"Task {task.Id} {task.ProjectId} {task.Title} {task.Status} {task.RequestedOutcome} {task.CreatedUtc:O} {task.UpdatedUtc:O}"));
        rows.AddRange((await dbContext.AgentSessions.AsNoTracking().ToListAsync())
            .Select(session => $"Session {session.Id} {session.TaskId} {session.Role} {session.Status} {session.ContextRevisionRead} {session.StartedUtc:O} {session.CompletedUtc:O}"));
        rows.AddRange((await dbContext.SessionHandoffs.AsNoTracking().ToListAsync())
            .Select(handoff => $"Handoff {handoff.Id} {handoff.TaskId} {handoff.SourceSessionId} {handoff.Status} {handoff.Kind} {handoff.ProposedRole} {handoff.ConcurrencyToken} {handoff.CreatedSessionId}"));
        rows.AddRange((await dbContext.ContextEntries.AsNoTracking().ToListAsync())
            .Select(entry => $"ContextEntry {entry.Id} {entry.ProjectId} {entry.TaskId} {entry.Kind} {entry.State} {entry.Title} {entry.Content}"));
        rows.AddRange((await dbContext.Workers.AsNoTracking().ToListAsync())
            .Select(worker => $"Worker {worker.Id} {worker.WorkerKey} {worker.DisplayName} {worker.Capabilities} {worker.Enabled} {worker.RegisteredUtc:O}"));
        rows.AddRange((await dbContext.Conversations.AsNoTracking().ToListAsync())
            .Select(conversation => $"Conversation {conversation.Id} {conversation.Status} {conversation.ApprovedTaskId} {conversation.ApprovedSessionId}"));
        rows.AddRange((await dbContext.ConversationMessages.AsNoTracking().ToListAsync())
            .Select(message => $"ConversationMessage {message.Id} {message.ConversationId} {message.Author} {message.Sequence} {message.Content}"));
        rows.AddRange((await dbContext.WorkProposals.AsNoTracking().ToListAsync())
            .Select(proposal => $"WorkProposal {proposal.Id} {proposal.ConversationId} {proposal.ProjectId} {proposal.Status} {proposal.Title} {proposal.RequestedOutcome} {proposal.ConcurrencyToken}"));

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private sealed record Sprint10Fixture(Guid ProjectId, Guid TaskId, int ContextRevision);

    /// <summary>
    /// A database holding one row in every table Sprint 10 shipped, so "no existing row was touched"
    /// is a statement about all of them rather than about the two this migration happens to be near.
    /// </summary>
    private static async Task<Sprint10Fixture> SeedSprint10ProjectAsync(FamiliarDbContext dbContext)
    {
        var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Migration project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarConversationMigrationTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        project.IncrementContextRevision();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Migration task",
            RequestedOutcome = "Seeded for FamiliarConversationMigrationTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Completed,
            ContextRevisionRead = 7,
            StartedUtc = now.AddHours(-2),
            CompletedUtc = now.AddHours(-1)
        };

        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = session.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Implementer,
            Kind = SessionHandoffKind.NextRole,
            Status = SessionHandoffStatus.Pending,
            ObservedContextRevision = 7,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            TaskId = task.Id,
            SourceSessionId = session.Id,
            Kind = ContextEntryKind.Plan,
            Title = "Migration plan",
            Content = "Seeded for FamiliarConversationMigrationTests.",
            State = ContextEntryState.Active,
            CreatedUtc = now
        };

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            WorkerKey = $"migration-worker-{Guid.NewGuid():N}",
            DisplayName = "Migration worker",
            Capabilities = "Planner",
            Enabled = true,
            RegisteredUtc = now,
            LastHeartbeatUtc = now
        };

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Status = ConversationStatus.AwaitingApproval,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = ConversationMessageAuthor.Human,
            Sequence = 1,
            Content = "Seeded for FamiliarConversationMigrationTests.",
            CreatedUtc = now
        };

        var proposal = new WorkProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            ProjectId = project.Id,
            Title = "Migration proposal",
            RequestedOutcome = "Seeded for FamiliarConversationMigrationTests.",
            Role = AgentSessionRole.Planner,
            ObservedContextRevision = 7,
            Status = WorkProposalStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.AddRange(project, task, session, handoff, entry, worker, conversation, message, proposal);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return new Sprint10Fixture(project.Id, task.Id, project.ContextRevision);
    }
}
