using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Services.Familiar.Chat.Brief;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using FindFamiliar.Server.Services.Familiar.Chat.Retrieval;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The part between a streaming provider and a persisted turn.
///
/// The property most of these protect is that a partial reply is never thrown away. A stream that
/// stops half way has produced something real — the person already read it — and a failure that
/// discarded it, or overwrote it with an explanation, would make the transcript disagree with the
/// screen.
///
/// The rest protect the boundary: nothing a provider says about a failure reaches a column, and
/// nothing about a project reaches a provider, because slice 2 sends no project state at all.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProviderFamiliarChatGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the ordinary path

    [Fact]
    public async Task A_completed_stream_becomes_a_completed_turn()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Emit("Nothing ", "is ", "blocked.");
        provider.Finish(FamiliarChatProviderStatus.Completed, "grok-4.1-fast-resolved", 1200, 42);

        var sink = new RecordingSink();
        var outcome = await NewGenerator(dbContext, provider)
            .GenerateAsync(Request(), sink);

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.FailureCode);
        Assert.Equal("Nothing is blocked.", sink.Text);

        // The model that actually answered, not the one configuration asked for.
        Assert.Equal("grok-4.1-fast-resolved", outcome.Metadata!.ProviderModel);
        Assert.Equal(1200, outcome.Metadata.InputTokens);
        Assert.Equal(42, outcome.Metadata.OutputTokens);
    }

    /// <summary>
    /// Fragments reach the sink as they arrive, not in one write at the end. This is the difference
    /// between a streamed reply and a slow one.
    /// </summary>
    [Fact]
    public async Task Fragments_are_appended_as_they_arrive()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Emit("one", "two", "three");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        var sink = new RecordingSink();
        await NewGenerator(dbContext, provider).GenerateAsync(Request(), sink);

        Assert.Equal(["one", "two", "three"], sink.Fragments);
    }

    // ---------------------------------------------------------------- partial replies

    /// <summary>
    /// The load-bearing test. A stream that fails after emitting text keeps the text, appends a short
    /// note, and writes no replacement sentence — so the host cannot overwrite what was read.
    /// </summary>
    [Theory]
    [InlineData(FamiliarChatProviderStatus.TimedOut)]
    [InlineData(FamiliarChatProviderStatus.Unavailable)]
    [InlineData(FamiliarChatProviderStatus.RateLimited)]
    public async Task A_stream_that_fails_part_way_keeps_what_arrived(FamiliarChatProviderStatus status)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Emit("Half an answ");
        provider.Finish(status);

        var sink = new RecordingSink();
        var outcome = await NewGenerator(dbContext, provider).GenerateAsync(Request(), sink);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FamiliarChatFailureWording.For(status).Code, outcome.FailureCode);

        // No replacement sentence: the host writes one only where no output exists.
        Assert.Null(outcome.Sentence);

        Assert.StartsWith("Half an answ", sink.Text, StringComparison.Ordinal);
        Assert.Contains("incomplete", sink.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stream_that_fails_before_saying_anything_carries_the_sentence()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Finish(FamiliarChatProviderStatus.Unauthenticated);

        var sink = new RecordingSink();
        var outcome = await NewGenerator(dbContext, provider).GenerateAsync(Request(), sink);

        Assert.False(outcome.Succeeded);
        Assert.Equal("chat-unauthenticated", outcome.FailureCode);
        Assert.Equal(FamiliarChatFailureWording.For(FamiliarChatProviderStatus.Unauthenticated).Sentence, outcome.Sentence);
        Assert.Empty(sink.Text);
    }

    /// <summary>
    /// A completed stream that said nothing is not a delivered empty reply. Recording it as one would
    /// put a silent bubble on the page and call it an answer.
    /// </summary>
    [Fact]
    public async Task A_completed_stream_with_no_text_is_a_failure()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Finish(FamiliarChatProviderStatus.Completed);

        var outcome = await NewGenerator(dbContext, provider).GenerateAsync(Request(), new RecordingSink());

        Assert.False(outcome.Succeeded);
        Assert.Equal("chat-empty-reply", outcome.FailureCode);
    }

    /// <summary>
    /// The interface promises exactly one terminal event. A stream that ends without one has broken
    /// that promise, and a truncated reply must not be recorded as a whole one.
    /// </summary>
    [Fact]
    public async Task A_stream_with_no_terminal_event_is_malformed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Emit("text with no ending");

        var outcome = await NewGenerator(dbContext, provider).GenerateAsync(Request(), new RecordingSink());

        Assert.False(outcome.Succeeded);
        Assert.Equal("chat-malformed", outcome.FailureCode);
    }

    // ---------------------------------------------------------------- what is sent

    /// <summary>
    /// The chokepoint test. Slice 2 sends no project state, and the way to prove it is to put a
    /// project in the database and assert that nothing about it appears in the request.
    /// </summary>
    [Fact]
    public async Task No_project_state_reaches_the_provider()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        dbContext.Projects.Add(new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = "Sasquatch Telemetry Overhaul",
            Purpose = "A distinctive purpose that must not leave this machine in slice 2.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var provider = new ScriptedChatProvider();
        provider.Emit("ok");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        await NewGenerator(dbContext, provider).GenerateAsync(Request(), new RecordingSink());

        var sent = provider.LastRequest!;
        var everything = sent.SystemPrompt + sent.UserMessage + (sent.StandingBrief ?? string.Empty)
            + string.Concat(sent.History.Select(turn => turn.UserText + turn.Output));

        // With an empty brief, no project reaches the wire at all. Which project state a *populated*
        // brief may carry — and which it must never — is asserted in FamiliarStandingBriefTests.
        Assert.DoesNotContain("Sasquatch", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("distinctive purpose", everything, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The brief travels as its own segment rather than folded into the constant system prompt, so a
    /// project edit cannot invalidate the cache entry for the part that never changes.
    /// </summary>
    [Fact]
    public async Task The_standing_brief_is_a_separate_segment_from_the_system_prompt()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Emit("ok");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        await NewGenerator(dbContext, provider).GenerateAsync(Request(), new RecordingSink());

        var sent = provider.LastRequest!;

        Assert.Equal(FamiliarChatSystemPrompt.Text, sent.SystemPrompt);
        Assert.NotNull(sent.StandingBrief);
        Assert.DoesNotContain(sent.StandingBrief!, sent.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_system_prompt_leads_and_the_user_message_is_last()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedChatProvider();
        provider.Emit("ok");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        await NewGenerator(dbContext, provider).GenerateAsync(Request("the question"), new RecordingSink());

        Assert.Equal(FamiliarChatSystemPrompt.Text, provider.LastRequest!.SystemPrompt);
        Assert.Equal("the question", provider.LastRequest.UserMessage);
    }

    /// <summary>
    /// Retrieval reaches the wire without the model asking for it. ADR-0014's reason for searching
    /// server-side rather than through a tool call is that a tool can silently fail to fire; this
    /// asserts the thing that replaced it actually fires.
    /// </summary>
    [Fact]
    public async Task Recorded_context_is_searched_and_sent_without_the_model_asking()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var projectId = Guid.NewGuid();

        dbContext.Projects.Add(new FamiliarProject
        {
            Id = projectId,
            Name = "Find Familiar",
            Purpose = "Preserve context.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        });

        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = ContextEntryKind.Decision,
            Title = "Separate the talk lane from the runner",
            Content = "They are different seams with different providers.",
            State = ContextEntryState.Active,
            CreatedUtc = Now.UtcDateTime
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var provider = new ScriptedChatProvider();
        provider.Emit("ok");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        await NewGenerator(dbContext, provider)
            .GenerateAsync(Request("why is the talk lane separate from the runner?"), new RecordingSink());

        var recorded = provider.LastRequest!.RecordedContext;

        Assert.NotNull(recorded);
        Assert.Contains("different seams", recorded!, StringComparison.Ordinal);

        // Its own segment, not folded into the constant head. This block changes every message, and
        // putting it in the stable head would invalidate the prefix cache on every single turn.
        Assert.DoesNotContain(recorded, provider.LastRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(recorded, provider.LastRequest.StandingBrief ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prior exchanges are sent so the conversation continues, oldest first, and bounded by a count
    /// rather than allowed to grow without limit.
    /// </summary>
    [Fact]
    public async Task Completed_history_is_sent_oldest_first_and_capped()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);

        var total = ProviderFamiliarChatGenerator.MaxHistoryTurns + 5;

        for (var sequence = 1; sequence <= total; sequence++)
        {
            await AddTurnAsync(dbContext, chatId, sequence, FamiliarChatTurnState.Completed, $"asked {sequence}", $"answered {sequence}");
        }

        var provider = new ScriptedChatProvider();
        provider.Emit("ok");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        await NewGenerator(dbContext, provider)
            .GenerateAsync(
                new FamiliarChatGenerationRequest(chatId, Guid.NewGuid(), total + 1, "next question", null),
                new RecordingSink());

        var history = provider.LastRequest!.History;

        Assert.Equal(ProviderFamiliarChatGenerator.MaxHistoryTurns, history.Count);

        // Oldest first, and it is the *most recent* window that was kept.
        Assert.Equal("asked 6", history[0].UserText);
        Assert.Equal($"asked {total}", history[^1].UserText);
    }

    /// <summary>
    /// A failed turn's output is this application's own sentence about a component the Familiar
    /// cannot observe. Re-feeding it would teach a model to imitate error text.
    /// </summary>
    [Fact]
    public async Task Failed_turns_are_not_sent_as_history()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);
        await AddTurnAsync(dbContext, chatId, 1, FamiliarChatTurnState.Completed, "asked one", "answered one");
        await AddTurnAsync(
            dbContext, chatId, 2, FamiliarChatTurnState.Failed, "asked two",
            "The conversational provider could not be reached, so there is no reply.");

        var provider = new ScriptedChatProvider();
        provider.Emit("ok");
        provider.Finish(FamiliarChatProviderStatus.Completed);

        await NewGenerator(dbContext, provider)
            .GenerateAsync(
                new FamiliarChatGenerationRequest(chatId, Guid.NewGuid(), 3, "asked three", null),
                new RecordingSink());

        var history = provider.LastRequest!.History;

        Assert.Single(history);
        Assert.Equal("asked one", history[0].UserText);
    }

    // ---------------------------------------------------------------- helpers

    private static ProviderFamiliarChatGenerator NewGenerator(
        FamiliarDbContext dbContext,
        IFamiliarChatProvider provider,
        IFamiliarStandingBriefService? briefs = null,
        IFamiliarContextRetrievalService? retrieval = null) =>
        new(dbContext,
            provider,
            briefs ?? new EmptyStandingBriefService(),
            retrieval ?? new FamiliarContextRetrievalService(dbContext));

    /// <summary>
    /// A brief with nothing in it, so these tests stay about the generator rather than about what the
    /// brief happens to contain. The brief's own behaviour is asserted in
    /// <c>FamiliarStandingBriefTests</c>.
    /// </summary>
    private sealed class EmptyStandingBriefService : IFamiliarStandingBriefService
    {
        public Task<FamiliarStandingBrief> GetBriefAsync(
            Guid? focusProjectId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FamiliarStandingBrief([], 0, 0, 0, [], DateTimeOffset.UnixEpoch));
    }

    private static FamiliarChatGenerationRequest Request(string message = "a question") =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, message, null);

    private static async Task<Guid> SeedChatAsync(FamiliarDbContext dbContext)
    {
        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Seeded for ProviderFamiliarChatGeneratorTests",
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        dbContext.FamiliarChats.Add(chat);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return chat.Id;
    }

    private static async Task AddTurnAsync(
        FamiliarDbContext dbContext,
        Guid chatId,
        int sequence,
        FamiliarChatTurnState state,
        string userText,
        string output)
    {
        dbContext.FamiliarChatTurns.Add(new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Sequence = sequence,
            State = state,
            UserText = userText,
            Output = output,
            CreatedUtc = Now.UtcDateTime,
            CompletedUtc = Now.UtcDateTime
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    /// <summary>Records what was appended, and in how many pieces.</summary>
    private sealed class RecordingSink : IFamiliarChatOutputSink
    {
        public List<string> Fragments { get; } = [];

        public string Text => string.Concat(Fragments);

        public Task AppendAsync(string fragment, CancellationToken cancellationToken = default)
        {
            Fragments.Add(fragment);
            return Task.CompletedTask;
        }
    }

    /// <summary>A provider whose whole stream a test states outright.</summary>
    private sealed class ScriptedChatProvider : IFamiliarChatProvider
    {
        private readonly List<FamiliarChatStreamEvent> _events = [];

        public string Name => "Scripted";

        public string Model => "scripted-model-1";

        public FamiliarChatRequest? LastRequest { get; private set; }

        public void Emit(params string[] fragments)
        {
            foreach (var fragment in fragments)
            {
                _events.Add(new FamiliarChatStreamEvent.Delta(fragment));
            }
        }

        public void Finish(
            FamiliarChatProviderStatus status,
            string? model = null,
            int? promptTokens = null,
            int? completionTokens = null) =>
            _events.Add(new FamiliarChatStreamEvent.Finished(status, model, promptTokens, completionTokens));

        public async IAsyncEnumerable<FamiliarChatStreamEvent> StreamAsync(
            FamiliarChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            foreach (var streamEvent in _events)
            {
                // A real await, so the caller genuinely iterates asynchronously rather than draining a
                // list that was complete before it started.
                await Task.Yield();
                yield return streamEvent;
            }
        }
    }
}
