using System.Net;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The portable reasoning provider, driven entirely through a stub handler.
///
/// <b>No test here opens a socket.</b> Every response is scripted in-process, so the whole provider —
/// every status mapping, the refusal check, the parsing, the redaction — is exercised with no
/// endpoint running, no credential present and no network available. That property is what makes this
/// suite safe to run anywhere, which matters for a repository other people will clone.
/// </summary>
public sealed class OpenAiCompatibleReasoningProviderTests
{
    // ---------------------------------------------------------------- the request that goes out

    /// <summary>
    /// The guarantee that survives every endpoint this can be pointed at: no tool is declared, so
    /// there is no execution surface whatever a reply says.
    /// </summary>
    [Fact]
    public async Task The_request_declares_no_tools()
    {
        var handler = Answering("All quiet.");
        await NewProvider(handler).RespondAsync(NewRequest());

        var body = handler.LastRequestBody!;

        Assert.DoesNotContain("\"tools\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_choice", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("function", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_request_sends_the_contract_as_system_and_the_snapshot_as_user()
    {
        var handler = Answering("All quiet.");
        await NewProvider(handler).RespondAsync(NewRequest());

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = document.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        // One copy of the contract, owned by the server, sent verbatim.
        Assert.Equal(FamiliarBehaviorContract.Text, messages[0].GetProperty("content").GetString());

        var user = messages[1].GetProperty("content").GetString()!;
        Assert.Contains("<project_snapshot>", user, StringComparison.Ordinal);
        Assert.Contains("<question>", user, StringComparison.Ordinal);
        Assert.Contains("why is this blocked?", user, StringComparison.Ordinal);
    }

    /// <summary>
    /// Structured output is what makes a small local model emit valid JSON reliably, so it is on by
    /// default — and it carries the server's one schema, not a second copy.
    /// </summary>
    [Fact]
    public async Task Structured_output_is_requested_by_default_and_carries_the_shared_schema()
    {
        var handler = Answering("All quiet.");
        await NewProvider(handler).RespondAsync(NewRequest());

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var format = document.RootElement.GetProperty("response_format");

        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());

        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schema.GetProperty("properties").TryGetProperty("reply", out _));
    }

    /// <summary>Endpoints that cannot honour it can turn it off; the reply is validated regardless.</summary>
    [Fact]
    public async Task Structured_output_can_be_turned_off_for_endpoints_that_reject_it()
    {
        var handler = Answering("All quiet.");
        await NewProvider(handler, options => options.UseStructuredOutput = false).RespondAsync(NewRequest());

        Assert.DoesNotContain("response_format", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- answers

    [Fact]
    public async Task A_well_formed_answer_is_parsed()
    {
        var taskId = Guid.NewGuid();

        var handler = Scripted(HttpStatusCode.OK, Completion($$"""
            {"reply":"It is blocked because no worker declares Implementer.",
             "action":{"kind":"StartPlanner","title":null,"requestedOutcome":null,"targetTaskId":"{{taskId}}"},
             "evidence":["{{taskId}}"]}
            """));

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.Answered, outcome.Status);
        Assert.Equal("It is blocked because no worker declares Implementer.", outcome.Reply);

        var draft = Assert.Single(outcome.Actions);
        Assert.Equal("StartPlanner", draft.Kind);
        Assert.Equal(taskId, draft.TargetTaskId);

        Assert.Equal([taskId], outcome.EvidenceIds);
        Assert.Null(outcome.Detail);
    }

    [Fact]
    public async Task An_unknown_action_kind_yields_no_draft_but_keeps_the_reply()
    {
        var handler = Scripted(HttpStatusCode.OK, Completion("""
            {"reply":"Here is what I found.",
             "action":{"kind":"DeleteEverything","title":"x","requestedOutcome":"y","targetTaskId":null},
             "evidence":null}
            """));

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.Answered, outcome.Status);
        Assert.Equal("Here is what I found.", outcome.Reply);
        Assert.Empty(outcome.Actions);
    }

    [Fact]
    public async Task Unparseable_evidence_identifiers_are_dropped()
    {
        var real = Guid.NewGuid();

        var handler = Scripted(HttpStatusCode.OK, Completion($$"""
            {"reply":"A reply.","action":null,"evidence":["{{real}}","not-a-guid","",""]}
            """));

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal([real], outcome.EvidenceIds);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"reply\":\"\"}")]
    [InlineData("{\"reply\":\"   \"}")]
    [InlineData("{\"nothing\":true}")]
    public async Task An_unusable_reply_is_reported_as_malformed(string content)
    {
        var handler = Scripted(HttpStatusCode.OK, Completion(content));

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.Malformed, outcome.Status);
        Assert.Null(outcome.Reply);
    }

    // ---------------------------------------------------------------- refusal

    /// <summary>A decline is a real outcome, and it is detected before any content is read.</summary>
    [Fact]
    public async Task A_refusal_field_is_reported_as_declined_without_reading_content()
    {
        var handler = Scripted(HttpStatusCode.OK, """
            {"model":"m","choices":[{"message":{"content":"{\"reply\":\"ignored\"}","refusal":"I cannot help with that."},"finish_reason":"stop"}]}
            """);

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.Declined, outcome.Status);
        Assert.Null(outcome.Reply);

        // The provider's own refusal text is never carried through.
        Assert.DoesNotContain("I cannot help", outcome.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_content_filter_finish_reason_is_reported_as_declined()
    {
        var handler = Scripted(HttpStatusCode.OK, """
            {"model":"m","choices":[{"message":{"content":null,"refusal":null},"finish_reason":"content_filter"}]}
            """);

        Assert.Equal(
            FamiliarReasoningStatus.Declined,
            (await NewProvider(handler).RespondAsync(NewRequest())).Status);
    }

    // ---------------------------------------------------------------- status mapping

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FamiliarReasoningStatus.Unauthenticated)]
    [InlineData(HttpStatusCode.Forbidden, FamiliarReasoningStatus.Unauthenticated)]
    [InlineData(HttpStatusCode.PaymentRequired, FamiliarReasoningStatus.Unauthenticated)]
    [InlineData(HttpStatusCode.TooManyRequests, FamiliarReasoningStatus.RateLimited)]
    [InlineData(HttpStatusCode.RequestTimeout, FamiliarReasoningStatus.TimedOut)]
    [InlineData(HttpStatusCode.GatewayTimeout, FamiliarReasoningStatus.TimedOut)]
    [InlineData(HttpStatusCode.BadRequest, FamiliarReasoningStatus.Malformed)]
    [InlineData(HttpStatusCode.UnprocessableEntity, FamiliarReasoningStatus.Malformed)]
    [InlineData(HttpStatusCode.InternalServerError, FamiliarReasoningStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FamiliarReasoningStatus.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, FamiliarReasoningStatus.Unavailable)]
    public async Task Each_http_status_maps_to_its_reasoning_status(
        HttpStatusCode statusCode,
        FamiliarReasoningStatus expected)
    {
        var handler = Scripted(statusCode, "{\"error\":\"something\"}");

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(expected, OpenAiCompatibleFamiliarReasoningProvider.ClassifyStatus(statusCode));
    }

    /// <summary>
    /// The redaction proof. An error body carrying a synthetic credential, a machine path and a host
    /// must not reach the outcome — the body is never read at all.
    /// </summary>
    [Fact]
    public async Task An_error_body_never_reaches_the_outcome()
    {
        const string FakeKey = "sk-not-a-real-key-000000000000000000";
        const string FakePath = "/srv/familiar/secrets/runner-bridge.token";
        const string FakeHost = "https://api.example.invalid/v1/chat/completions";

        var handler = Scripted(
            HttpStatusCode.Unauthorized,
            $$$"""{"error":{"message":"401 from {{{FakeHost}}} using {{{FakeKey}}} configured at {{{FakePath}}}"}}""");

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.Unauthenticated, outcome.Status);

        var carried = $"{outcome.Detail} {outcome.Reply}";
        Assert.DoesNotContain(FakeKey, carried, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePath, carried, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeHost, carried, StringComparison.Ordinal);
        Assert.DoesNotContain("401", carried, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- faults and cancellation

    /// <summary>
    /// The interface's central promise. Whatever the transport does, the page still renders — so a
    /// range of faults is scripted and none may escape.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransportFaults))]
    public async Task The_provider_never_throws_for_a_transport_fault(Exception fault)
    {
        var handler = Throwing(fault);

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.NotEqual(FamiliarReasoningStatus.Answered, outcome.Status);
        Assert.NotNull(outcome.Detail);
    }

    public static TheoryData<Exception> TransportFaults() =>
    [
        new HttpRequestException("connection refused to 127.0.0.1:11434"),
        new HttpRequestException("no such host"),
        new NotSupportedException("unsupported"),
        new UriFormatException("bad uri")
    ];

    /// <summary>An exception message carrying a secret is not carried into the outcome either.</summary>
    [Fact]
    public async Task An_exception_message_never_reaches_the_outcome()
    {
        const string Secret = "sk-not-a-real-key-111111111111111111";

        var handler = Throwing(new HttpRequestException($"failed at /home/wizard/app with {Secret}"));

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.Unavailable, outcome.Status);
        Assert.DoesNotContain(Secret, outcome.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/wizard", outcome.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller who went away is not an endpoint that failed. The cancellation propagates rather than
    /// being recorded as a provider fault the provider never had.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var caller = new CancellationTokenSource();
        var handler = Throwing(new OperationCanceledException(), beforeThrow: caller.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewProvider(handler).RespondAsync(NewRequest(), caller.Token));
    }

    /// <summary>Our own bound elapsing is a timeout, and is reported as one.</summary>
    [Fact]
    public async Task A_timeout_that_is_not_the_callers_is_reported_as_timed_out()
    {
        var handler = Throwing(new TaskCanceledException("timed out"));

        var outcome = await NewProvider(handler).RespondAsync(NewRequest());

        Assert.Equal(FamiliarReasoningStatus.TimedOut, outcome.Status);
    }

    // ---------------------------------------------------------------- attribution and credentials

    [Fact]
    public async Task The_answering_model_is_recorded_when_the_endpoint_names_one()
    {
        var handler = Scripted(HttpStatusCode.OK, """
            {"model":"llama-3.3-70b-versatile","choices":[{"message":{"content":"{\"reply\":\"Hi.\",\"action\":null,\"evidence\":null}"},"finish_reason":"stop"}]}
            """);

        var outcome = await NewProvider(handler, o => o.DisplayName = "Groq").RespondAsync(NewRequest());

        Assert.Equal("Groq", outcome.Metadata.Provider);
        Assert.Equal("llama-3.3-70b-versatile", outcome.Metadata.Model);
        Assert.NotNull(outcome.Metadata.LatencyMs);
    }

    /// <summary>
    /// The credential comes from the environment, never from configuration — the options type has
    /// nowhere to put a key, only the name of the variable holding one.
    /// </summary>
    [Fact]
    public void The_api_key_is_read_from_the_environment_only()
    {
        var options = new OpenAiCompatibleReasoningOptions { ApiKeyVariable = "SOME_PROVIDER_KEY" };

        Assert.Equal(
            "a-value",
            options.ReadApiKey(name => name == "SOME_PROVIDER_KEY" ? "  a-value  " : null));

        // Unset, blank, and "no variable configured" are all simply null — a local endpoint needs none.
        Assert.Null(options.ReadApiKey(_ => null));
        Assert.Null(options.ReadApiKey(_ => "   "));
        Assert.Null(new OpenAiCompatibleReasoningOptions().ReadApiKey(_ => "ignored"));

        // No property on the options can hold a key, so none can be bound from appsettings.
        Assert.DoesNotContain(
            typeof(OpenAiCompatibleReasoningOptions).GetProperties(),
            property => property.Name.Contains("ApiKey", StringComparison.Ordinal)
                        && property.Name != nameof(OpenAiCompatibleReasoningOptions.ApiKeyVariable));
    }

    // ---------------------------------------------------------------- helpers

    private static OpenAiCompatibleFamiliarReasoningProvider NewProvider(
        StubHandler handler,
        Action<OpenAiCompatibleReasoningOptions>? configure = null)
    {
        var options = new OpenAiCompatibleReasoningOptions();
        configure?.Invoke(options);

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/v1/") };

        return new OpenAiCompatibleFamiliarReasoningProvider(
            client, Options.Create(options), TimeProvider.System);
    }

    private static StubHandler Answering(string reply) =>
        Scripted(HttpStatusCode.OK, Completion(
            $$"""{"reply":{{JsonSerializer.Serialize(reply)}},"action":null,"evidence":null}"""));

    /// <summary>An OpenAI-shaped completion whose message content is the structured reply.</summary>
    private static string Completion(string content) =>
        $$"""
          {"model":"stub-model","choices":[{"message":{"content":{{JsonSerializer.Serialize(content)}},"refusal":null},"finish_reason":"stop"}]}
          """;

    private static StubHandler Scripted(HttpStatusCode statusCode, string body) => new(statusCode, body);

    private static StubHandler Throwing(Exception fault, Action? beforeThrow = null) =>
        new(fault, beforeThrow);

    private static FamiliarReasoningRequest NewRequest() => new(
        Snapshot(),
        [new FamiliarTurn(FamiliarMessageAuthor.Human, "earlier question")],
        "why is this blocked?",
        FamiliarBehaviorContract.Text);

    private static ProjectSnapshot Snapshot() => new(
        Guid.NewGuid(), "A project", "Purpose.", false, ProjectStatus.Active, 3,
        [], [], [], [],
        new SnapshotHealth(0, [], 0, false),
        [],
        new SnapshotWorkforce(0, [], 0, 0, 0),
        [],
        500, true, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Answers in-process. There is no socket, no endpoint and no credential anywhere in this suite —
    /// the handler either returns the scripted response or throws the scripted fault.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _body;
        private readonly Exception? _fault;
        private readonly Action? _beforeThrow;

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public StubHandler(Exception fault, Action? beforeThrow = null)
        {
            _fault = fault;
            _beforeThrow = beforeThrow;
        }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (_fault is not null)
            {
                _beforeThrow?.Invoke();
                throw _fault;
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body!, Encoding.UTF8, "application/json")
            };
        }
    }
}
