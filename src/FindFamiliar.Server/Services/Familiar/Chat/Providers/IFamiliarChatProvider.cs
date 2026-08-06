namespace FindFamiliar.Server.Services.Familiar.Chat.Providers;

/// <summary>
/// One visible turn as the provider sees it: what was asked, and what was answered. Content only —
/// no ids, no timestamps, no state, and no turn that failed, because re-feeding this application's
/// own failure sentences would teach a model to imitate error text.
/// </summary>
public sealed record FamiliarChatHistoryTurn(string UserText, string Output);

/// <summary>
/// Everything sent, and nothing else.
///
/// Ordered stable to volatile by construction, so a provider's prefix cache covers the head that does
/// not change between turns:
///
/// 1. <see cref="SystemPrompt"/> — a compile-time constant, identical on every request ever made;
/// 2. <see cref="StandingBrief"/> — changes only when project state does, so it is stable across a
///    sitting even though it is not stable across a week;
/// 3. <see cref="History"/> — append-only, so each turn extends the previous prefix rather than
///    rewriting it;
/// 4. <see cref="RecordedContext"/> — searched fresh for this message, so different every time;
/// 5. <see cref="UserMessage"/> — different every time, and therefore last.
///
/// The brief is a separate segment rather than appended to the system prompt on purpose: it changes,
/// and folding it into the constant would mean every project edit invalidated the cache entry for the
/// part that never changes at all.
/// </summary>
/// <param name="RecordedContext">
/// What searching the recorded context turned up for this message, or null when nothing selective was
/// asked. Placed after the history rather than beside the brief because it is the most volatile
/// segment there is: it belongs at the tail, where changing it costs nothing that was cached.
/// </param>
public sealed record FamiliarChatRequest(
    string SystemPrompt,
    IReadOnlyList<FamiliarChatHistoryTurn> History,
    string UserMessage,
    string? StandingBrief = null,
    string? RecordedContext = null);

/// <summary>
/// How a stream ended. Every member maps to exactly one code and one sentence in
/// <see cref="FamiliarChatFailureWording"/>; adding a member without adding wording fails a test
/// rather than falling through to a generic string.
/// </summary>
public enum FamiliarChatProviderStatus
{
    /// <summary>The stream finished normally. Whatever was emitted is the whole reply.</summary>
    Completed,

    /// <summary>Unreachable, or answering something unreadable.</summary>
    Unavailable,

    /// <summary>Credentials missing or rejected. A server configuration problem, not a user error.</summary>
    Unauthenticated,

    /// <summary>This application's own bound elapsed. Distinct from the caller cancelling.</summary>
    TimedOut,

    RateLimited,

    /// <summary>
    /// A request this application built and the endpoint rejected, or a response it could not read.
    /// A retired model id lands here, which is what makes it a visible error rather than a dead
    /// stream.
    /// </summary>
    Malformed,

    /// <summary>The provider refused to answer. A real outcome, not a fault.</summary>
    Declined
}

/// <summary>
/// One thing that happened on the wire.
///
/// A closed hierarchy rather than an exception, because a stream that fails after emitting half a
/// reply is an ordinary outcome the caller must handle: the half already written is real, and the
/// person is entitled to keep it.
/// </summary>
public abstract record FamiliarChatStreamEvent
{
    private FamiliarChatStreamEvent()
    {
    }

    /// <summary>A fragment of the visible reply, in order.</summary>
    public sealed record Delta(string Text) : FamiliarChatStreamEvent;

    /// <summary>
    /// The terminal event. Exactly one is emitted, always, including on failure — a stream that ends
    /// without one has broken this contract, and the generator classifies that as malformed rather
    /// than leaving a turn unfinished.
    /// </summary>
    /// <param name="Model">
    /// What actually answered, when the endpoint names it. A proxy may resolve an alias, and the
    /// transcript should record what really replied rather than what was asked for.
    /// </param>
    public sealed record Finished(
        FamiliarChatProviderStatus Status,
        string? Model = null,
        int? InputTokens = null,
        int? OutputTokens = null,
        int? CachedInputTokens = null) : FamiliarChatStreamEvent;
}

/// <summary>
/// The talk lane's wire seam (ADR-0013), independent of the Runner and of
/// <c>IFamiliarReasoningProvider</c>.
///
/// Stateless with respect to the provider: the server owns all conversation state and sends the full
/// assembled context on every call. No provider feature that stores conversation state server-side is
/// used, which is both an architectural choice and what makes Zero Data Retention cost this design
/// nothing.
///
/// Nothing in this namespace names a vendor SDK, and no implementation receives a <c>DbContext</c>, an
/// <c>HttpContext</c>, or a dispatch service. <b>No tools are declared</b> — the request type has no
/// member for them — so there is no execution surface regardless of what a reply says.
///
/// <b>This never throws</b> except for the caller's own cancellation. Every failure is a
/// <see cref="FamiliarChatStreamEvent.Finished"/> with a typed status, exactly as
/// <c>IFamiliarReasoningProvider</c> returns one rather than breaking a page.
/// </summary>
public interface IFamiliarChatProvider
{
    /// <summary>The name recorded on a turn and shown beside it.</summary>
    string Name { get; }

    /// <summary>The model this provider was configured to ask for.</summary>
    string Model { get; }

    IAsyncEnumerable<FamiliarChatStreamEvent> StreamAsync(
        FamiliarChatRequest request,
        CancellationToken cancellationToken = default);
}
