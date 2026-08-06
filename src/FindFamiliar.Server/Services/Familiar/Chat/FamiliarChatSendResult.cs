namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>What a send did. Every value is an outcome a person can be told about plainly.</summary>
public enum FamiliarChatSendStatus
{
    /// <summary>The turn is durable and queued for generation. Nothing else is promised yet.</summary>
    Accepted,

    /// <summary>Refused before anything was written. Carries a sentence for the composer.</summary>
    Invalid,

    /// <summary>No such conversation.</summary>
    ChatNotFound,

    /// <summary>
    /// A turn was already in flight, so this sender attaches to it rather than queueing behind it.
    /// The message that was typed is not written and not lost — the page keeps it in the composer.
    /// </summary>
    Attached,

    /// <summary>Retryable, and nothing was written. No competing actor is claimed.</summary>
    DatabaseBusy
}

/// <summary>
/// The outcome of a send, and where to look next.
///
/// <see cref="ChatId"/> is set on every non-refusal, including the send that created the
/// conversation, because the caller redirects to it.
/// </summary>
public sealed record FamiliarChatSendResult(
    FamiliarChatSendStatus Status,
    Guid ChatId = default,
    int Sequence = 0,
    string? ValidationMessage = null)
{
    public static FamiliarChatSendResult Accepted(Guid chatId, int sequence) =>
        new(FamiliarChatSendStatus.Accepted, chatId, sequence);

    public static FamiliarChatSendResult Attached(Guid chatId, int sequence) =>
        new(FamiliarChatSendStatus.Attached, chatId, sequence);

    public static FamiliarChatSendResult Invalid(string message) =>
        new(FamiliarChatSendStatus.Invalid, ValidationMessage: message);

    public static readonly FamiliarChatSendResult ChatNotFound = new(FamiliarChatSendStatus.ChatNotFound);

    public static readonly FamiliarChatSendResult DatabaseBusy = new(FamiliarChatSendStatus.DatabaseBusy);
}
