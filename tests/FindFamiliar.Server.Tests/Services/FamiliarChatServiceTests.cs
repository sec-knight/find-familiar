using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The system-wide conversation's read and write surface.
///
/// Three properties carry the slice, and each is asserted rather than assumed:
///
/// - a send makes a turn durable and returns, without calling anything;
/// - the resume read answers "everything after sequence N" identically however far behind the caller
///   is, including when it is not behind at all;
/// - a second send while a reply is running attaches to it rather than queueing behind it, and
///   writes nothing.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- durability

    [Fact]
    public async Task A_send_creates_the_conversation_and_its_first_turn()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var result = await NewService(dbContext).SendAsync(null, "what is blocked?");

        Assert.Equal(FamiliarChatSendStatus.Accepted, result.Status);
        Assert.Equal(1, result.Sequence);

        var turn = await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync();
        Assert.Equal(FamiliarChatTurnState.Pending, turn.State);
        Assert.Equal("what is blocked?", turn.UserText);
        Assert.Equal(string.Empty, turn.Output);
        Assert.Null(turn.StartedUtc);
        Assert.Null(turn.CompletedUtc);
    }

    /// <summary>
    /// The turn is committed before it is scheduled, never the other way round: a generator that
    /// read the queue first would be reading a row that does not exist yet.
    /// </summary>
    [Fact]
    public async Task The_turn_is_committed_before_it_is_enqueued()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        await using var observer = await database.CreateContextAsync();

        var queue = new FamiliarChatGenerationQueue();
        var result = await NewService(dbContext, queue).SendAsync(null, "a question");

        var scheduled = await ReadQueuedAsync(queue);

        Assert.Single(scheduled);
        var committed = await observer.FamiliarChatTurns.AsNoTracking().SingleAsync();
        Assert.Equal(scheduled[0], committed.Id);
        Assert.Equal(result.ChatId, committed.ChatId);
    }

    /// <summary>The conversation is system-wide: no project is required to have one.</summary>
    [Fact]
    public async Task A_conversation_needs_no_project()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var result = await NewService(dbContext).SendAsync(null, "across everything, what is stuck?");

        Assert.Equal(FamiliarChatSendStatus.Accepted, result.Status);

        var chat = await dbContext.FamiliarChats.AsNoTracking().SingleAsync();
        Assert.Null(chat.FocusProjectId);
    }

    [Fact]
    public async Task A_focus_is_recorded_on_the_conversation_and_on_the_turn()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var result = await NewService(dbContext).SendAsync(null, "what about this one?", project.Id);

        var chat = await dbContext.FamiliarChats.AsNoTracking().SingleAsync();
        Assert.Equal(project.Id, chat.FocusProjectId);

        var turn = await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync();
        Assert.Equal(project.Id, turn.FocusProjectIdAtTime);
        Assert.Equal(result.ChatId, chat.Id);
    }

    /// <summary>
    /// The focus is a lean, not an owner. Deleting the project it names must lose the lean and keep
    /// the conversation — which is what the SetNull behaviour on the foreign key is for.
    /// </summary>
    [Fact]
    public async Task Deleting_the_focus_project_keeps_the_conversation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        await NewService(dbContext).SendAsync(null, "about this project", project.Id);

        dbContext.Projects.Remove(await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var chat = await dbContext.FamiliarChats.AsNoTracking().SingleAsync();
        Assert.Null(chat.FocusProjectId);

        // The turn's record of what the focus was at the time survives the project's deletion,
        // because it carries no foreign key. It is a historical fact, not a live pointer.
        var turn = await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync();
        Assert.Equal(project.Id, turn.FocusProjectIdAtTime);
    }

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task An_empty_message_writes_nothing(string message)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var result = await NewService(dbContext).SendAsync(null, message);

        Assert.Equal(FamiliarChatSendStatus.Invalid, result.Status);
        Assert.NotNull(result.ValidationMessage);
        Assert.Empty(await dbContext.FamiliarChats.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_over_long_message_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var result = await NewService(dbContext)
            .SendAsync(null, new string('x', FamiliarChatTurn.MaxUserTextLength + 1));

        Assert.Equal(FamiliarChatSendStatus.Invalid, result.Status);
        Assert.Empty(await dbContext.FamiliarChats.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Sending_to_an_unknown_conversation_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var result = await NewService(dbContext).SendAsync(Guid.NewGuid(), "hello?");

        Assert.Equal(FamiliarChatSendStatus.ChatNotFound, result.Status);
        Assert.Empty(await dbContext.FamiliarChatTurns.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- one turn in flight

    /// <summary>
    /// The structural rule: a second sender attaches to the reply that is running rather than
    /// queueing behind it, and their message is not written.
    /// </summary>
    [Fact]
    public async Task A_second_send_while_a_turn_is_in_flight_attaches_to_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var service = NewService(dbContext);
        var first = await service.SendAsync(null, "the first question");

        var second = await service.SendAsync(first.ChatId, "the second question");

        Assert.Equal(FamiliarChatSendStatus.Attached, second.Status);
        Assert.Equal(first.Sequence, second.Sequence);

        var turn = await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync();
        Assert.Equal("the first question", turn.UserText);
    }

    [Theory]
    [InlineData(FamiliarChatTurnState.Generating)]
    [InlineData(FamiliarChatTurnState.Pending)]
    public async Task Both_in_flight_states_hold_the_slot(FamiliarChatTurnState state)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var service = NewService(dbContext);
        var first = await service.SendAsync(null, "the first question");

        var turn = await dbContext.FamiliarChatTurns.SingleAsync();
        turn.State = state;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(
            FamiliarChatSendStatus.Attached,
            (await service.SendAsync(first.ChatId, "the second question")).Status);
    }

    [Theory]
    [InlineData(FamiliarChatTurnState.Completed)]
    [InlineData(FamiliarChatTurnState.Failed)]
    public async Task A_settled_turn_releases_the_slot(FamiliarChatTurnState state)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var service = NewService(dbContext);
        var first = await service.SendAsync(null, "the first question");

        await SettleAsync(dbContext, state);

        var second = await service.SendAsync(first.ChatId, "the second question");

        Assert.Equal(FamiliarChatSendStatus.Accepted, second.Status);
        Assert.Equal(2, second.Sequence);
    }

    /// <summary>Sequence is monotonic within a conversation, and independent between conversations.</summary>
    [Fact]
    public async Task Sequences_are_per_conversation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        var first = await service.SendAsync(null, "first conversation");
        var second = await service.SendAsync(null, "second conversation");

        Assert.NotEqual(first.ChatId, second.ChatId);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(1, second.Sequence);

        await SettleAsync(dbContext, FamiliarChatTurnState.Completed);

        Assert.Equal(2, (await service.SendAsync(first.ChatId, "again")).Sequence);
    }

    // ---------------------------------------------------------------- resume

    /// <summary>
    /// The resume read is the same call for a client four seconds behind and one four hours behind.
    /// This asserts every cursor position over one conversation, including the two ends.
    /// </summary>
    [Fact]
    public async Task Reading_after_a_sequence_returns_exactly_what_follows_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        var chatId = await SeedThreeTurnConversationAsync(dbContext, service);

        var fromStart = await service.ReadTurnsAfterAsync(chatId, 0);
        Assert.NotNull(fromStart);
        Assert.Equal([1, 2, 3], fromStart.Turns.Select(turn => turn.Sequence));
        Assert.Equal(3, fromStart.LatestSequence);

        var midway = await service.ReadTurnsAfterAsync(chatId, 2);
        Assert.NotNull(midway);
        Assert.Equal([3], midway.Turns.Select(turn => turn.Sequence));
        Assert.Equal(3, midway.LatestSequence);

        // Fully caught up: no turns, and the true head of the conversation reported anyway. A client
        // that inferred its cursor from an empty list would sit on a stale one forever.
        var caughtUp = await service.ReadTurnsAfterAsync(chatId, 3);
        Assert.NotNull(caughtUp);
        Assert.Empty(caughtUp.Turns);
        Assert.Equal(3, caughtUp.LatestSequence);
        Assert.False(caughtUp.HasTurnInFlight);
    }

    /// <summary>A cursor past the end asks for nothing and is not an error.</summary>
    [Fact]
    public async Task A_cursor_beyond_the_conversation_returns_no_turns()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        var chatId = await SeedThreeTurnConversationAsync(dbContext, service);

        var page = await service.ReadTurnsAfterAsync(chatId, 99);

        Assert.NotNull(page);
        Assert.Empty(page.Turns);
        Assert.Equal(3, page.LatestSequence);
    }

    /// <summary>A negative cursor means "everything", the same as no cursor at all.</summary>
    [Fact]
    public async Task A_negative_cursor_is_normalised_rather_than_refused()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        var chatId = await SeedThreeTurnConversationAsync(dbContext, service);

        var page = await service.ReadTurnsAfterAsync(chatId, -5);

        Assert.NotNull(page);
        Assert.Equal(0, page.AfterSequence);
        Assert.Equal(3, page.Turns.Count);
    }

    /// <summary>
    /// A conversation that does not exist and a conversation with nothing new are different answers.
    /// Collapsing them would leave a client polling a deleted conversation forever.
    /// </summary>
    [Fact]
    public async Task Reading_an_unknown_conversation_is_null_rather_than_empty()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        Assert.Null(await NewService(dbContext).ReadTurnsAfterAsync(Guid.NewGuid(), 0));
    }

    [Fact]
    public async Task A_turn_in_flight_is_reported_to_a_caught_up_client()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        var sent = await service.SendAsync(null, "a question");

        var page = await service.ReadTurnsAfterAsync(sent.ChatId, 1);

        Assert.NotNull(page);
        Assert.Empty(page.Turns);
        Assert.True(page.HasTurnInFlight);
    }

    /// <summary>A partially generated turn is readable while it is still being written.</summary>
    [Fact]
    public async Task Partial_output_is_visible_before_the_turn_finishes()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        var sent = await service.SendAsync(null, "a question");

        var turn = await dbContext.FamiliarChatTurns.SingleAsync();
        turn.State = FamiliarChatTurnState.Generating;
        turn.Output = "half an ans";
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var view = await service.GetAsync(sent.ChatId);

        Assert.NotNull(view);
        Assert.Equal("half an ans", view.Turns[0].Output);
        Assert.True(view.Turns[0].IsInFlight);
        Assert.NotNull(view.InFlightTurn);
    }

    // ---------------------------------------------------------------- list

    [Fact]
    public async Task The_list_is_ordered_by_recent_activity()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Now);
        var service = NewService(dbContext, timeProvider: clock);

        var older = await service.SendAsync(null, "the older conversation");
        clock.Advance(TimeSpan.FromMinutes(5));
        var newer = await service.SendAsync(null, "the newer conversation");

        var listed = await service.ListAsync();

        Assert.Equal([newer.ChatId, older.ChatId], listed.Select(chat => chat.ChatId));
        Assert.True(listed[0].HasTurnInFlight);
        Assert.Equal(1, listed[0].TurnCount);
    }

    [Fact]
    public async Task A_conversation_is_titled_from_its_opening_message()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await NewService(dbContext).SendAsync(null, "  what   is\nblocked on Find Familiar?  ");

        var chat = await dbContext.FamiliarChats.AsNoTracking().SingleAsync();
        Assert.Equal("what is blocked on Find Familiar?", chat.Title);
    }

    [Fact]
    public async Task A_read_creates_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = NewService(dbContext);

        Assert.Null(await service.GetAsync(Guid.NewGuid()));
        Assert.Empty(await service.ListAsync());
        Assert.Empty(await dbContext.FamiliarChats.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarChatService NewService(
        FamiliarDbContext dbContext,
        FamiliarChatGenerationQueue? queue = null,
        TimeProvider? timeProvider = null) =>
        new(dbContext, queue ?? new FamiliarChatGenerationQueue(), timeProvider ?? new TestTimeProvider(Now));

    private static async Task<List<Guid>> ReadQueuedAsync(FamiliarChatGenerationQueue queue)
    {
        var queued = new List<Guid>();

        // Cancelled the moment the channel has nothing more to hand over, so the read drains what is
        // there without waiting for a writer that will never come.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        try
        {
            await foreach (var id in queue.ReadAllAsync(stop.Token))
            {
                queued.Add(id);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return queued;
    }

    private static async Task<Guid> SeedThreeTurnConversationAsync(
        FamiliarDbContext dbContext,
        IFamiliarChatService service)
    {
        var first = await service.SendAsync(null, "turn one");
        await SettleAsync(dbContext, FamiliarChatTurnState.Completed);
        await service.SendAsync(first.ChatId, "turn two");
        await SettleAsync(dbContext, FamiliarChatTurnState.Completed);
        await service.SendAsync(first.ChatId, "turn three");
        await SettleAsync(dbContext, FamiliarChatTurnState.Completed);

        return first.ChatId;
    }

    /// <summary>
    /// Moves every in-flight turn to a terminal state, standing in for the generation host. Doing it
    /// here rather than running the host keeps these tests about the service.
    /// </summary>
    private static async Task SettleAsync(FamiliarDbContext dbContext, FamiliarChatTurnState state)
    {
        var inFlight = await dbContext.FamiliarChatTurns
            .Where(turn =>
                turn.State == FamiliarChatTurnState.Pending
                || turn.State == FamiliarChatTurnState.Generating)
            .ToListAsync();

        foreach (var turn in inFlight)
        {
            turn.State = state;
            turn.Output = state == FamiliarChatTurnState.Completed ? "An answer." : "It failed.";
            turn.FailureCode = state == FamiliarChatTurnState.Failed ? "test-failure" : null;
            turn.CompletedUtc = Now.UtcDateTime;
        }

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Chat project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarChatServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }
}
