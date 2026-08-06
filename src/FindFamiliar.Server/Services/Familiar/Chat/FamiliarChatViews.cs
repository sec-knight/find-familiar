using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// One conversation in the server-side list. The list is server-side so every device sees the same
/// set; nothing about which conversations exist is remembered in a browser.
/// </summary>
/// <param name="TurnCount">Exchanges so far, so an empty-looking entry is distinguishable from a busy one.</param>
/// <param name="HasTurnInFlight">True when a reply is generating right now, on any device.</param>
public sealed record FamiliarChatSummary(
    Guid ChatId,
    string Title,
    Guid? FocusProjectId,
    string? FocusProjectName,
    int TurnCount,
    bool HasTurnInFlight,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// One exchange, as a client renders it.
///
/// <see cref="Output"/> is whatever has accumulated so far — partial while
/// <see cref="FamiliarChatTurnState.Generating"/>, final afterwards. A client that reads a partial
/// turn and comes back for the rest asks by <see cref="Sequence"/>, never by offset into the text.
/// </summary>
public sealed record FamiliarChatTurnView(
    Guid TurnId,
    int Sequence,
    FamiliarChatTurnState State,
    string UserText,
    string Output,
    string? FailureCode,
    DateTime CreatedUtc,
    DateTime? CompletedUtc)
{
    public bool IsInFlight => State is FamiliarChatTurnState.Pending or FamiliarChatTurnState.Generating;
}

/// <summary>One conversation and its turns, oldest first, in Sequence order.</summary>
public sealed record FamiliarChatView(
    Guid ChatId,
    string Title,
    Guid? FocusProjectId,
    string? FocusProjectName,
    IReadOnlyList<FamiliarChatTurnView> Turns)
{
    /// <summary>The turn a client should watch, or null when the conversation is at rest.</summary>
    public FamiliarChatTurnView? InFlightTurn => Turns.LastOrDefault(turn => turn.IsInFlight);

    /// <summary>
    /// The highest sequence this view contains, and therefore the cursor a client resumes from. Zero
    /// for a conversation with no turns, which is the same cursor a client starts with.
    /// </summary>
    public int LatestSequence => Turns.Count == 0 ? 0 : Turns[^1].Sequence;
}

/// <summary>
/// The answer to "give me everything after sequence N".
///
/// <see cref="LatestSequence"/> is echoed rather than inferred by the client from the returned turns:
/// a reply of zero turns still has to move a cursor honestly, and a client that computed its own
/// cursor from an empty list would sit on a stale one forever.
/// </summary>
public sealed record FamiliarChatTurnPage(
    Guid ChatId,
    int AfterSequence,
    int LatestSequence,
    bool HasTurnInFlight,
    IReadOnlyList<FamiliarChatTurnView> Turns);
