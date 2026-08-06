namespace FindFamiliar.Server.Services.Familiar.Chat.Providers;

/// <summary>A stable code this codebase writes, and the sentence a person reads.</summary>
public sealed record FamiliarChatFailureNote(string Code, string Sentence);

/// <summary>
/// What a person is told when a reply does not arrive.
///
/// Every sentence is authored here. No provider response body, exception message, header, host, path
/// or fragment of a credential reaches a person or a column — an error body routinely echoes the
/// request, and this is the chokepoint that guarantees none of it escapes.
///
/// The Familiar never speaks in an error's voice. These are notes from this application about a
/// component the Familiar cannot observe, which is why a failed turn renders as Find Familiar rather
/// than as the Familiar.
/// </summary>
public static class FamiliarChatFailureWording
{
    /// <summary>
    /// One note per status. Exhaustive by construction: every member of
    /// <see cref="FamiliarChatProviderStatus"/> is named, and
    /// <c>FamiliarChatFailureWordingTests</c> fails if one is added without wording.
    /// </summary>
    public static FamiliarChatFailureNote For(FamiliarChatProviderStatus status) => status switch
    {
        FamiliarChatProviderStatus.Completed => new(
            "chat-empty-reply",
            "The conversational provider finished without saying anything. Nothing was lost — ask again."),

        FamiliarChatProviderStatus.Unauthenticated => new(
            "chat-unauthenticated",
            "The conversational provider rejected this server's credentials, so there is no reply. "
            + "This is a configuration problem on this machine, not something you did."),

        FamiliarChatProviderStatus.RateLimited => new(
            "chat-rate-limited",
            "The conversational provider is rate limiting this server, so there is no reply. Try again shortly."),

        FamiliarChatProviderStatus.TimedOut => new(
            "chat-timed-out",
            "The conversational provider did not finish in time, so the reply is incomplete or missing. "
            + "Your message was saved."),

        // Deliberately does not name one cause. xAI answers a rejected credential with HTTP 400 and
        // not 401, so this status covers both a bad key and a retired model — and the response body
        // that would distinguish them is exactly what this application refuses to read, because error
        // bodies echo the request. Naming the likelier cause was tried and was wrong the first time it
        // mattered, which is the whole argument for saying only what is known.
        FamiliarChatProviderStatus.Malformed => new(
            "chat-malformed",
            "The conversational provider rejected this server's request, so there is no reply. "
            + "The usual causes are a model id that has been retired or renamed, or a credential that "
            + "is not valid. Both are configuration on this machine, not something you did."),

        FamiliarChatProviderStatus.Declined => new(
            "chat-declined",
            "The conversational provider declined to answer that."),

        _ => new(
            "chat-unavailable",
            "The conversational provider could not be reached, so there is no reply. Your message was saved.")
    };

    /// <summary>
    /// The note for a stream that failed <i>after</i> emitting text.
    ///
    /// Distinct wording, because the situation is genuinely different: a partial reply is on screen
    /// and must not be described as though nothing arrived. The partial text is kept and this is
    /// appended, so the transcript never disagrees with what the person already read.
    /// </summary>
    public static FamiliarChatFailureNote Truncated(FamiliarChatProviderStatus status) => new(
        For(status).Code,
        "\n\n— The reply stopped early and is incomplete.");
}
