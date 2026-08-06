using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FindFamiliar.Server.Domain;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// A reasoning provider for any endpoint speaking the OpenAI chat-completions shape.
///
/// This is the portable one, and portability is the point: the same code serves a model running on
/// the operator's own machine and a hosted endpoint costing fractions of a penny, chosen by a base
/// address in configuration. Somebody cloning this repository can use whatever they already have.
///
/// It needs no SDK and no NuGet package — just <c>HttpClient</c> — so it lives beside the abstraction
/// rather than in its own project, and the composition root binds it without any structural
/// gymnastics.
///
/// The safety properties are structural and hold for every endpoint this talks to. <b>No tools are
/// declared</b>, and the request type below has no member for them, so there is no execution surface
/// regardless of what a reply says. The reply is constrained to <see cref="FamiliarReplySchema"/>
/// where the endpoint supports it, then parsed and validated again rather than trusted.
/// <c>RespondAsync</c> never throws except for the caller's own cancellation, so an endpoint that is
/// switched off degrades to an honest sentence instead of a broken page. And no response body,
/// exception message or header reaches a log, a column or a person — only this application's own
/// fixed wording does.
/// </summary>
public sealed class OpenAiCompatibleFamiliarReasoningProvider(
    HttpClient httpClient,
    IOptions<OpenAiCompatibleReasoningOptions> options,
    TimeProvider timeProvider) : IFamiliarReasoningProvider
{
    public string Provider => options.Value.DisplayName;

    public async Task<FamiliarReasoningOutcome> RespondAsync(
        FamiliarReasoningRequest request,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var metadata = new FamiliarProviderMetadata(Provider, settings.Model, null);
        var startedAt = timeProvider.GetTimestamp();

        try
        {
            var payload = new ChatRequest(
                settings.Model,
                [
                    // The behaviour contract as the system turn. One copy, owned by the server, sent
                    // identically whichever endpoint answers.
                    new ChatMessage("system", FamiliarBehaviorContract.Text),
                    new ChatMessage("user", ComposeUserMessage(request))
                ],
                settings.MaxOutputTokens,
                Stream: false,
                ResponseFormat: settings.UseStructuredOutput ? ResponseFormat.ForReply() : null);

            using var response = await httpClient.PostAsJsonAsync("chat/completions", payload, cancellationToken);

            var elapsed = (int)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            var answered = metadata with { LatencyMs = elapsed };

            if (!response.IsSuccessStatusCode)
            {
                // The status is classified; the body is never read. An error body routinely echoes the
                // request, and can name a host, a path, an account or part of a key.
                var status = ClassifyStatus(response.StatusCode);

                return FamiliarReasoningOutcome.Failed(status, answered, DetailFor(status));
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken);
            var choice = completion?.Choices?.FirstOrDefault();

            // The refusal signal, checked before the content is read. Endpoints that implement it
            // populate either a dedicated refusal field or a finish reason; both are treated as the
            // real outcome they are rather than as a fault.
            if (choice is not null && IsRefusal(choice))
            {
                return FamiliarReasoningOutcome.Failed(
                    FamiliarReasoningStatus.Declined,
                    answered,
                    DetailFor(FamiliarReasoningStatus.Declined));
            }

            if (FamiliarStructuredReply.Parse(choice?.Message?.Content) is not { } reply)
            {
                return FamiliarReasoningOutcome.Failed(
                    FamiliarReasoningStatus.Malformed,
                    answered,
                    DetailFor(FamiliarReasoningStatus.Malformed));
            }

            return FamiliarReasoningOutcome.Answered(
                reply.Reply!.Trim(),
                answered with
                {
                    // The model that actually answered, when the endpoint names one — a proxy may
                    // resolve an alias, and the transcript should record what really replied.
                    Model = string.IsNullOrWhiteSpace(completion!.Model) ? settings.Model : completion.Model
                },
                FamiliarStructuredReply.Drafts(reply),
                FamiliarStructuredReply.EvidenceIds(reply));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away. Not a provider failure, and recording it as one would blame the
            // endpoint for something it did not do.
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(FamiliarReasoningStatus.TimedOut, metadata, startedAt);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or NotSupportedException or UriFormatException)
        {
            // Not running, not reachable, or answering something unreadable. One fact from the page's
            // side, and the exception's text is carried nowhere.
            return Failed(FamiliarReasoningStatus.Unavailable, metadata, startedAt);
        }
    }

    private FamiliarReasoningOutcome Failed(
        FamiliarReasoningStatus status,
        FamiliarProviderMetadata metadata,
        long startedAt) =>
        FamiliarReasoningOutcome.Failed(
            status,
            metadata with { LatencyMs = (int)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds },
            DetailFor(status));

    /// <summary>
    /// HTTP status to reasoning status, most specific first.
    ///
    /// Authentication before the generic client-error bucket, and rate limiting before both, because
    /// the page says something different and more useful for each. Anything unrecognised is
    /// <see cref="FamiliarReasoningStatus.Unavailable"/> — from a person's side an unclassified
    /// failure and an unreachable endpoint are the same fact.
    /// </summary>
    public static FamiliarReasoningStatus ClassifyStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => FamiliarReasoningStatus.Unauthenticated,
        HttpStatusCode.Forbidden => FamiliarReasoningStatus.Unauthenticated,
        HttpStatusCode.PaymentRequired => FamiliarReasoningStatus.Unauthenticated,
        HttpStatusCode.TooManyRequests => FamiliarReasoningStatus.RateLimited,
        HttpStatusCode.RequestTimeout => FamiliarReasoningStatus.TimedOut,
        HttpStatusCode.GatewayTimeout => FamiliarReasoningStatus.TimedOut,

        // A request this application built and the endpoint rejected — most often an unsupported
        // response_format. Unusable either way, and the page says so without speculating about which
        // side was wrong.
        HttpStatusCode.BadRequest => FamiliarReasoningStatus.Malformed,
        HttpStatusCode.UnprocessableEntity => FamiliarReasoningStatus.Malformed,

        _ => FamiliarReasoningStatus.Unavailable
    };

    /// <summary>Safe detail for a status. Authored here, never from a response or an exception.</summary>
    public static string DetailFor(FamiliarReasoningStatus status) => status switch
    {
        FamiliarReasoningStatus.Unauthenticated =>
            "The reasoning endpoint rejected this application's credentials.",

        FamiliarReasoningStatus.RateLimited =>
            "The reasoning endpoint is rate limiting this application.",

        FamiliarReasoningStatus.TimedOut =>
            "The reasoning endpoint did not answer within the configured timeout.",

        FamiliarReasoningStatus.Malformed =>
            "The reasoning endpoint returned a response this application could not use.",

        FamiliarReasoningStatus.Declined =>
            "The reasoning endpoint declined to answer.",

        _ => "The reasoning endpoint could not be reached."
    };

    /// <summary>
    /// Whether this choice is a decline rather than an answer.
    ///
    /// Two spellings are accepted because the shape is a de-facto standard rather than a specified
    /// one: a dedicated <c>refusal</c> field, and a <c>finish_reason</c> of <c>content_filter</c>.
    /// </summary>
    private static bool IsRefusal(ChatChoice choice) =>
        !string.IsNullOrWhiteSpace(choice.Message?.Refusal)
        || string.Equals(choice.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The user turn: the snapshot, the visible history, and the person's message.
    ///
    /// Serialized with the canonical serializer — the same one that measured the envelope — so what
    /// was measured and what is sent cannot disagree.
    /// </summary>
    private static string ComposeUserMessage(FamiliarReasoningRequest request)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<project_snapshot>");
        builder.AppendLine(JsonSerializer.Serialize(request.Snapshot, ProjectSnapshotSerialization.Options));
        builder.AppendLine("</project_snapshot>");
        builder.AppendLine();

        if (request.History.Count > 0)
        {
            builder.AppendLine("<conversation>");

            foreach (var turn in request.History)
            {
                builder.AppendLine($"{Speaker(turn.Author)}: {turn.Content}");
            }

            builder.AppendLine("</conversation>");
            builder.AppendLine();
        }

        builder.AppendLine("<question>");
        builder.AppendLine(request.UserMessage);
        builder.AppendLine("</question>");

        return builder.ToString();
    }

    private static string Speaker(FamiliarMessageAuthor author) =>
        author == FamiliarMessageAuthor.Human ? "Person" : "Familiar";

    // ---------------------------------------------------------------- wire types

    /// <summary>
    /// The request body. There is deliberately no <c>tools</c> member on this type, so no tool can be
    /// declared even by mistake.
    /// </summary>
    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("response_format")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ResponseFormat? ResponseFormat);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    /// <summary>The schema-constrained output request, in the shape these endpoints expect.</summary>
    private sealed record ResponseFormat(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("json_schema")] JsonSchemaEnvelope JsonSchema)
    {
        public static ResponseFormat ForReply() => new(
            "json_schema",
            new JsonSchemaEnvelope("familiar_reply", true, FamiliarReplySchema.Document));
    }

    private sealed record JsonSchemaEnvelope(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("strict")] bool Strict,
        [property: JsonPropertyName("schema")] JsonElement Schema);

    private sealed record ChatResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("refusal")] string? Refusal);
}
