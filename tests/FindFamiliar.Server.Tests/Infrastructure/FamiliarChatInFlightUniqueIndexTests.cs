using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// IX_FamiliarChatTurns_ChatId_InFlight and IX_FamiliarChatTurns_ChatId_Sequence, asserted against
/// the database itself rather than through any service.
///
/// One turn in flight per conversation is Sprint 12's structural invariant, and it is the database
/// that holds it — a service check can be skipped, raced past, or forgotten by the next caller. The
/// filter is the SQL literal <c>"State" IN ('Pending', 'Generating')</c>, which matches only because
/// <c>FamiliarChatTurn.State</c> is stored via <c>HasConversion&lt;string&gt;()</c>. Remove that
/// conversion and the filter silently stops matching, the index quietly covers nothing, and the
/// invariant disappears without a single service test going red. These tests are the ones that go
/// red.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatInFlightUniqueIndexTests
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE.</summary>
    private const int SqliteConstraintUnique = 2067;

    [Fact]
    public async Task The_migration_created_the_filter_against_the_stored_text_values()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var sql = SqliteSchemaReader.IndexSql(
            database.ConnectionString,
            "IX_FamiliarChatTurns_ChatId_InFlight");

        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("\"ChatId\"", sql, StringComparison.Ordinal);
        Assert.Contains("'Pending'", sql, StringComparison.Ordinal);
        Assert.Contains("'Generating'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FamiliarChatTurnState.Pending, FamiliarChatTurnState.Pending)]
    [InlineData(FamiliarChatTurnState.Pending, FamiliarChatTurnState.Generating)]
    [InlineData(FamiliarChatTurnState.Generating, FamiliarChatTurnState.Generating)]
    public async Task A_second_in_flight_turn_in_one_conversation_is_rejected_by_the_database(
        FamiliarChatTurnState first,
        FamiliarChatTurnState second)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);
        await InsertTurnAsync(dbContext, chatId, sequence: 1, first);

        // Raw SQL deliberately: this asserts the database's own guarantee, not a service's.
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertTurnAsync(dbContext, chatId, sequence: 2, second));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData(FamiliarChatTurnState.Completed)]
    [InlineData(FamiliarChatTurnState.Failed)]
    public async Task A_settled_turn_does_not_occupy_the_in_flight_slot(FamiliarChatTurnState settled)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);

        // The ordinary shape of a conversation in use: a run of settled turns, at most one running.
        await InsertTurnAsync(dbContext, chatId, sequence: 1, settled);
        await InsertTurnAsync(dbContext, chatId, sequence: 2, settled);
        await InsertTurnAsync(dbContext, chatId, sequence: 3, FamiliarChatTurnState.Generating);

        Assert.Equal(3, await dbContext.FamiliarChatTurns.CountAsync(turn => turn.ChatId == chatId));
    }

    [Fact]
    public async Task In_flight_turns_in_different_conversations_are_allowed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var first = await SeedChatAsync(dbContext);
        var second = await SeedChatAsync(dbContext);

        await InsertTurnAsync(dbContext, first, sequence: 1, FamiliarChatTurnState.Generating);
        await InsertTurnAsync(dbContext, second, sequence: 1, FamiliarChatTurnState.Generating);

        Assert.Equal(2, await dbContext.FamiliarChatTurns
            .CountAsync(turn => turn.State == FamiliarChatTurnState.Generating));
    }

    /// <summary>
    /// Two turns cannot share a sequence, so the resume read can never skip or duplicate one.
    /// </summary>
    [Fact]
    public async Task A_sequence_cannot_repeat_within_a_conversation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);
        await InsertTurnAsync(dbContext, chatId, sequence: 1, FamiliarChatTurnState.Completed);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertTurnAsync(dbContext, chatId, sequence: 1, FamiliarChatTurnState.Completed));

        Assert.Equal(SqliteConstraintUnique, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task The_same_sequence_in_different_conversations_is_allowed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var first = await SeedChatAsync(dbContext);
        var second = await SeedChatAsync(dbContext);

        await InsertTurnAsync(dbContext, first, sequence: 1, FamiliarChatTurnState.Completed);
        await InsertTurnAsync(dbContext, second, sequence: 1, FamiliarChatTurnState.Completed);

        Assert.Equal(2, await dbContext.FamiliarChatTurns.CountAsync(turn => turn.Sequence == 1));
    }

    /// <summary>
    /// Inserts through raw SQL on purpose. Going around EF is what makes these assertions about the
    /// database's own guarantee rather than about any service's checks.
    /// </summary>
    private static Task InsertTurnAsync(
        FamiliarDbContext dbContext,
        Guid chatId,
        int sequence,
        FamiliarChatTurnState state) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "FamiliarChatTurns" (
                "Id", "ChatId", "Sequence", "State", "UserText", "FocusProjectIdAtTime",
                "Output", "FailureCode", "CreatedUtc", "StartedUtc", "CompletedUtc")
            VALUES (
                {Guid.NewGuid()}, {chatId}, {sequence}, {state.ToString()},
                'Seeded for FamiliarChatInFlightUniqueIndexTests.', NULL,
                '', NULL, {DateTime.UtcNow}, NULL, NULL);
            """);

    private static async Task<Guid> SeedChatAsync(FamiliarDbContext dbContext)
    {
        var now = DateTime.UtcNow;

        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = $"Index conversation {Guid.NewGuid():N}",
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.FamiliarChats.Add(chat);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return chat.Id;
    }
}
