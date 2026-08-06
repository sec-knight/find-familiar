using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Chat.Providers;

/// <summary>
/// The talk lane over any endpoint speaking the OpenAI chat-completions shape, streamed.
///
/// xAI is the configured one (ADR-0013), and the shape is a de-facto standard rather than a vendor
/// API, so the same code serves OpenRouter, a local runtime, or anything else that speaks it — chosen
/// by a base address in configuration. That portability is also why the second implementation of this
/// interface should be Anthropic-shaped: another OpenAI-compatible endpoint would prove nothing about
/// the abstraction.
///
/// The safety properties are structural and hold for every endpoint this talks to. <b>No tools are
/// declared</b>, and the request type below has no member for them, so there is no execution surface
/// regardless of what a reply says. Nothing stateful is used — no Responses API, no Files, no
/// Collections, no Batch — because the server owns conversation state by design, which is also why
/// Zero Data Retention costs this architecture nothing.
///
/// <c>StreamAsync</c> never throws except for the caller's own cancellation, and never lets a response
/// body, exception message or header reach a log, a column or a person. An error body routinely echoes
/// the request and can name a host, a path, an account or part of a key; the status is classified and
/// the body is not read.
/// </summary>
public sealed class OpenAiCompatibleFamiliarChatProvider(
    HttpClient httpClient,
    IOptions<FamiliarChatOptions> options) : IFamiliarChatProvider
{
    /// <summary>The sentinel an OpenAI-shaped stream ends with, before the connection closes.</summary>
    private const string DoneSentinel = "[DONE]";

    private const string DataPrefix = "data:";

    public string Name => options.Value.DisplayName;

    public string Model => options.Value.Model;

    public async IAsyncEnumerable<FamiliarChatStreamEvent> StreamAsync(
        FamiliarChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        // This application's own bound on the whole stream, linked to the caller's token so the two
        // stay distinguishable: a request the caller abandoned is not a provider that timed out and
        // must not be recorded as one.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        var opened = await OpenAsync(settings, request, cancellationToken, linked.Token);

        if (opened.Failure is { } openFailure)
        {
            yield return new FamiliarChatStreamEvent.Finished(openFailure);
            yield break;
        }

        using var response = opened.Response!;

        if (!response.IsSuccessStatusCode)
        {
            // The status is classified; the body is never read.
            yield return new FamiliarChatStreamEvent.Finished(ClassifyStatus(response.StatusCode));
            yield break;
        }

        await foreach (var streamEvent in ReadStreamAsync(response, cancellationToken, linked.Token))
        {
            yield return streamEvent;
        }
    }

    /// <summary>What opening the connection produced: a response, or a classified reason it failed.</summary>
    private readonly record struct OpenResult(HttpResponseMessage? Response, FamiliarChatProviderStatus? Failure);

    /// <summary>
    /// Sends the request. Separate from the iterator because C# forbids yielding from a catch block,
    /// and the classification genuinely belongs where the exception is caught.
    /// </summary>
    private async Task<OpenResult> OpenAsync(
        FamiliarChatOptions settings,
        FamiliarChatRequest request,
        CancellationToken callerToken,
        CancellationToken linkedToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(BuildRequest(settings, request))
            };

            // ResponseHeadersRead, not the default: without it HttpClient buffers the entire body
            // before returning, and a streamed response would arrive all at once — which is the whole
            // thing this lane exists to avoid.
            return new OpenResult(
                await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linkedToken),
                null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // The caller went away. Not a provider failure, and recording it as one would blame the
            // endpoint for something it did not do.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new OpenResult(null, FamiliarChatProviderStatus.TimedOut);
        }
        catch (Exception exception) when (IsExpectedTransportFault(exception))
        {
            return new OpenResult(null, FamiliarChatProviderStatus.Unavailable);
        }
    }

    /// <summary>One line of the event stream, or a classified reason there is no more.</summary>
    private readonly record struct LineResult(string? Line, bool Ended, FamiliarChatProviderStatus? Failure);

    private static async Task<LineResult> ReadLineAsync(
        StreamReader reader,
        CancellationToken callerToken,
        CancellationToken linkedToken)
    {
        try
        {
            var line = await reader.ReadLineAsync(linkedToken);
            return new LineResult(line, line is null, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our bound elapsed mid-stream. Whatever was already yielded is real and is kept.
            return new LineResult(null, true, FamiliarChatProviderStatus.TimedOut);
        }
        catch (Exception exception) when (IsExpectedTransportFault(exception))
        {
            return new LineResult(null, true, FamiliarChatProviderStatus.Unavailable);
        }
    }

    /// <summary>
    /// Reads server-sent events off the response body and turns them into stream events.
    ///
    /// Exactly one <see cref="FamiliarChatStreamEvent.Finished"/> is emitted on every path, including
    /// a connection that simply stops: a stream that ends without the sentinel has not completed, and
    /// reporting it as though it had would present a truncated reply as a whole one.
    /// </summary>
    private static async IAsyncEnumerable<FamiliarChatStreamEvent> ReadStreamAsync(
        HttpResponseMessage response,
        CancellationToken callerToken,
        [EnumeratorCancellation] CancellationToken linkedToken)
    {
        var opened = await OpenBodyAsync(response, callerToken, linkedToken);

        if (opened.Failure is { } bodyFailure)
        {
            yield return new FamiliarChatStreamEvent.Finished(bodyFailure);
            yield break;
        }

        using var reader = opened.Reader!;

        var completedNormally = false;
        var declined = false;
        FamiliarChatProviderStatus? transportFailure = null;
        string? resolvedModel = null;
        int? promptTokens = null;
        int? completionTokens = null;

        while (true)
        {
            var read = await ReadLineAsync(reader, callerToken, linkedToken);

            if (read.Failure is { } failure)
            {
                transportFailure = failure;
                break;
            }

            if (read.Ended)
            {
                // The connection ended. Whether that is completion depends on the sentinel, not on
                // the socket closing.
                break;
            }

            var line = read.Line!;

            if (line.Length == 0 || !line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                // Blank separators and comment/keep-alive lines. Ignored rather than treated as
                // malformed: they are part of the transport, not part of the reply.
                continue;
            }

            var data = line[DataPrefix.Length..].Trim();

            if (data.Length == 0)
            {
                continue;
            }

            if (string.Equals(data, DoneSentinel, StringComparison.Ordinal))
            {
                completedNormally = true;
                break;
            }

            if (TryParseChunk(data) is not { } chunk)
            {
                // One unreadable frame is not a reason to discard a reply that is arriving correctly.
                // A stream that is entirely unreadable produces no deltas and no sentinel, and is
                // reported below.
                continue;
            }

            resolvedModel = string.IsNullOrWhiteSpace(chunk.Model) ? resolvedModel : chunk.Model;

            if (chunk.Usage is { } usage)
            {
                promptTokens = usage.InputTokens ?? promptTokens;
                completionTokens = usage.OutputTokens ?? completionTokens;
            }

            if (chunk.Choices?.FirstOrDefault() is not { } choice)
            {
                continue;
            }

            // The refusal signal, checked before the content. Two spellings are accepted because the
            // shape is a de-facto standard rather than a specified one.
            if (!string.IsNullOrWhiteSpace(choice.Delta?.Refusal)
                || string.Equals(choice.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            {
                declined = true;
                continue;
            }

            if (choice.Delta?.Content is { Length: > 0 } text)
            {
                yield return new FamiliarChatStreamEvent.Delta(text);
            }
        }

        // Most specific first: a transport failure is what actually stopped the stream, a refusal is a
        // real outcome, and only a stream that reached the sentinel completed.
        var status = transportFailure
            ?? (declined
                ? FamiliarChatProviderStatus.Declined
                : completedNormally
                    ? FamiliarChatProviderStatus.Completed
                    : FamiliarChatProviderStatus.Unavailable);

        yield return new FamiliarChatStreamEvent.Finished(status, resolvedModel, promptTokens, completionTokens);
    }

    private readonly record struct BodyResult(StreamReader? Reader, FamiliarChatProviderStatus? Failure);

    private static async Task<BodyResult> OpenBodyAsync(
        HttpResponseMessage response,
        CancellationToken callerToken,
        CancellationToken linkedToken)
    {
        try
        {
            // The reader owns the stream and disposes it, so there is one disposal path rather than
            // two that have to agree.
            return new BodyResult(new StreamReader(await response.Content.ReadAsStreamAsync(linkedToken)), null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new BodyResult(null, FamiliarChatProviderStatus.TimedOut);
        }
        catch (Exception exception) when (IsExpectedTransportFault(exception))
        {
            return new BodyResult(null, FamiliarChatProviderStatus.Unavailable);
        }
    }

    private static StreamChunk? TryParseChunk(string data)
    {
        try
        {
            return JsonSerializer.Deserialize<StreamChunk>(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatRequest BuildRequest(FamiliarChatOptions settings, FamiliarChatRequest request)
    {
        // Stable to volatile, so the provider's prefix cache covers the head that does not change.
        var messages = new List<ChatMessage> { new("system", request.SystemPrompt) };

        foreach (var turn in request.History)
        {
            messages.Add(new ChatMessage("user", turn.UserText));

            if (turn.Output.Length > 0)
            {
                messages.Add(new ChatMessage("assistant", turn.Output));
            }
        }

        messages.Add(new ChatMessage("user", request.UserMessage));

        return new ChatRequest(
            settings.Model,
            messages,
            settings.MaxOutputTokens,
            Stream: true,
            StreamOptions: new StreamOptions(IncludeUsage: true));
    }

    /// <summary>
    /// HTTP status to provider status, most specific first.
    ///
    /// A retired or renamed model arrives here as a 400 or 404 and becomes
    /// <see cref="FamiliarChatProviderStatus.Malformed"/>, which is what turns it into a visible error
    /// in the UI rather than a dead stream.
    /// </summary>
    public static FamiliarChatProviderStatus ClassifyStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => FamiliarChatProviderStatus.Unauthenticated,
        HttpStatusCode.Forbidden => FamiliarChatProviderStatus.Unauthenticated,
        HttpStatusCode.PaymentRequired => FamiliarChatProviderStatus.Unauthenticated,
        HttpStatusCode.TooManyRequests => FamiliarChatProviderStatus.RateLimited,
        HttpStatusCode.RequestTimeout => FamiliarChatProviderStatus.TimedOut,
        HttpStatusCode.GatewayTimeout => FamiliarChatProviderStatus.TimedOut,
        HttpStatusCode.BadRequest => FamiliarChatProviderStatus.Malformed,
        HttpStatusCode.NotFound => FamiliarChatProviderStatus.Malformed,
        HttpStatusCode.UnprocessableEntity => FamiliarChatProviderStatus.Malformed,

        _ => FamiliarChatProviderStatus.Unavailable
    };

    /// <summary>
    /// Transport faults this application expects and classifies. Anything else is a real defect and is
    /// left to propagate rather than dressed up as an unreachable endpoint.
    /// </summary>
    private static bool IsExpectedTransportFault(Exception exception) =>
        exception is HttpRequestException or IOException or JsonException
            or NotSupportedException or UriFormatException or ObjectDisposedException;

    // ---------------------------------------------------------------- wire types

    /// <summary>
    /// The request body. There is deliberately no <c>tools</c> member on this type, so no tool can be
    /// declared even by mistake — and no <c>store</c>, <c>previous_response_id</c> or any other member
    /// that would ask the provider to hold conversation state.
    /// </summary>
    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("stream_options")] StreamOptions? StreamOptions);

    private sealed record StreamOptions(
        [property: JsonPropertyName("include_usage")] bool IncludeUsage);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record StreamChunk(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<StreamChoice>? Choices,
        [property: JsonPropertyName("usage")] StreamUsage? Usage);

    private sealed record StreamChoice(
        [property: JsonPropertyName("delta")] StreamDelta? Delta,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record StreamDelta(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("refusal")] string? Refusal);

    private sealed record StreamUsage(
        [property: JsonPropertyName("prompt_tokens")] int? InputTokens,
        [property: JsonPropertyName("completion_tokens")] int? OutputTokens);
}
