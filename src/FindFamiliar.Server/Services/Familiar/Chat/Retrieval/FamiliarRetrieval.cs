using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Chat.Retrieval;

/// <summary>
/// One recorded piece of context, found for a question and carried into the prompt.
/// </summary>
/// <param name="EntryId">
/// Written into the prompt so an answer can cite it. Slice 2 validates citations against exactly
/// these ids, which is only possible because the id travelled with the text.
/// </param>
/// <param name="Excerpt">
/// A window of the entry's content, not the whole of it. A single Decision entry can be longer than
/// the entire retrieval budget, and one entry crowding out five others is a worse answer than six
/// partial ones.
/// </param>
public sealed record RetrievedEntry(
    Guid EntryId,
    Guid ProjectId,
    string ProjectName,
    ContextEntryKind Kind,
    string Title,
    string Excerpt,
    DateTime CreatedUtc,
    bool IsExcerpted);

/// <summary>
/// What a search of the recorded context found, what it could not see, and what it declined to carry.
///
/// <see cref="Entries"/> being empty is a first-class outcome rather than a missing section. A model
/// shown no context and no statement that none was found will answer from whatever it recalls about
/// software projects in general, in the same confident register it uses for facts it actually has —
/// which is the failure mode this whole application exists to prevent. Finding nothing is information,
/// and it is written into the prompt as such.
/// </summary>
/// <param name="BelowThreshold">
/// Entries that shared a word with the question and did not clear the relevance floor.
///
/// Counted rather than carried, and the distinction matters: this is the number that used to be
/// returned as an answer. A search with no floor always has a best candidate, and its best candidate
/// on a question about repository snapshots was an unrelated defect about absolute paths — presented
/// with the same confidence as a direct hit. Keeping the count means the prompt can say <i>some
/// entries mentioned these words and none of them was responsive</i>, which is true, instead of
/// either narrating one of them or implying the store is empty.
/// </param>
public sealed record FamiliarRetrievalResult(
    IReadOnlyList<RetrievedEntry> Entries,
    IReadOnlyList<string> Terms,
    int CandidatesConsidered,
    int SensitiveWithheld,
    int BelowThreshold = 0)
{
    /// <summary>Entries carried into one prompt.</summary>
    public const int MaxEntries = 6;

    /// <summary>A bound on one entry's excerpt, in characters.</summary>
    public const int MaxExcerptCharacters = 900;

    /// <summary>A bound on the whole retrieved block, in characters.</summary>
    public const int MaxCharacters = 6_000;

    /// <summary>
    /// Rows loaded before scoring. The corpus is small and this is a backstop, not a strategy: when it
    /// starts binding, the answer is an index, not a bigger number.
    /// </summary>
    public const int MaxCandidates = 500;

    public static FamiliarRetrievalResult Empty { get; } = new([], [], 0, 0);

    public bool FoundNothing => Entries.Count == 0;

    /// <summary>
    /// True when the search ran, something shared a word with the question, and none of it was close
    /// enough to carry. The explicit no-match signal: distinct from an empty store, and the case the
    /// prompt must state out loud rather than fill.
    /// </summary>
    public bool NoMatchAboveFloor => Entries.Count == 0 && BelowThreshold > 0;
}
