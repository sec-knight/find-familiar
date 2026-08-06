using System.Net;
using System.Text;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The provider's stream parsing, fed real event-stream bodies.
///
/// This file exists because of a defect it would have caught. The usage frame's cached-token field was
/// parsed into a record that nothing ever read, so <see cref="FamiliarChatStreamEvent.Finished"/>
/// carried a null cached count on every turn and the dashboard reported "not reported" — for months,
/// against a working endpoint that was sending the number. Every other test in this suite stubbed at
/// the <c>IFamiliarChatProvider</c> seam, which is above the parser, so nothing ever read a byte of
/// wire format.
///
/// Classification of HTTP statuses is covered in <c>FamiliarChatProviderContractTests</c>. What is
/// covered here is only what happens to a body once the response is a success.
/// </summary>
public sealed class OpenAiCompatibleFamiliarChatProviderStreamTests
{
    [Fact]
    public async Task Deltas_arrive_in_order_and_the_sentinel_completes_the_stream()
    {
        var events = await StreamAsync(
            Frame("""{"choices":[{"delta":{"content":"The "}}]}"""),
            Frame("""{"choices":[{"delta":{"content":"talk lane"}}]}"""),
            "data: [DONE]");

        Assert.Equal(
            ["The ", "talk lane"],
            events.OfType<FamiliarChatStreamEvent.Delta>().Select(delta => delta.Text));

        var finished = Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]);
        Assert.Equal(FamiliarChatProviderStatus.Completed, finished.Status);
    }

    /// <summary>
    /// The regression. A usage frame reporting cached input must reach the terminal event, because
    /// that number is the entire evidence that prefix caching — the reason the prompt is ordered
    /// stable-to-volatile at all — is working.
    /// </summary>
    [Fact]
    public async Task A_usage_frame_reaches_the_terminal_event()
    {
        var events = await StreamAsync(
            Frame("""{"model":"grok-4.20-non-reasoning","choices":[{"delta":{"content":"hi"}}]}"""),
            Frame("""
                  {"usage":{"prompt_tokens":4120,"completion_tokens":85,
                   "prompt_tokens_details":{"cached_tokens":3900}}}
                  """),
            "data: [DONE]");

        var finished = Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]);
        Assert.Equal(4120, finished.InputTokens);
        Assert.Equal(85, finished.OutputTokens);
        Assert.Equal(3900, finished.CachedInputTokens);
        Assert.Equal("grok-4.20-non-reasoning", finished.Model);
    }

    /// <summary>
    /// xAI spells the cached count differently from OpenAI. Both are accepted, for the same reason the
    /// refusal signal accepts two spellings: this shape is a convention, not a specification.
    /// </summary>
    [Fact]
    public async Task The_xai_spelling_of_the_cached_count_is_accepted()
    {
        var events = await StreamAsync(
            Frame("""
                  {"usage":{"prompt_tokens":200,"completion_tokens":10,
                   "prompt_tokens_details":{"cached_prompt_text_tokens":128}}}
                  """),
            "data: [DONE]");

        Assert.Equal(128, Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]).CachedInputTokens);
    }

    /// <summary>
    /// Absent means unknown, not zero. A zero would be a claim that nothing was cached, and the
    /// dashboard reports the two differently on purpose.
    /// </summary>
    [Fact]
    public async Task An_absent_cached_count_stays_null_rather_than_becoming_zero()
    {
        var events = await StreamAsync(
            Frame("""{"usage":{"prompt_tokens":200,"completion_tokens":10}}"""),
            "data: [DONE]");

        var finished = Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]);
        Assert.Equal(200, finished.InputTokens);
        Assert.Null(finished.CachedInputTokens);
    }

    /// <summary>
    /// A stream that stops without the sentinel has not completed, and a truncated reply must not be
    /// presented as a whole one — but whatever already arrived is real and is kept.
    /// </summary>
    [Fact]
    public async Task A_stream_ending_without_the_sentinel_is_not_completed()
    {
        var events = await StreamAsync(Frame("""{"choices":[{"delta":{"content":"half a sen"}}]}"""));

        Assert.Equal("half a sen", Assert.IsType<FamiliarChatStreamEvent.Delta>(events[0]).Text);
        Assert.NotEqual(
            FamiliarChatProviderStatus.Completed,
            Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]).Status);
    }

    /// <summary>
    /// Keep-alive comments and blank separators are transport, not content. Treating one as malformed
    /// would kill a healthy stream on an idle endpoint.
    /// </summary>
    [Fact]
    public async Task Keepalive_and_blank_lines_are_not_content()
    {
        var events = await StreamAsync(
            ": ping",
            string.Empty,
            Frame("""{"choices":[{"delta":{"content":"still here"}}]}"""),
            string.Empty,
            "data: [DONE]");

        Assert.Equal("still here", Assert.Single(events.OfType<FamiliarChatStreamEvent.Delta>()).Text);
        Assert.Equal(
            FamiliarChatProviderStatus.Completed,
            Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]).Status);
    }

    /// <summary>
    /// One unreadable frame is not a reason to discard a reply that is otherwise arriving correctly.
    /// </summary>
    [Fact]
    public async Task One_unreadable_frame_does_not_discard_the_reply()
    {
        var events = await StreamAsync(
            Frame("""{"choices":[{"delta":{"content":"before"}}]}"""),
            "data: {not json at all",
            Frame("""{"choices":[{"delta":{"content":" after"}}]}"""),
            "data: [DONE]");

        Assert.Equal(
            "before after",
            string.Concat(events.OfType<FamiliarChatStreamEvent.Delta>().Select(delta => delta.Text)));
        Assert.Equal(
            FamiliarChatProviderStatus.Completed,
            Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]).Status);
    }

    /// <summary>A refusal is an outcome, not a fault, and it is reported as one.</summary>
    [Fact]
    public async Task A_refusal_is_declined()
    {
        var events = await StreamAsync(
            Frame("""{"choices":[{"delta":{"refusal":"I will not."}}]}"""),
            "data: [DONE]");

        Assert.Equal(
            FamiliarChatProviderStatus.Declined,
            Assert.IsType<FamiliarChatStreamEvent.Finished>(events[^1]).Status);
    }

    /// <summary>
    /// Exactly one terminal event on every path. The generator classifies a stream without one as
    /// malformed, so a second would corrupt a turn as surely as none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exactly_one_terminal_event_is_emitted(bool sentinel)
    {
        var lines = sentinel
            ? new[] { Frame("""{"choices":[{"delta":{"content":"x"}}]}"""), "data: [DONE]" }
            : [Frame("""{"choices":[{"delta":{"content":"x"}}]}""")];

        Assert.Single(await StreamAsync(lines), streamEvent => streamEvent is FamiliarChatStreamEvent.Finished);
    }

    // ------------------------------------------------------------------------------- infrastructure

    /// <summary>
    /// One event-stream frame. Newlines inside the JSON are collapsed so a raw string literal can be
    /// laid out readably in a test without emitting an illegal multi-line frame.
    /// </summary>
    private static string Frame(string json) =>
        "data: " + string.Concat(json.Split('\n').Select(line => line.Trim()));

    private static async Task<IReadOnlyList<FamiliarChatStreamEvent>> StreamAsync(params string[] lines)
    {
        var body = string.Join("\n\n", lines) + "\n\n";

        using var httpClient = new HttpClient(new StubHandler(body))
        {
            BaseAddress = new Uri("https://stub.invalid/v1/")
        };

        var provider = new OpenAiCompatibleFamiliarChatProvider(
            httpClient,
            Options.Create(new FamiliarChatOptions
            {
                Provider = FamiliarChatOptions.XaiProvider,
                Model = "test-model",
                ApiKeyVariable = "STUB_KEY_VARIABLE_THAT_IS_NOT_SET"
            }));

        var events = new List<FamiliarChatStreamEvent>();

        await foreach (var streamEvent in provider.StreamAsync(
                           new FamiliarChatRequest("system", [], "hello")))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });
    }
}
