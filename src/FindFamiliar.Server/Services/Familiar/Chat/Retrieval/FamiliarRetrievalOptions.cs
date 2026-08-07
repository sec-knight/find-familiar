namespace FindFamiliar.Server.Services.Familiar.Chat.Retrieval;

/// <summary>
/// Where the bar sits between "this answers the question" and "this shares a word with it".
///
/// The bug this exists for: a search that always returns its best candidate has no way to say
/// <i>nothing here is responsive</i>. Asked about repository snapshots it returned an unrelated open
/// defect whose title happened to contain "plans", and the Familiar narrated that near-miss in the
/// same confident register it uses for a direct hit. A confidently-worded wrong answer is worse than
/// an empty one, because an empty one is obviously empty.
///
/// <b>Two bars, not one, because they catch different failures.</b>
/// <see cref="MinimumScore"/> catches the weak match — one incidental mention in a long body.
/// <see cref="MinimumMatchedTerms"/> catches the <i>narrow</i> match, which is the one that actually
/// bit: a single term landing in a title scores highly (a title hit is worth eight content hits) while
/// touching one word of a five-word question. Score alone cannot tell those apart, because the thing
/// that makes a title hit valuable is exactly what lets one accidental title hit clear an absolute
/// floor.
///
/// Configured rather than compiled because the right numbers are a property of the corpus, not of the
/// algorithm, and the corpus is thirty-odd entries today and will not be later. The defaults are
/// deliberately mild — they discard the near-misses this system has actually produced and nothing
/// more. Raising them is how a future operator trades recall for precision without a rebuild.
/// </summary>
public sealed class FamiliarRetrievalOptions
{
    public const string SectionName = "Familiar:Retrieval";

    /// <summary>
    /// The lowest score an entry may have and still be carried.
    ///
    /// Calibrated against the weakest match worth having: one query term appearing once in a body,
    /// which scores 2 and is a mention rather than a subject.
    ///
    /// It binds hardest on the one-word question, where <see cref="MinimumMatchedTerms"/> is clamped
    /// to one and breadth can say nothing. Asked "handoff", a passing mention in an unrelated weekly
    /// note is all breadth requires and all this rejects.
    /// </summary>
    public int MinimumScore { get; set; } = 4;

    /// <summary>
    /// How many of the question's own terms an entry must touch.
    ///
    /// Two, because one is the observed defect and three would silently drop the short precise
    /// questions this system is best at ("what did ADR-0013 decide about the runner?"). It is a count
    /// rather than a fraction on purpose: a fraction makes a long question harder to answer than a
    /// short one, which is backwards — a person who types more has given retrieval more to work with,
    /// not more to satisfy.
    /// </summary>
    public int MinimumMatchedTerms { get; set; } = 2;

    /// <summary>
    /// The bar actually applied, given how many terms the question yielded.
    ///
    /// A one-word question cannot match two terms, and requiring it to would mean the most precise
    /// query a person can type — a single identifier — could never return anything. The requirement
    /// is therefore clamped to what the question makes possible.
    /// </summary>
    public int RequiredMatchedTerms(int questionTermCount) =>
        Math.Max(1, Math.Min(MinimumMatchedTerms, questionTermCount));
}
