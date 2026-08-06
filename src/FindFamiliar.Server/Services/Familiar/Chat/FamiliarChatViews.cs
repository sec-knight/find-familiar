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
    DateTime? CompletedUtc,
    string? ProviderName = null,
    string? ProviderModel = null)
{
    public bool IsInFlight => State is FamiliarChatTurnState.Pending or FamiliarChatTurnState.Generating;
}

/// <summary>
/// The cursor a client should resume from, given what it has just rendered.
///
/// One implementation, used by the page, the stream and the script alike, because the rule is subtle
/// enough that three copies would eventually disagree: the cursor stops *before* a turn that is still
/// arriving. A cursor at an in-flight turn's own sequence would mean the next resume skipped it, and
/// the reply would freeze on screen half-written.
/// </summary>
internal static class FamiliarChatCursor
{
    public static int Resume(IReadOnlyList<FamiliarChatTurnView> turns, int latestSequence)
    {
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            if (turns[index].IsInFlight)
            {
                return turns[index].Sequence - 1;
            }
        }

        return latestSequence;
    }
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
    /// The highest sequence this view contains. Zero for a conversation with no turns, which is the
    /// same cursor a client starts with.
    /// </summary>
    public int LatestSequence => Turns.Count == 0 ? 0 : Turns[^1].Sequence;

    /// <summary>Where a client resumes from — before any turn still arriving.</summary>
    public int ResumeCursor => FamiliarChatCursor.Resume(Turns, LatestSequence);
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
    IReadOnlyList<FamiliarChatTurnView> Turns)
{
    /// <summary>
    /// Where the client should resume from after applying this page. Sent rather than computed on the
    /// client so the rule about in-flight turns lives in exactly one place.
    /// </summary>
    public int ResumeCursor => Turns.Count == 0
        ? Math.Max(AfterSequence, HasTurnInFlight ? Math.Max(LatestSequence - 1, 0) : LatestSequence)
        : FamiliarChatCursor.Resume(Turns, LatestSequence);
}
