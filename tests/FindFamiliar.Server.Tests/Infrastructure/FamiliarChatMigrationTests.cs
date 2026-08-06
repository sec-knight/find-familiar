using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// The FamiliarChats migration, asserted against real SQLite files.
///
/// This migration is purely additive, and "purely" is the claim worth testing. The application
/// migrates at startup against a database holding every project, task, session, handoff and
/// per-project conversation this system has recorded, so a stray ALTER or UPDATE here would rewrite
/// production history on the next boot. Comparing sqlite_master and every existing row across the
/// migration is what makes that claim checkable rather than merely intended.
///
/// The Sprint 11 <c>FamiliarConversations</c> tables matter most here: Sprint 12's conversation is a
/// separate aggregate in its own tables, not a reshaping of that one, and the byte-identical DDL
/// comparison below is what proves it stayed separate.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatMigrationTests
{
    /// <summary>The last migration before this one, i.e. the schema Sprint 11 shipped and accepted.</summary>
    /// <summary>
    /// The migration these tests are about.
    ///
    /// Named explicitly, and migrated to explicitly, because "apply the migration under test" and
    /// "migrate to head" stopped being the same thing the moment a later migration added a column to
    /// an existing table. Migrating to head here would quietly turn every assertion below into a
    /// claim about the whole chain, and the first legitimately column-adding migration would fail a
    /// test named after a different one.
    /// </summary>
    private const string MigrationUnderTest = "20260806172442_FamiliarChats";

    private const string Sprint11Baseline = "20260805215236_FamiliarConversations";

    private static readonly string[] NewTables = ["FamiliarChats", "FamiliarChatTurns"];

    [Fact]
    public async Task The_two_tables_are_created_on_a_fresh_database()
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
        Assert.Empty(await dbContext.FamiliarChats.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarChatTurns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task No_conversation_row_is_created_for_an_existing_project()
    {
        using var database = new TemporarySqliteDatabase();

        await using (var before = await database.CreateContextAtMigrationAsync(Sprint11Baseline))
        {
            await SeedSprint11FixtureAsync(before);
        }

        await using var after = await database.CreateContextAsync();

        // Nothing is backfilled. A conversation nobody started is not a conversation, and a row
        // created by a migration would be exactly that.
        Assert.Empty(await after.FamiliarChats.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Applying_it_to_a_sprint_11_database_alters_no_existing_table_and_touches_no_row()
    {
        using var database = new TemporarySqliteDatabase();

        Sprint11Fixture fixture;
        await using (var before = await database.CreateContextAtMigrationAsync(Sprint11Baseline))
        {
            fixture = await SeedSprint11FixtureAsync(before);
        }

        var schemaBefore = SqliteSchemaReader.Definitions(database.ConnectionString);
        var rowsBefore = await ReadEveryExistingRowAsync(database, Sprint11Baseline);

        await using var after = await database.CreateContextAtMigrationAsync(MigrationUnderTest);

        // Every table and index that existed before still exists, with byte-identical DDL. This is
        // the assertion that would fail if a future edit to this migration recreated a table to add
        // a column — SQLite's table rebuild is exactly how an "additive" change stops being one.
        var schemaAfter = SqliteSchemaReader.Definitions(database.ConnectionString);
        foreach (var (name, sql) in schemaBefore)
        {
            Assert.True(schemaAfter.ContainsKey(name), $"{name} disappeared across the migration.");
            Assert.Equal(sql, schemaAfter[name]);
        }

        Assert.Equal(rowsBefore, await ReadEveryExistingRowAsync(database, MigrationUnderTest));

        // Spot-check the Sprint 11 conversation through EF as well: it is the aggregate this sprint
        // deliberately did not reshape, so it is the one worth reading back in the shape the
        // application uses.
        var conversation = await after.FamiliarConversations.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.ProjectId, conversation.ProjectId);

        var message = await after.FamiliarMessages.AsNoTracking().SingleAsync();
        Assert.Equal(FamiliarMessageAuthor.Human, message.Author);

        foreach (var table in NewTables)
        {
            Assert.Contains(table, SqliteSchemaReader.TableNames(database.ConnectionString));
        }
    }

    [Fact]
    public async Task Rolling_it_back_restores_the_sprint_11_schema_exactly()
    {
        using var database = new TemporarySqliteDatabase();

        await using (var before = await database.CreateContextAtMigrationAsync(Sprint11Baseline))
        {
            await SeedSprint11FixtureAsync(before);
        }

        var schemaBefore = SqliteSchemaReader.Definitions(database.ConnectionString);
        var rowsBefore = await ReadEveryExistingRowAsync(database, Sprint11Baseline);

        // Up …
        await using (var migrated = await database.CreateContextAsync())
        {
            Assert.Contains("FamiliarChatTurns", SqliteSchemaReader.TableNames(database.ConnectionString));
            await migrated.DisposeAsync();
        }

        // … then Down. Dropping the two tables must leave nothing behind: an index surviving a Down
        // is how a later re-apply fails on a database nobody thought to inspect.
        await using (var rolledBack = await database.CreateContextAtMigrationAsync(Sprint11Baseline))
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

        Assert.Equal(rowsBefore, await ReadEveryExistingRowAsync(database, Sprint11Baseline));
    }

    /// <summary>
    /// Every row of every Sprint 11 table, rendered as ordered text so two readings can be compared
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

        // Read with explicit column lists, for the same reason they were written that way.
        rows.AddRange(await LegacyRowSeeder.ReadProjectRowsAsync(dbContext));
        rows.AddRange((await dbContext.Tasks.AsNoTracking().ToListAsync())
            .Select(task => $"Task {task.Id} {task.ProjectId} {task.Title} {task.Status} {task.RequestedOutcome} {task.CreatedUtc:O} {task.UpdatedUtc:O}"));
        rows.AddRange((await dbContext.AgentSessions.AsNoTracking().ToListAsync())
            .Select(session => $"Session {session.Id} {session.TaskId} {session.Role} {session.Status} {session.ContextRevisionRead} {session.StartedUtc:O} {session.CompletedUtc:O}"));
        rows.AddRange(await LegacyRowSeeder.ReadContextEntryRowsAsync(dbContext));
        rows.AddRange((await dbContext.Workers.AsNoTracking().ToListAsync())
            .Select(worker => $"Worker {worker.Id} {worker.WorkerKey} {worker.DisplayName} {worker.Capabilities} {worker.Enabled} {worker.RegisteredUtc:O}"));
        rows.AddRange((await dbContext.FamiliarConversations.AsNoTracking().ToListAsync())
            .Select(conversation => $"FamiliarConversation {conversation.Id} {conversation.ProjectId} {conversation.CreatedUtc:O} {conversation.UpdatedUtc:O}"));
        rows.AddRange((await dbContext.FamiliarMessages.AsNoTracking().ToListAsync())
            .Select(message => $"FamiliarMessage {message.Id} {message.ConversationId} {message.Author} {message.Sequence} {message.Content} {message.Delivery} {message.FailureCode}"));
        rows.AddRange((await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync())
            .Select(evidence => $"FamiliarEvidence {evidence.Id} {evidence.MessageId} {evidence.Kind} {evidence.ReferenceId} {evidence.Label}"));
        rows.AddRange((await dbContext.FamiliarActionProposals.AsNoTracking().ToListAsync())
            .Select(proposal => $"FamiliarActionProposal {proposal.Id} {proposal.ConversationId} {proposal.ProjectId} {proposal.MessageId} {proposal.Kind} {proposal.Status} {proposal.ConcurrencyToken}"));

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private sealed record Sprint11Fixture(Guid ProjectId, Guid ConversationId);

    /// <summary>
    /// A database holding the Sprint 11 shape this migration must not disturb: a project with a
    /// task, a session, context, a worker, and a per-project Familiar conversation carrying a
    /// message, its evidence and a pending proposal.
    /// </summary>
    private static async Task<Sprint11Fixture> SeedSprint11FixtureAsync(FamiliarDbContext dbContext)
    {
        var now = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

        // Inserted with explicit SQL, not through EF: this database is migrated only as far
        // as the baseline above, and the current model describes columns that schema does not
        // have. See LegacyRowSeeder.
        var projectId = Guid.NewGuid();
        const int seededContextRevision = 1;
        await LegacyRowSeeder.InsertProjectAsync(
            dbContext,
            projectId,
            $"Migration project {Guid.NewGuid():N}",
            "Seeded for FamiliarChatMigrationTests.",
            ProjectStatus.Active,
            seededContextRevision,
            now);

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = "Migration task",
            RequestedOutcome = "Seeded for FamiliarChatMigrationTests.",
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
            ContextRevisionRead = 1,
            StartedUtc = now.AddHours(-2),
            CompletedUtc = now.AddHours(-1)
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

        var conversation = new FamiliarConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var message = new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = FamiliarMessageAuthor.Human,
            Sequence = 1,
            Content = "Seeded for FamiliarChatMigrationTests.",
            CreatedUtc = now,
            Delivery = FamiliarMessageDelivery.Delivered
        };

        var evidence = new FamiliarEvidence
        {
            Id = Guid.NewGuid(),
            MessageId = message.Id,
            Kind = FamiliarEvidenceKind.Task,
            ReferenceId = task.Id,
            Label = "Task \"Migration task\""
        };

        var proposal = new FamiliarActionProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            ProjectId = projectId,
            MessageId = message.Id,
            Kind = FamiliarActionKind.CreateTask,
            Status = FamiliarActionStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = 1,
            Title = "Migration proposal",
            RequestedOutcome = "Seeded for FamiliarChatMigrationTests.",
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.AddRange(task, session, worker, conversation, message, evidence, proposal);
        await dbContext.SaveChangesAsync();

        // Same reason as the project above: ContextEntries gained a column after this baseline.
        await LegacyRowSeeder.InsertContextEntryAsync(
            dbContext,
            Guid.NewGuid(),
            projectId,
            task.Id,
            session.Id,
            ContextEntryKind.Plan,
            "Migration plan",
            "Seeded for a migration test.",
            ContextEntryState.Active,
            now);

        dbContext.ChangeTracker.Clear();

        return new Sprint11Fixture(projectId, conversation.Id);
    }
}
