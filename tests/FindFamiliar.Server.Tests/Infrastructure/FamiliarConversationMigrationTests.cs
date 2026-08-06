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
    /// <summary>
    /// The migration these tests are about.
    ///
    /// Named explicitly, and migrated to explicitly, because "apply the migration under test" and
    /// "migrate to head" stopped being the same thing the moment a later migration added a column to
    /// an existing table. Migrating to head here would quietly turn every assertion below into a
    /// claim about the whole chain, and the first legitimately column-adding migration would fail a
    /// test named after a different one.
    /// </summary>
    private const string MigrationUnderTest = "20260805215236_FamiliarConversations";

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

        var rowsAfter = await ReadEveryExistingRowAsync(database, MigrationUnderTest);
        Assert.Equal(rowsBefore, rowsAfter);

        // The project is spot-checked through the raw reader rather than through EF. At this
        // migration the Projects table has no IsSensitive column, so an EF read would name a column
        // that does not exist and fail for a reason that has nothing to do with this migration.
        var projectRows = await LegacyRowSeeder.ReadProjectRowsAsync(after);
        var projectRow = Assert.Single(projectRows);
        Assert.Contains(fixture.ProjectId.ToString(), projectRow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ProjectStatus.Active.ToString(), projectRow, StringComparison.Ordinal);
        Assert.Contains($" {fixture.ContextRevision} ", projectRow, StringComparison.Ordinal);

        // Tables this migration did not touch are still read through EF, which is the stronger check
        // where it is available: a row that survived in a shape EF cannot read would fail here.
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

        // Read with explicit column lists, for the same reason they were written that way.
        rows.AddRange(await LegacyRowSeeder.ReadProjectRowsAsync(dbContext));
        rows.AddRange((await dbContext.Tasks.AsNoTracking().ToListAsync())
            .Select(task => $"Task {task.Id} {task.ProjectId} {task.Title} {task.Status} {task.RequestedOutcome} {task.CreatedUtc:O} {task.UpdatedUtc:O}"));
        rows.AddRange((await dbContext.AgentSessions.AsNoTracking().ToListAsync())
            .Select(session => $"Session {session.Id} {session.TaskId} {session.Role} {session.Status} {session.ContextRevisionRead} {session.StartedUtc:O} {session.CompletedUtc:O}"));
        rows.AddRange((await dbContext.SessionHandoffs.AsNoTracking().ToListAsync())
            .Select(handoff => $"Handoff {handoff.Id} {handoff.TaskId} {handoff.SourceSessionId} {handoff.Status} {handoff.Kind} {handoff.ProposedRole} {handoff.ConcurrencyToken} {handoff.CreatedSessionId}"));
        rows.AddRange(await LegacyRowSeeder.ReadContextEntryRowsAsync(dbContext));
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

        // Inserted with explicit SQL, not through EF: this database is migrated only as far
        // as the baseline above, and the current model describes columns that schema does not
        // have. See LegacyRowSeeder.
        var projectId = Guid.NewGuid();
        const int seededContextRevision = 1;
        await LegacyRowSeeder.InsertProjectAsync(
            dbContext,
            projectId,
            $"Migration project {Guid.NewGuid():N}",
            "Seeded for FamiliarConversationMigrationTests.",
            ProjectStatus.Active,
            seededContextRevision,
            now);

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
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
            ProjectId = projectId,
            Title = "Migration proposal",
            RequestedOutcome = "Seeded for FamiliarConversationMigrationTests.",
            Role = AgentSessionRole.Planner,
            ObservedContextRevision = 7,
            Status = WorkProposalStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.AddRange(task, session, handoff, worker, conversation, message, proposal);
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

        return new Sprint10Fixture(projectId, task.Id, seededContextRevision);
    }
}
