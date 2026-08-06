using System.Net;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The SSE endpoint, through the real HTTP pipeline.
///
/// What is being protected here is the walk-out-the-door property: a reply is generated on the server
/// and a client can attach, detach and re-attach to it at any point without losing or duplicating
/// text. Every test below reads the stream from a cursor, which is the same request a phone makes
/// after a wifi handoff, a tab suspension, or a reload — there is only one resume path, so testing it
/// once tests all three.
///
/// The stream is a view of the persisted row, never the generation itself, so these tests move the
/// row directly rather than running a provider. That is the point of the separation: what a client
/// sees is a function of what is stored, and nothing else.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatStreamTests(FindFamiliarWebApplicationFactory factory)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task An_unknown_conversation_is_not_found_before_any_stream_begins()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/familiar/chats/{Guid.NewGuid()}/stream?after=0",
            HttpCompletionOption.ResponseHeadersRead);

        // A 404, not a 200 carrying an error inside a stream a client would have to parse.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_stream_announces_itself_as_an_event_stream()
    {
        var chatId = await SeedSettledConversationAsync("a settled question", "a settled answer");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/api/familiar/chats/{chatId}/stream?after=0",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Proxies buffer by default, which would turn a stream into one delivery at the end.
        Assert.Contains("no", response.Headers.GetValues("X-Accel-Buffering"));
    }

    /// <summary>
    /// A client arriving at a settled conversation gets the whole thing at once and the stream ends.
    /// There is nothing to wait for, and holding the connection open would be pretending otherwise.
    /// </summary>
    [Fact]
    public async Task A_settled_conversation_sends_its_turns_and_closes()
    {
        var chatId = await SeedSettledConversationAsync("what happened?", "this happened");

        var frames = await ReadFramesAsync(chatId, after: 0);

        Assert.NotEmpty(frames);

        var first = frames[0];
        Assert.Equal(1, first.GetProperty("turns").GetArrayLength());
        Assert.Equal("what happened?", first.GetProperty("turns")[0].GetProperty("userText").GetString());
        Assert.Equal("this happened", first.GetProperty("turns")[0].GetProperty("output").GetString());
        Assert.False(first.GetProperty("hasTurnInFlight").GetBoolean());
    }

    /// <summary>
    /// The gap is delivered first, before anything is waited for. A tab that slept through three
    /// exchanges is caught up by the same mechanism as a fresh connection.
    /// </summary>
    [Fact]
    public async Task A_client_far_behind_is_sent_everything_it_missed_first()
    {
        var chatId = await SeedSettledConversationAsync("turn one", "answer one");
        await AppendSettledTurnAsync(chatId, 2, "turn two", "answer two");
        await AppendSettledTurnAsync(chatId, 3, "turn three", "answer three");

        var frames = await ReadFramesAsync(chatId, after: 0);

        var turns = frames[0].GetProperty("turns");
        Assert.Equal(3, turns.GetArrayLength());
        Assert.Equal(3, frames[0].GetProperty("latestSequence").GetInt32());
    }

    [Fact]
    public async Task A_caught_up_client_is_told_the_head_and_sent_no_turns()
    {
        var chatId = await SeedSettledConversationAsync("the only turn", "the only answer");

        var frames = await ReadFramesAsync(chatId, after: 1);

        Assert.Equal(0, frames[0].GetProperty("turns").GetArrayLength());
        Assert.Equal(1, frames[0].GetProperty("latestSequence").GetInt32());
        Assert.Equal(1, frames[0].GetProperty("resumeCursor").GetInt32());
    }

    // ---------------------------------------------------------------- the walk-out-the-door path

    /// <summary>
    /// The load-bearing test for the sprint's acceptance question. A reply is growing in the database
    /// while nobody is attached; a client connects part way through and receives what already exists
    /// and then what follows, in order, without a gap.
    /// </summary>
    [Fact]
    public async Task A_client_attaching_mid_reply_receives_what_exists_and_then_what_follows()
    {
        var chatId = await SeedGeneratingConversationAsync("what is happening?", "The first part");

        var reader = ReadFramesAsync(chatId, after: 0, readToEnd: true);

        // The reply keeps growing with nobody's connection responsible for it — the generation is not
        // this connection's, and never was.
        await Task.Delay(400);
        await GrowOutputAsync(chatId, " and the second part.");
        await Task.Delay(400);
        await SettleAsync(chatId);

        var frames = await reader;

        // The first frame carries what already existed at the moment of attaching.
        Assert.StartsWith(
            "The first part",
            frames[0].GetProperty("turns")[0].GetProperty("output").GetString(),
            StringComparison.Ordinal);

        // The last frame carries the whole reply. No fragment was lost between connecting and the
        // text that arrived after.
        var final = frames[^1].GetProperty("turns")[0];
        Assert.Equal("The first part and the second part.", final.GetProperty("output").GetString());
        Assert.Equal("Completed", final.GetProperty("state").GetString());
    }

    /// <summary>
    /// Disconnecting and reconnecting from the same cursor loses nothing and repeats nothing new — the
    /// wifi-to-cellular handoff, expressed as two requests.
    /// </summary>
    [Fact]
    public async Task Reconnecting_from_the_cursor_neither_loses_nor_skips_text()
    {
        var chatId = await SeedGeneratingConversationAsync("a question", "Part one");

        // First connection: attach, take what is there, then walk away mid-reply.
        var firstFrames = await ReadFramesAsync(chatId, after: 0, minimumFrames: 1);
        var cursor = firstFrames[^1].GetProperty("resumeCursor").GetInt32();

        // While nothing is attached at all, the reply finishes.
        await GrowOutputAsync(chatId, " and part two.");
        await SettleAsync(chatId);

        // Second connection, from the cursor the first one ended on.
        var secondFrames = await ReadFramesAsync(chatId, after: cursor);
        var turns = secondFrames[0].GetProperty("turns");

        // The in-flight turn is returned again rather than skipped, now complete. A cursor that had
        // advanced past it would have left the reply frozen half-written.
        Assert.Equal(1, turns.GetArrayLength());
        Assert.Equal("Part one and part two.", turns[0].GetProperty("output").GetString());
    }

    /// <summary>
    /// The cursor stops before a turn that is still arriving. This is the rule the page, the stream
    /// and the script all share, and getting it wrong is how a reply freezes half-written on a phone.
    /// </summary>
    [Fact]
    public async Task The_resume_cursor_stops_before_an_in_flight_turn()
    {
        var chatId = await SeedSettledConversationAsync("turn one", "answer one");
        await AppendGeneratingTurnAsync(chatId, 2, "turn two", "partial");

        var frames = await ReadFramesAsync(chatId, after: 0, minimumFrames: 1);

        Assert.Equal(2, frames[0].GetProperty("latestSequence").GetInt32());
        Assert.True(frames[0].GetProperty("hasTurnInFlight").GetBoolean());

        // One before the turn still arriving.
        Assert.Equal(1, frames[0].GetProperty("resumeCursor").GetInt32());
    }

    /// <summary>
    /// Two devices watching the same conversation both see it. There is no single connection that
    /// owns the work, which is what makes that possible.
    /// </summary>
    [Fact]
    public async Task Two_clients_watching_one_conversation_both_receive_it()
    {
        var chatId = await SeedGeneratingConversationAsync("a shared question", "Shared");

        var first = ReadFramesAsync(chatId, after: 0, readToEnd: true);
        var second = ReadFramesAsync(chatId, after: 0, readToEnd: true);

        await Task.Delay(400);
        await GrowOutputAsync(chatId, " answer.");
        await SettleAsync(chatId);

        var firstFrames = await first;
        var secondFrames = await second;

        Assert.Equal(
            "Shared answer.",
            firstFrames[^1].GetProperty("turns")[0].GetProperty("output").GetString());
        Assert.Equal(
            "Shared answer.",
            secondFrames[^1].GetProperty("turns")[0].GetProperty("output").GetString());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Reads <c>data:</c> frames off the stream until it closes or enough have arrived.
    ///
    /// Comment lines are skipped, which is what an <c>EventSource</c> does with them too — they are
    /// keep-alives and close notices, traffic rather than content.
    /// </summary>
    private async Task<List<JsonElement>> ReadFramesAsync(
        Guid chatId,
        int after,
        int minimumFrames = 1,
        bool readToEnd = false)
    {
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(ReadTimeout);

        using var response = await client.GetAsync(
            $"/api/familiar/chats/{chatId}/stream?after={after}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);

        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(body);

        var frames = new List<JsonElement>();

        // Reading to the end is the deterministic option where a test asserts on the *final* state:
        // the server closes the stream once nothing is in flight, so end-of-stream is the signal that
        // there is nothing more coming rather than a guess about how many frames that took.
        while (readToEnd || frames.Count < minimumFrames)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);

            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            frames.Add(JsonDocument.Parse(line["data:".Length..].Trim()).RootElement.Clone());
        }

        Assert.NotEmpty(frames);
        return frames;
    }

    private async Task<Guid> SeedSettledConversationAsync(string userText, string output)
    {
        var chatId = await CreateChatAsync();
        await AppendSettledTurnAsync(chatId, 1, userText, output);
        return chatId;
    }

    private async Task<Guid> SeedGeneratingConversationAsync(string userText, string partialOutput)
    {
        var chatId = await CreateChatAsync();
        await AppendGeneratingTurnAsync(chatId, 1, userText, partialOutput);
        return chatId;
    }

    private async Task<Guid> CreateChatAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var now = DateTime.UtcNow;
        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = $"Stream conversation {Guid.NewGuid():N}",
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.FamiliarChats.Add(chat);
        await dbContext.SaveChangesAsync();

        return chat.Id;
    }

    private Task AppendSettledTurnAsync(Guid chatId, int sequence, string userText, string output) =>
        AppendTurnAsync(chatId, sequence, FamiliarChatTurnState.Completed, userText, output);

    private Task AppendGeneratingTurnAsync(Guid chatId, int sequence, string userText, string output) =>
        AppendTurnAsync(chatId, sequence, FamiliarChatTurnState.Generating, userText, output);

    private async Task AppendTurnAsync(
        Guid chatId,
        int sequence,
        FamiliarChatTurnState state,
        string userText,
        string output)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var now = DateTime.UtcNow;

        dbContext.FamiliarChatTurns.Add(new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Sequence = sequence,
            State = state,
            UserText = userText,
            Output = output,
            CreatedUtc = now,
            StartedUtc = now,
            CompletedUtc = state == FamiliarChatTurnState.Completed ? now : null
        });

        await dbContext.SaveChangesAsync();
    }

    /// <summary>Stands in for the generation host writing into the row nobody is connected to.</summary>
    private async Task GrowOutputAsync(Guid chatId, string fragment)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var turn = await dbContext.FamiliarChatTurns
            .Where(candidate => candidate.ChatId == chatId)
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstAsync();

        turn.Output += fragment;
        await dbContext.SaveChangesAsync();
    }

    private async Task SettleAsync(Guid chatId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var turn = await dbContext.FamiliarChatTurns
            .Where(candidate => candidate.ChatId == chatId)
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstAsync();

        turn.State = FamiliarChatTurnState.Completed;
        turn.CompletedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
    }
}
