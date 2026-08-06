using FindFamiliar.Server.Domain;
using System.Text.Json.Serialization;

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
/// One context entry a reply may cite, resolved for display.
///
/// Resolved when the transcript is read, not when the answer was written, and through a query that
/// re-applies the sensitivity filter. An entry flagged sensitive after it was cited stops being
/// displayable without any turn having to be rewritten — the row records which ids were offered, and
/// this decides which of those a reader may still be shown.
/// </summary>
public sealed record FamiliarChatCitationView(
    Guid EntryId,
    Guid ProjectId,
    ContextEntryKind Kind,
    string Title);

/// <summary>One proposed item of a drafted plan, as a client renders it.</summary>
public sealed record FamiliarPlanItemView(
    Guid ItemId,
    int Position,
    string Title,
    string RequestedOutcome,
    AgentSessionRole? Role,
    IReadOnlyList<FamiliarChatCitationView> Evidence,
    bool IsIncluded);

/// <summary>
/// A plan drafted in this conversation, rendered inline in the transcript.
///
/// Carries no approve or decline affordance in slice 3 — the plan is durable and readable, and
/// nothing can act on it yet. What it does carry is the disclosure that makes an approval honest when
/// slice 4 adds one: how many tasks would be created, and which single session would start.
/// </summary>
public sealed record FamiliarPlanView(
    Guid PlanId,
    Guid TurnId,
    Guid ProjectId,
    string ProjectName,
    FamiliarPlanStatus Status,
    string Summary,
    IReadOnlyList<FamiliarPlanItemView> Items,
    DateTime CreatedUtc)
{
    public bool IsPending => Status == FamiliarPlanStatus.Pending;

    public IReadOnlyList<FamiliarPlanItemView> Included =>
        Items.Where(item => item.IsIncluded).ToList();

    /// <summary>
    /// The first included item that names a session. One session starts on approval, not one per item
    /// (ADR-0014 §4) — a plan written before any of it ran is a guess, and the first result is the
    /// best evidence about whether the second step is still right.
    /// </summary>
    public FamiliarPlanItemView? FirstSessionItem =>
        Included.FirstOrDefault(item => item.Role is not null);
}

/// <summary>
/// One exchange, as a client renders it.
///
/// <see cref="Output"/> is whatever has accumulated so far — partial while
/// <see cref="FamiliarChatTurnState.Generating"/>, final afterwards. A client that reads a partial
/// turn and comes back for the rest asks by <see cref="Sequence"/>, never by offset into the text.
/// </summary>
/// <param name="Citations">
/// The entries this turn was answered from, in the order they were offered. An id in the reply that
/// is not here was never in the pack, and the renderers mark it rather than showing it as a source.
/// </param>
/// <param name="Plan">
/// The plan this turn drafted, when it drafted one. Travels with the turn rather than beside the
/// conversation so it renders in place, at the point in the transcript where it was proposed.
/// </param>
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
    string? ProviderModel = null,
    IReadOnlyList<FamiliarChatCitationView>? Citations = null,
    FamiliarPlanView? Plan = null)
{
    public bool IsInFlight => State is FamiliarChatTurnState.Pending or FamiliarChatTurnState.Generating;

    [JsonIgnore]
    public IReadOnlyList<FamiliarChatCitationView> Cited => Citations ?? [];

    /// <summary>
    /// The reply split into text and citations, for the Razor page to walk.
    ///
    /// Off the wire deliberately. The script does its own segmentation from <see cref="Output"/> and
    /// <see cref="Citations"/>, because sending pre-split text would mean every delta re-sent the
    /// whole reply as an array — and the two renderers agreeing is a property worth testing rather
    /// than one worth avoiding.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<FamiliarReplySegment> Segments =>
        FamiliarChatCitations.Segment(Output, Cited.Select(citation => citation.EntryId).ToHashSet());
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
