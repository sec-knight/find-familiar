using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// IX_FamiliarPlanProposals_ChatId_Pending, asserted against the database itself rather than through
/// any service.
///
/// At most one undecided plan per conversation is what makes a half-approved plan unreachable: two
/// contenders can only ever race for one row. A service check can be skipped, raced past, or
/// forgotten by the next caller, so the guarantee lives in the schema.
///
/// The filter is the SQL literal <c>"Status" = 'Pending'</c>, which matches only because
/// <c>FamiliarPlanProposal.Status</c> is stored via <c>HasConversion&lt;string&gt;()</c>. Remove that
/// conversion and the filter silently stops matching, the index quietly covers nothing, and the
/// invariant disappears without a single service test going red. These are the tests that go red —
/// the same job <c>FamiliarChatInFlightUniqueIndexTests</c> does for the in-flight turn.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarPlanPendingUniqueIndexTests
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE.</summary>
    private const int SqliteConstraintUnique = 2067;

    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_migration_created_the_filter_against_the_stored_text_value()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var sql = SqliteSchemaReader.IndexSql(
            database.ConnectionString,
            "IX_FamiliarPlanProposals_ChatId_Pending");

        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("\"ChatId\"", sql, StringComparison.Ordinal);
        Assert.Contains("'Pending'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_pending_plan_in_one_conversation_is_rejected_by_the_database()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (chatId, projectId, turnId) = await SeedAsync(dbContext);

        await InsertPlanAsync(dbContext, chatId, projectId, turnId, FamiliarPlanStatus.Pending);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertPlanAsync(dbContext, chatId, projectId, turnId, FamiliarPlanStatus.Pending));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    /// <summary>
    /// A decided plan releases the slot, or one approval would end planning in that conversation
    /// forever.
    /// </summary>
    [Theory]
    [InlineData(FamiliarPlanStatus.Approved)]
    [InlineData(FamiliarPlanStatus.Declined)]
    public async Task A_decided_plan_does_not_occupy_the_slot(FamiliarPlanStatus decided)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (chatId, projectId, turnId) = await SeedAsync(dbContext);

        await InsertPlanAsync(dbContext, chatId, projectId, turnId, decided);
        await InsertPlanAsync(dbContext, chatId, projectId, turnId, decided);
        await InsertPlanAsync(dbContext, chatId, projectId, turnId, FamiliarPlanStatus.Pending);

        Assert.Equal(3, await dbContext.FamiliarPlanProposals.CountAsync(plan => plan.ChatId == chatId));
    }

    [Fact]
    public async Task Pending_plans_in_different_conversations_are_allowed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var first = await SeedAsync(dbContext);
        var second = await SeedAsync(dbContext);

        await InsertPlanAsync(dbContext, first.ChatId, first.ProjectId, first.TurnId, FamiliarPlanStatus.Pending);
        await InsertPlanAsync(dbContext, second.ChatId, second.ProjectId, second.TurnId, FamiliarPlanStatus.Pending);

        Assert.Equal(2, await dbContext.FamiliarPlanProposals
            .CountAsync(plan => plan.Status == FamiliarPlanStatus.Pending));
    }

    /// <summary>
    /// One created task belongs to at most one item, so a replayed approval cannot let two items claim
    /// the same task. Filtered, because every unapproved item holds NULL and many of them coexist.
    /// </summary>
    [Fact]
    public async Task Two_items_cannot_claim_the_same_created_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (chatId, projectId, turnId) = await SeedAsync(dbContext);
        var planId = await InsertPlanAsync(dbContext, chatId, projectId, turnId, FamiliarPlanStatus.Approved);

        var taskId = Guid.NewGuid();

        await InsertItemAsync(dbContext, planId, position: 0, createdTaskId: taskId);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertItemAsync(dbContext, planId, position: 1, createdTaskId: taskId));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task Many_items_may_have_created_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (chatId, projectId, turnId) = await SeedAsync(dbContext);
        var planId = await InsertPlanAsync(dbContext, chatId, projectId, turnId, FamiliarPlanStatus.Pending);

        await InsertItemAsync(dbContext, planId, position: 0, createdTaskId: null);
        await InsertItemAsync(dbContext, planId, position: 1, createdTaskId: null);
        await InsertItemAsync(dbContext, planId, position: 2, createdTaskId: null);

        Assert.Equal(3, await dbContext.FamiliarPlanItems.CountAsync(item => item.PlanId == planId));
    }

    /// <summary>
    /// Two items cannot share a position, so a plan cannot read in a different order on two devices.
    /// </summary>
    [Fact]
    public async Task A_position_cannot_repeat_within_a_plan()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var (chatId, projectId, turnId) = await SeedAsync(dbContext);
        var planId = await InsertPlanAsync(dbContext, chatId, projectId, turnId, FamiliarPlanStatus.Pending);

        await InsertItemAsync(dbContext, planId, position: 0, createdTaskId: null);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertItemAsync(dbContext, planId, position: 0, createdTaskId: null));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(Guid ChatId, Guid ProjectId, Guid TurnId)> SeedAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Plan index project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarPlanPendingUniqueIndexTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Planning",
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        var turn = new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Sequence = 1,
            State = FamiliarChatTurnState.Completed,
            UserText = "plan it",
            Output = "a reply",
            CreatedUtc = Now
        };

        dbContext.Projects.Add(project);
        dbContext.FamiliarChats.Add(chat);
        dbContext.FamiliarChatTurns.Add(turn);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (chat.Id, project.Id, turn.Id);
    }

    /// <summary>
    /// Raw SQL deliberately: this asserts the database's own guarantee, not a service's, so it must
    /// not go through anything that could be enforcing the rule itself.
    /// </summary>
    private static async Task<Guid> InsertPlanAsync(
        FamiliarDbContext dbContext,
        Guid chatId,
        Guid projectId,
        Guid turnId,
        FamiliarPlanStatus status)
    {
        var planId = Guid.NewGuid();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "FamiliarPlanProposals"
              ("Id", "ChatId", "TurnId", "ProjectId", "Status", "ConcurrencyToken",
               "ObservedContextRevision", "Summary", "CreatedUtc", "UpdatedUtc", "DecidedUtc")
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 0, 'a summary', {6}, {6}, NULL);
            """,
            planId,
            chatId,
            turnId,
            projectId,
            status.ToString(),
            Guid.NewGuid(),
            Now);

        return planId;
    }

    private static async Task InsertItemAsync(
        FamiliarDbContext dbContext,
        Guid planId,
        int position,
        Guid? createdTaskId)
    {
        // The created-task column is written as a literal rather than a parameter. EF's raw-SQL
        // binder has no store mapping for DBNull and refuses the command outright, and a nullable
        // Guid parameter cannot be passed through its params object[] without a null warning.
        var createdTask = createdTaskId is { } taskId ? $"'{taskId}'" : "NULL";

        var sql =
            """
            INSERT INTO "FamiliarPlanItems"
              ("Id", "PlanId", "Position", "Title", "RequestedOutcome", "Role",
               "EvidenceEntryIds", "IsIncluded", "CreatedTaskId")
            VALUES ({0}, {1}, {2}, 'a title', 'an outcome', NULL, NULL, 1, 
            """
            + createdTask + ");";

        await dbContext.Database.ExecuteSqlRawAsync(sql, Guid.NewGuid(), planId, position);
    }
}
