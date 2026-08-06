namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// The read and write surface of the system-wide Familiar conversation.
///
/// Nothing here calls a reasoning provider. A send makes a turn durable and returns; generation
/// happens elsewhere, detached from whichever connection asked for it (see
/// <see cref="IFamiliarChatGenerator"/>). That separation is the point rather than an optimisation:
/// it is what lets a reply survive the laptop closing.
/// </summary>
public interface IFamiliarChatService
{
    /// <summary>Every conversation, most recently active first.</summary>
    Task<IReadOnlyList<FamiliarChatSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One conversation with all of its turns, or null when it does not exist.</summary>
    Task<FamiliarChatView?> GetAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything after <paramref name="afterSequence"/>. The single resume path: a client four
    /// seconds behind and a client four hours behind take exactly this call, so it is exercised
    /// constantly rather than rarely.
    /// </summary>
    Task<FamiliarChatTurnPage?> ReadTurnsAfterAsync(
        Guid chatId,
        int afterSequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a turn, creating the conversation when <paramref name="chatId"/> is null.
    ///
    /// Returns as soon as the turn is committed. The reply is not awaited and is not this call's
    /// business.
    /// </summary>
    Task<FamiliarChatSendResult> SendAsync(
        Guid? chatId,
        string message,
        Guid? focusProjectId = null,
        CancellationToken cancellationToken = default);
}
