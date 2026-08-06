using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Detached generation, driven through the real hosted-service lifecycle rather than by calling its
/// internals — so what these tests exercise is the path a running server takes.
///
/// The property under test is the one Sprint 12 called structural from commit one: a turn generates
/// because it is a durable row, not because a connection is waiting on it. Everything here follows
/// from that. Nobody is listening in any of these tests, and every turn still reaches a terminal
/// state; a process that dies mid-generation leaves a turn that the next process classifies honestly
/// and, critically, releases the in-flight slot for — otherwise a conversation would be permanently
/// unable to accept another turn.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatGenerationHostTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Generous, because it bounds a failure rather than a pass; a pass returns at once.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The whole slice in one test: a send returns, nothing is listening, and the turn still reaches
    /// a terminal state written by a different scope entirely.
    /// </summary>
    [Fact]
    public async Task A_sent_turn_generates_with_nobody_listening()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var queue = new FamiliarChatGenerationQueue();
        var generator = new ScriptedChatGenerator();
        generator.EnqueueCompletion("Half an answer. ", "The rest of it.");

        await using var harness = new Harness(database, queue, generator);
        await harness.StartAsync();

        var sent = await new FamiliarChatService(dbContext, queue, new TestTimeProvider(Now))
            .SendAsync(null, "what is blocked?");

        var turn = await WaitForTerminalAsync(dbContext, sent.ChatId);

        Assert.Equal(FamiliarChatTurnState.Completed, turn.State);
        Assert.Equal("Half an answer. The rest of it.", turn.Output);
        Assert.Null(turn.FailureCode);
        Assert.NotNull(turn.StartedUtc);
        Assert.NotNull(turn.CompletedUtc);
    }

    /// <summary>
    /// With nothing configured, generation still runs end to end and says the one sentence that is
    /// true. No credential is required to run this application at all.
    /// </summary>
    [Fact]
    public async Task With_no_provider_configured_the_turn_fails_honestly()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var queue = new FamiliarChatGenerationQueue();

        await using var harness = new Harness(database, queue, new UnconfiguredFamiliarChatGenerator());
        await harness.StartAsync();

        var sent = await new FamiliarChatService(dbContext, queue, new TestTimeProvider(Now))
            .SendAsync(null, "what is blocked?");

        var turn = await WaitForTerminalAsync(dbContext, sent.ChatId);

        Assert.Equal(FamiliarChatTurnState.Failed, turn.State);
        Assert.Equal(UnconfiguredFamiliarChatGenerator.FailureCode, turn.FailureCode);
        Assert.Equal(UnconfiguredFamiliarChatGenerator.Sentence, turn.Output);
    }

    /// <summary>
    /// The interface says a generator never throws. One that does must still leave a terminal turn,
    /// because a turn stuck Generating holds the conversation's only in-flight slot forever.
    /// </summary>
    [Fact]
    public async Task A_generator_that_throws_still_leaves_a_terminal_turn()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var queue = new FamiliarChatGenerationQueue();
        var generator = new ScriptedChatGenerator();
        generator.EnqueueThrow(new InvalidOperationException("generator exploded"));

        await using var harness = new Harness(database, queue, generator);
        await harness.StartAsync();

        var service = new FamiliarChatService(dbContext, queue, new TestTimeProvider(Now));
        var sent = await service.SendAsync(null, "what is blocked?");

        var turn = await WaitForTerminalAsync(dbContext, sent.ChatId);

        Assert.Equal(FamiliarChatTurnState.Failed, turn.State);
        Assert.Equal(FamiliarChatGenerationHost.FaultedFailureCode, turn.FailureCode);

        // Nothing the exception said reached the row. The sentence is this application's own.
        Assert.DoesNotContain("exploded", turn.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FamiliarChatGenerationHost.FaultedSentence, turn.Output);

        // And the slot is released, so the conversation is usable again.
        Assert.Equal(FamiliarChatSendStatus.Accepted, (await service.SendAsync(sent.ChatId, "again?")).Status);
    }

    // ---------------------------------------------------------------- restart recovery

    /// <summary>
    /// A turn left Generating by a process that died. The next process classifies it rather than
    /// restarting it — the generator that held it is gone, its partial output is already in the row,
    /// and re-running would append a second reply to the same turn.
    /// </summary>
    [Fact]
    public async Task A_turn_left_generating_by_a_dead_process_is_failed_at_startup()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedConversationAsync(dbContext, FamiliarChatTurnState.Generating, "half an ans");

        var queue = new FamiliarChatGenerationQueue();
        await using var harness = new Harness(database, queue, new ScriptedChatGenerator());
        await harness.StartAsync();

        var turn = await WaitForTerminalAsync(dbContext, chatId);

        Assert.Equal(FamiliarChatTurnState.Failed, turn.State);
        Assert.Equal(FamiliarChatGenerationHost.InterruptedFailureCode, turn.FailureCode);

        // The partial output is kept. It is what the person already saw, and deleting it would make
        // the transcript disagree with the screen they were looking at.
        Assert.Equal("half an ans", turn.Output);
    }

    [Fact]
    public async Task An_interrupted_turn_with_no_output_says_so()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedConversationAsync(dbContext, FamiliarChatTurnState.Generating, string.Empty);

        var queue = new FamiliarChatGenerationQueue();
        await using var harness = new Harness(database, queue, new ScriptedChatGenerator());
        await harness.StartAsync();

        var turn = await WaitForTerminalAsync(dbContext, chatId);

        Assert.Equal(FamiliarChatGenerationHost.InterruptedSentence, turn.Output);
    }

    /// <summary>
    /// Recovery must release the in-flight slot, not merely record a failure. A conversation whose
    /// slot is never released can never be spoken to again.
    /// </summary>
    [Fact]
    public async Task Recovery_releases_the_in_flight_slot()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedConversationAsync(dbContext, FamiliarChatTurnState.Generating, "partial");

        var queue = new FamiliarChatGenerationQueue();
        await using var harness = new Harness(database, queue, new ScriptedChatGenerator());
        await harness.StartAsync();
        await WaitForTerminalAsync(dbContext, chatId);

        var result = await new FamiliarChatService(dbContext, queue, new TestTimeProvider(Now))
            .SendAsync(chatId, "carry on");

        Assert.Equal(FamiliarChatSendStatus.Accepted, result.Status);
        Assert.Equal(2, result.Sequence);
    }

    /// <summary>
    /// A Pending turn whose scheduling hint died with the in-memory queue. The row is still durable
    /// and still valid, so the sweep puts it back on — losing the channel costs scheduling, never a
    /// turn.
    /// </summary>
    [Fact]
    public async Task A_pending_turn_that_was_never_scheduled_is_picked_up_at_startup()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedConversationAsync(dbContext, FamiliarChatTurnState.Pending, string.Empty);

        var generator = new ScriptedChatGenerator();
        generator.EnqueueCompletion("Answered after a restart.");

        // A queue with nothing in it, exactly as a fresh process has.
        await using var harness = new Harness(database, new FamiliarChatGenerationQueue(), generator);
        await harness.StartAsync();

        var turn = await WaitForTerminalAsync(dbContext, chatId);

        Assert.Equal(FamiliarChatTurnState.Completed, turn.State);
        Assert.Equal("Answered after a restart.", turn.Output);
    }

    /// <summary>
    /// Idempotent by state, which is what makes re-enqueueing safe whether it comes from the sweep or
    /// from a duplicate send. A settled turn is never regenerated over the top of its own record.
    /// </summary>
    [Fact]
    public async Task A_settled_turn_is_not_regenerated()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedConversationAsync(dbContext, FamiliarChatTurnState.Completed, "The original answer.");
        var turnId = (await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync()).Id;

        var generator = new ScriptedChatGenerator();
        generator.EnqueueCompletion("A second answer that must never be written.");

        var queue = new FamiliarChatGenerationQueue();
        await using var harness = new Harness(database, queue, generator);
        await harness.StartAsync();

        queue.Enqueue(turnId);

        // Nothing to wait for, so give the host a real opportunity to do the wrong thing.
        await Task.Delay(250);

        var turn = await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync();
        Assert.Equal("The original answer.", turn.Output);
        Assert.Equal(0, generator.CallCount);
    }

    /// <summary>Output beyond the cap is discarded rather than allowed to fail the write.</summary>
    [Fact]
    public async Task Output_past_the_cap_is_discarded_and_the_turn_still_completes()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var queue = new FamiliarChatGenerationQueue();
        var generator = new ScriptedChatGenerator();
        generator.EnqueueCompletion(
            new string('a', FamiliarChatTurn.MaxOutputLength - 10),
            new string('b', 50));

        await using var harness = new Harness(database, queue, generator);
        await harness.StartAsync();

        var sent = await new FamiliarChatService(dbContext, queue, new TestTimeProvider(Now))
            .SendAsync(null, "say a lot");

        var turn = await WaitForTerminalAsync(dbContext, sent.ChatId);

        Assert.Equal(FamiliarChatTurnState.Completed, turn.State);
        Assert.Equal(FamiliarChatTurn.MaxOutputLength, turn.Output.Length);
        Assert.EndsWith(new string('b', 10), turn.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Polls the database from an independent context until the conversation's turn is terminal.
    ///
    /// An independent read on purpose: the assertion is that a different scope committed the state,
    /// which is what "detached from the requesting connection" has to mean to be worth anything.
    /// </summary>
    private static async Task<FamiliarChatTurn> WaitForTerminalAsync(FamiliarDbContext dbContext, Guid chatId)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var turn = await dbContext.FamiliarChatTurns
                .AsNoTracking()
                .Where(candidate => candidate.ChatId == chatId)
                .OrderByDescending(candidate => candidate.Sequence)
                .FirstOrDefaultAsync();

            if (turn is not null
                && turn.State is FamiliarChatTurnState.Completed or FamiliarChatTurnState.Failed)
            {
                return turn;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"No turn in conversation {chatId} reached a terminal state in {SettleTimeout}.");
    }

    private static async Task<Guid> SeedConversationAsync(
        FamiliarDbContext dbContext,
        FamiliarChatTurnState state,
        string output)
    {
        var nowUtc = Now.UtcDateTime;

        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Seeded for FamiliarChatGenerationHostTests",
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        dbContext.FamiliarChats.Add(chat);
        dbContext.FamiliarChatTurns.Add(new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Sequence = 1,
            State = state,
            UserText = "Seeded for FamiliarChatGenerationHostTests.",
            Output = output,
            CreatedUtc = nowUtc,
            StartedUtc = state == FamiliarChatTurnState.Pending ? null : nowUtc,
            CompletedUtc = state is FamiliarChatTurnState.Completed or FamiliarChatTurnState.Failed
                ? nowUtc
                : null
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return chat.Id;
    }

    /// <summary>
    /// A real service provider and a real running host over the test database, so the tests above go
    /// through <c>ExecuteAsync</c> and its startup sweep exactly as a running server does.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly FamiliarChatGenerationHost _host;

        public Harness(
            TemporarySqliteDatabase database,
            FamiliarChatGenerationQueue queue,
            IFamiliarChatGenerator generator)
        {
            var services = new ServiceCollection();
            services.AddDbContext<FamiliarDbContext>(options => options.UseSqlite(database.ConnectionString));
            services.AddSingleton<IFamiliarChatGenerator>(generator);

            _services = services.BuildServiceProvider();

            _host = new FamiliarChatGenerationHost(
                queue,
                _services.GetRequiredService<IServiceScopeFactory>(),
                new TestTimeProvider(Now),
                NullLogger<FamiliarChatGenerationHost>.Instance);
        }

        public Task StartAsync() => _host.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
            await _services.DisposeAsync();
        }
    }

    /// <summary>
    /// A generator whose behaviour a test states outright. Fragments are appended through the real
    /// sink, so accumulation into the row is exercised rather than simulated.
    /// </summary>
    private sealed class ScriptedChatGenerator : IFamiliarChatGenerator
    {
        private readonly Queue<Func<IFamiliarChatOutputSink, Task<FamiliarChatGenerationOutcome>>> _script = new();

        public string Name => "Scripted";

        public int CallCount { get; private set; }

        public void EnqueueCompletion(params string[] fragments) =>
            _script.Enqueue(async sink =>
            {
                foreach (var fragment in fragments)
                {
                    await sink.AppendAsync(fragment);
                }

                return FamiliarChatGenerationOutcome.Completed;
            });

        public void EnqueueThrow(Exception exception) =>
            _script.Enqueue(_ => throw exception);

        public Task<FamiliarChatGenerationOutcome> GenerateAsync(
            FamiliarChatGenerationRequest request,
            IFamiliarChatOutputSink sink,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return _script.Count > 0
                ? _script.Dequeue()(sink)
                : Task.FromResult(FamiliarChatGenerationOutcome.Completed);
        }
    }
}
