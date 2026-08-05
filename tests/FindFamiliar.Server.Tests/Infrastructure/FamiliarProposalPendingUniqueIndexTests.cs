using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// IX_FamiliarActionProposals_ConversationId_Pending and the two created-link indexes, asserted
/// against the database itself rather than through any service.
///
/// At most one Pending proposal per conversation is what will make concurrent confirmation trivially
/// safe, for the same reason IX_SessionHandoffs_TaskId_Pending does: contenders can only ever race
/// for one row. The filter is the SQL literal <c>"Status" = 'Pending'</c>, which matches only because
/// <c>FamiliarActionProposal.Status</c> is stored via <c>HasConversion&lt;string&gt;()</c>. Remove
/// that conversion and the filter silently stops matching, the index quietly covers nothing, and the
/// invariant disappears without a single test going red. These tests are the ones that go red.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarProposalPendingUniqueIndexTests
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE.</summary>
    private const int SqliteConstraintUnique = 2067;

    [Fact]
    public async Task The_migration_created_the_filter_against_the_stored_text_value()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var sql = SqliteSchemaReader.IndexSql(
            database.ConnectionString,
            "IX_FamiliarActionProposals_ConversationId_Pending");

        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("\"ConversationId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Status\" = 'Pending'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_pending_proposal_in_one_conversation_is_rejected_by_the_database()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await SeedAsync(dbContext);
        await InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Pending);

        // Raw SQL deliberately: this asserts the database's own guarantee, not a service's.
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Pending));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData(FamiliarActionStatus.Confirmed)]
    [InlineData(FamiliarActionStatus.Dismissed)]
    public async Task A_decided_proposal_does_not_occupy_the_pending_slot(FamiliarActionStatus decided)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await SeedAsync(dbContext);

        // The ordinary shape of a conversation that has been used: several settled proposals, at
        // most one still awaiting a human.
        await InsertProposalAsync(dbContext, fixture, decided);
        await InsertProposalAsync(dbContext, fixture, decided);
        await InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Pending);

        Assert.Equal(3, await dbContext.FamiliarActionProposals
            .CountAsync(proposal => proposal.ConversationId == fixture.ConversationId));
    }

    [Fact]
    public async Task Pending_proposals_in_different_conversations_are_allowed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var first = await SeedAsync(dbContext);
        var second = await SeedAsync(dbContext);

        await InsertProposalAsync(dbContext, first, FamiliarActionStatus.Pending);
        await InsertProposalAsync(dbContext, second, FamiliarActionStatus.Pending);

        Assert.Equal(2, await dbContext.FamiliarActionProposals
            .CountAsync(proposal => proposal.Status == FamiliarActionStatus.Pending));
    }

    [Fact]
    public async Task Two_proposals_cannot_claim_the_same_created_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await SeedAsync(dbContext);

        await InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Confirmed, createdTaskId: fixture.TaskId);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Confirmed, createdTaskId: fixture.TaskId));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task Two_proposals_cannot_claim_the_same_created_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await SeedAsync(dbContext);

        await InsertProposalAsync(
            dbContext, fixture, FamiliarActionStatus.Confirmed, createdSessionId: fixture.SessionId);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertProposalAsync(
                dbContext, fixture, FamiliarActionStatus.Confirmed, createdSessionId: fixture.SessionId));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task Many_proposals_may_hold_no_created_links_at_all()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var fixture = await SeedAsync(dbContext);

        // Both created-link indexes are filtered on IS NOT NULL. Without that filter the second
        // dismissed proposal here would collide with the first on a shared NULL.
        await InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Dismissed);
        await InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Dismissed);
        await InsertProposalAsync(dbContext, fixture, FamiliarActionStatus.Dismissed);

        Assert.Equal(3, await dbContext.FamiliarActionProposals
            .CountAsync(proposal => proposal.CreatedTaskId == null && proposal.CreatedSessionId == null));
    }

    internal sealed record ConversationFixture(
        Guid ProjectId,
        Guid ConversationId,
        Guid MessageId,
        Guid TaskId,
        Guid SessionId);

    /// <summary>
    /// Inserts through raw SQL on purpose. Going around EF is what makes these assertions about the
    /// database's own guarantee rather than about any service's checks. Values are passed as typed
    /// parameters so EF maps them to the same storage shape it writes itself.
    /// </summary>
    internal static Task InsertProposalAsync(
        FamiliarDbContext dbContext,
        ConversationFixture fixture,
        FamiliarActionStatus status,
        Guid? createdTaskId = null,
        Guid? createdSessionId = null) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "FamiliarActionProposals" (
                "Id", "ConversationId", "ProjectId", "MessageId", "Kind", "Status",
                "ConcurrencyToken", "ObservedContextRevision", "Title", "RequestedOutcome",
                "TargetTaskId", "CreatedUtc", "UpdatedUtc", "DecidedUtc",
                "CreatedTaskId", "CreatedSessionId")
            VALUES (
                {Guid.NewGuid()}, {fixture.ConversationId}, {fixture.ProjectId}, {fixture.MessageId},
                {FamiliarActionKind.CreateTask.ToString()}, {status.ToString()},
                {Guid.NewGuid()}, 0, 'Index proposal', 'Seeded for FamiliarProposalPendingUniqueIndexTests.',
                NULL, {DateTime.UtcNow}, {DateTime.UtcNow}, NULL,
                {createdTaskId}, {createdSessionId});
            """);

    /// <summary>A project with one conversation, one Familiar message, one task and one session.</summary>
    internal static async Task<ConversationFixture> SeedAsync(FamiliarDbContext dbContext)
    {
        var now = DateTime.UtcNow;

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Index project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarProposalPendingUniqueIndexTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Index task",
            RequestedOutcome = "Seeded for FamiliarProposalPendingUniqueIndexTests.",
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
            ContextRevisionRead = 0,
            StartedUtc = now.AddHours(-1),
            CompletedUtc = now
        };

        var conversation = new FamiliarConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var message = new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = FamiliarMessageAuthor.Familiar,
            Sequence = 1,
            Content = "Seeded for FamiliarProposalPendingUniqueIndexTests.",
            CreatedUtc = now,
            Delivery = FamiliarMessageDelivery.Delivered
        };

        dbContext.AddRange(project, task, session, conversation, message);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return new ConversationFixture(project.Id, conversation.Id, message.Id, task.Id, session.Id);
    }
}
