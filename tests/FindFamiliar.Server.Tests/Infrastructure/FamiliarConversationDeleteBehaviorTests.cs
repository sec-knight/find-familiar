using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// What SQLite actually does when a row a conversation depends on is deleted.
///
/// Asserted with raw SQL rather than through EF's change tracker on purpose. EF happily performs its
/// own client-side cascade for entities it has loaded, so a test that deletes through
/// <c>DbContext.Remove</c> proves only that EF is configured a certain way — the database would still
/// be free to do something else to a row EF never saw. These deletes go straight to the file.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarConversationDeleteBehaviorTests
{
    /// <summary>
    /// SQLITE_CONSTRAINT_TRIGGER. SQLite reports a refused RESTRICT action under this code rather
    /// than SQLITE_CONSTRAINT_FOREIGNKEY, which it reserves for an immediate reference violation.
    /// The message is asserted alongside it so the distinction is visible to whoever reads a failure.
    /// </summary>
    private const int SqliteConstraintTrigger = 1811;

    [Fact]
    public async Task Deleting_a_project_takes_its_conversation_and_everything_under_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await FamiliarProposalPendingUniqueIndexTests.SeedAsync(dbContext);
        await AddEvidenceAsync(dbContext, fixture.MessageId);
        await FamiliarProposalPendingUniqueIndexTests.InsertProposalAsync(
            dbContext, fixture, FamiliarActionStatus.Pending);

        // The task and session are deleted first: a proposal's Restrict links point at Tasks and
        // AgentSessions, so removing them out of order would abort the project delete rather than
        // demonstrate the cascade. That refusal is itself the subject of the two tests below.
        await dbContext.Database.ExecuteSqlAsync($"""DELETE FROM "AgentSessions" WHERE "Id" = {fixture.SessionId};""");
        await dbContext.Database.ExecuteSqlAsync($"""DELETE FROM "Tasks" WHERE "Id" = {fixture.TaskId};""");
        await dbContext.Database.ExecuteSqlAsync($"""DELETE FROM "Projects" WHERE "Id" = {fixture.ProjectId};""");

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.FamiliarConversations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarActionProposals.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_conversation_takes_its_messages_evidence_and_proposals()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await FamiliarProposalPendingUniqueIndexTests.SeedAsync(dbContext);
        await AddEvidenceAsync(dbContext, fixture.MessageId);
        await FamiliarProposalPendingUniqueIndexTests.InsertProposalAsync(
            dbContext, fixture, FamiliarActionStatus.Dismissed);

        await dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "FamiliarConversations" WHERE "Id" = {fixture.ConversationId};""");

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.FamiliarMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarActionProposals.AsNoTracking().ToListAsync());

        // The project, task and session are untouched: a conversation is a record of a discussion
        // about work, never the owner of it.
        Assert.NotNull(await dbContext.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == fixture.ProjectId));
        Assert.NotNull(await dbContext.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == fixture.TaskId));
        Assert.NotNull(await dbContext.AgentSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == fixture.SessionId));
    }

    [Fact]
    public async Task Deleting_a_message_takes_its_evidence()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await FamiliarProposalPendingUniqueIndexTests.SeedAsync(dbContext);
        await AddEvidenceAsync(dbContext, fixture.MessageId);

        await dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "FamiliarMessages" WHERE "Id" = {fixture.MessageId};""");

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_task_a_proposal_targets_cannot_be_deleted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await FamiliarProposalPendingUniqueIndexTests.SeedAsync(dbContext);
        await InsertPlannerProposalAsync(dbContext, fixture, targetTaskId: fixture.TaskId);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Tasks" WHERE "Id" = {fixture.TaskId};"""));

        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Contains("FOREIGN KEY constraint failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_a_proposal_says_it_created_cannot_be_deleted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await FamiliarProposalPendingUniqueIndexTests.SeedAsync(dbContext);
        await FamiliarProposalPendingUniqueIndexTests.InsertProposalAsync(
            dbContext, fixture, FamiliarActionStatus.Confirmed, createdTaskId: fixture.TaskId);

        // Restrict, not Cascade or SetNull: a proposal is the durable record that a human confirmed
        // this exact task into existence, and a link silently emptied would leave that record lying.
        var exception = await Assert.ThrowsAsync<SqliteException>(() => dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Tasks" WHERE "Id" = {fixture.TaskId};"""));

        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Contains("FOREIGN KEY constraint failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_a_proposal_says_it_created_cannot_be_deleted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await FamiliarProposalPendingUniqueIndexTests.SeedAsync(dbContext);
        await FamiliarProposalPendingUniqueIndexTests.InsertProposalAsync(
            dbContext, fixture, FamiliarActionStatus.Confirmed, createdSessionId: fixture.SessionId);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "AgentSessions" WHERE "Id" = {fixture.SessionId};"""));

        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Contains("FOREIGN KEY constraint failed", exception.Message, StringComparison.Ordinal);
    }

    private static Task InsertPlannerProposalAsync(
        FamiliarDbContext dbContext,
        FamiliarProposalPendingUniqueIndexTests.ConversationFixture fixture,
        Guid targetTaskId) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "FamiliarActionProposals" (
                "Id", "ConversationId", "ProjectId", "MessageId", "Kind", "Status",
                "ConcurrencyToken", "ObservedContextRevision", "Title", "RequestedOutcome",
                "TargetTaskId", "CreatedUtc", "UpdatedUtc", "DecidedUtc",
                "CreatedTaskId", "CreatedSessionId")
            VALUES (
                {Guid.NewGuid()}, {fixture.ConversationId}, {fixture.ProjectId}, {fixture.MessageId},
                {FamiliarActionKind.StartPlanner.ToString()}, {FamiliarActionStatus.Pending.ToString()},
                {Guid.NewGuid()}, 0, NULL, NULL,
                {targetTaskId}, {DateTime.UtcNow}, {DateTime.UtcNow}, NULL,
                NULL, NULL);
            """);

    private static async Task AddEvidenceAsync(FamiliarDbContext dbContext, Guid messageId)
    {
        dbContext.Add(new FamiliarEvidence
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            Kind = FamiliarEvidenceKind.Task,
            ReferenceId = Guid.NewGuid(),
            Label = "Seeded for FamiliarConversationDeleteBehaviorTests."
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }
}
