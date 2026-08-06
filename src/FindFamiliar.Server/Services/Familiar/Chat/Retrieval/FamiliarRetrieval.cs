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
/// What a search of the recorded context found, and what it could not see.
///
/// <see cref="Entries"/> being empty is a first-class outcome rather than a missing section. A model
/// shown no context and no statement that none was found will answer from whatever it recalls about
/// software projects in general, in the same confident register it uses for facts it actually has —
/// which is the failure mode this whole application exists to prevent. Finding nothing is information,
/// and it is written into the prompt as such.
/// </summary>
public sealed record FamiliarRetrievalResult(
    IReadOnlyList<RetrievedEntry> Entries,
    IReadOnlyList<string> Terms,
    int CandidatesConsidered,
    int SensitiveWithheld)
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
}
