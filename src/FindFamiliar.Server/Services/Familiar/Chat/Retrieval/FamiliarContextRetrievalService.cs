using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Chat.Retrieval;

/// <summary>
/// Searches the recorded context for one message. Read-only on every path.
/// </summary>
public interface IFamiliarContextRetrievalService
{
    Task<FamiliarRetrievalResult> RetrieveAsync(
        string message,
        Guid? focusProjectId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Familiar reading its own memory.
///
/// Until this existed the Familiar could see projects and tasks and nothing else — so every result its
/// own sessions had harvested since Sprint 9, and every ADR recording why this system is shaped the
/// way it is, sat in a store it could not read. The loop the whole project exists to close was open at
/// the last step.
///
/// <b>The server searches; the model does not.</b> ADR-0014 chose deterministic retrieval over a
/// model-driven tool call, and the reason is a failure mode rather than a preference: a tool that
/// silently does not fire produces an answer drafted blind by something that does not know it is
/// blind. This cannot fail to fire. It also costs one round trip instead of two, which is the latency
/// budget the talk lane exists to protect.
///
/// The scoring below is keyword overlap — no stemming, no embeddings, no index. That is the right
/// tool at thirty-five entries and the wrong one at thirty thousand; the note in
/// <see cref="FamiliarRetrievalResult.MaxCandidates"/> says what to do when that changes.
///
/// <b>Sensitivity is honoured in the query, not after it</b>, on both the entry and its project, so
/// there is no moment at which flagged rows are in memory beside a prompt being built. The count of
/// what was withheld travels out; nothing about the content does.
///
/// <b>There is a floor, and below it the answer is "nothing".</b> Ranking always has a best candidate;
/// relevance does not. Without a floor this returned its top row whatever it scored, which is how a
/// question about repository snapshots came back with an unrelated defect about absolute paths,
/// narrated as though it were responsive. <see cref="FamiliarRetrievalOptions"/> holds where the bar
/// sits and why it is two numbers rather than one.
/// </summary>
public sealed class FamiliarContextRetrievalService(
    FamiliarDbContext dbContext,
    IOptions<FamiliarRetrievalOptions> options)
    : IFamiliarContextRetrievalService
{
    private readonly FamiliarRetrievalOptions _options = options.Value;

    /// <summary>
    /// Kinds never retrieved, whatever they score.
    ///
    /// <see cref="ContextEntryKind.Prompt"/> and <see cref="ContextEntryKind.RawOutput"/> are the
    /// verbatim input and output of a previous agent run. Feeding those back into a conversation
    /// teaches a model to imitate them — to write in the voice of a session transcript, or to treat an
    /// instruction addressed to a Planner as one addressed to itself. This is the same rule that keeps
    /// failed turns out of conversation history, applied to the other store that holds machine text.
    /// </summary>
    private static readonly ContextEntryKind[] NeverRetrieved =
        [ContextEntryKind.Prompt, ContextEntryKind.RawOutput];

    /// <summary>
    /// A title hit is worth this many content hits.
    ///
    /// Titles are written by a person or by a session summarising itself, and a term appearing there
    /// is a statement that the entry is about that thing — where the same term in the body may be one
    /// mention in nine hundred characters.
    /// </summary>
    private const int TitleWeight = 8;

    /// <summary>
    /// Repeated mentions in a body count, but with sharply diminishing returns, so a long entry cannot
    /// outrank a precise one by sheer length. Counted to this cap and no further.
    /// </summary>
    private const int MaxContentHitsPerTerm = 3;

    /// <summary>
    /// Kinds that record why something is the way it is, rather than what happened once.
    ///
    /// A small thumb on the scale, not a filter: at equal relevance a Decision is more useful to a
    /// question than an Implementation note, because decisions stay true after the work they describe
    /// has been replaced.
    /// </summary>
    private static int KindBonus(ContextEntryKind kind) => kind switch
    {
        ContextEntryKind.Decision => 3,
        ContextEntryKind.Constraint => 3,
        ContextEntryKind.Goal => 2,
        ContextEntryKind.OpenQuestion => 2,
        _ => 0
    };

    public async Task<FamiliarRetrievalResult> RetrieveAsync(
        string message,
        Guid? focusProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var terms = FamiliarQueryTerms.Extract(message);

        if (terms.Count == 0)
        {
            // Nothing selective was said. Returning the newest entries regardless would put unrelated
            // context in front of a model and invite it to connect the two.
            return FamiliarRetrievalResult.Empty;
        }

        // Sensitive entries and sensitive projects are excluded here, in the query, and counted
        // separately so the exclusion can be disclosed without disclosing what was excluded.
        var visible = dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry =>
                entry.State == ContextEntryState.Active
                && !entry.IsSensitive
                && !entry.Project.IsSensitive
                && !NeverRetrieved.Contains(entry.Kind));

        var withheld = await dbContext.ContextEntries
            .AsNoTracking()
            .CountAsync(
                entry => entry.State == ContextEntryState.Active
                         && (entry.IsSensitive || entry.Project.IsSensitive)
                         && !NeverRetrieved.Contains(entry.Kind),
                cancellationToken);

        var candidates = await visible
            .OrderByDescending(entry => entry.CreatedUtc)
            .Take(FamiliarRetrievalResult.MaxCandidates)
            .Select(entry => new Candidate(
                entry.Id,
                entry.ProjectId,
                entry.Project.Name,
                entry.Kind,
                entry.Title,
                entry.Content,
                entry.CreatedUtc))
            .ToListAsync(cancellationToken);

        var touched = candidates
            .Select(candidate => new { Candidate = candidate, Relevance = Score(candidate, terms, focusProjectId) })
            .Where(row => row.Relevance.MatchedTerms > 0)
            .ToList();

        // Both bars, and the clamp: a one-word question cannot match two terms, and requiring it to
        // would make the most precise query a person can type unanswerable.
        var requiredTerms = _options.RequiredMatchedTerms(terms.Count);

        var relevant = touched
            .Where(row =>
                row.Relevance.Score >= _options.MinimumScore
                && row.Relevance.MatchedTerms >= requiredTerms)
            .ToList();

        // Counted before the entry cap, not after. This is "how much of what shares a word with the
        // question was not responsive" — a fact about relevance. Rows dropped by MaxEntries were
        // responsive and lost to a budget, which is a different fact and not this one.
        var belowThreshold = touched.Count - relevant.Count;

        var scored = relevant
            .OrderByDescending(row => row.Relevance.Score)
            // Ties broken by recency, then by id. The id is arbitrary and that is the point: two
            // entries scoring identically must order identically on every run, or the same question
            // produces a different prompt each time and nothing about an answer is reproducible.
            .ThenByDescending(row => row.Candidate.CreatedUtc)
            .ThenBy(row => row.Candidate.EntryId)
            .Take(FamiliarRetrievalResult.MaxEntries)
            .ToList();

        var entries = new List<RetrievedEntry>(scored.Count);
        var budget = FamiliarRetrievalResult.MaxCharacters;

        foreach (var row in scored)
        {
            if (budget <= 0)
            {
                break;
            }

            var excerpt = Excerpt(row.Candidate.Content, terms, Math.Min(budget, FamiliarRetrievalResult.MaxExcerptCharacters));
            budget -= excerpt.Text.Length;

            entries.Add(new RetrievedEntry(
                row.Candidate.EntryId,
                row.Candidate.ProjectId,
                row.Candidate.ProjectName,
                row.Candidate.Kind,
                row.Candidate.Title,
                excerpt.Text,
                row.Candidate.CreatedUtc,
                excerpt.IsExcerpted));
        }

        return new FamiliarRetrievalResult(entries, terms, candidates.Count, withheld, belowThreshold);
    }

    private sealed record Candidate(
        Guid EntryId,
        Guid ProjectId,
        string ProjectName,
        ContextEntryKind Kind,
        string Title,
        string Content,
        DateTime CreatedUtc);

    /// <summary>
    /// How well one entry answers one question, in the two dimensions the floor is applied to.
    /// </summary>
    /// <param name="Score">Weighted strength of the match.</param>
    /// <param name="MatchedTerms">
    /// How many distinct terms of the question the entry touched — its <i>breadth</i>. Carried
    /// separately rather than folded into the score because the floor needs to distinguish a strong
    /// narrow match from a strong broad one, and by the time they are one number it cannot.
    /// </param>
    private readonly record struct Relevance(int Score, int MatchedTerms)
    {
        public static Relevance None { get; } = new(0, 0);
    }

    /// <summary>
    /// How well one entry answers one question.
    ///
    /// Zero means it is not carried at all. An entry matching nothing is not weakly relevant — it is
    /// irrelevant, and padding a prompt with it costs tokens and invites the model to use it.
    /// </summary>
    private static Relevance Score(Candidate candidate, IReadOnlyList<string> terms, Guid? focusProjectId)
    {
        var score = 0;
        var matched = 0;

        foreach (var term in terms)
        {
            var titleHits = Count(candidate.Title, term, 1);
            var contentHits = Count(candidate.Content, term, MaxContentHitsPerTerm);

            if (titleHits + contentHits == 0)
            {
                continue;
            }

            matched++;
            score += titleHits * TitleWeight + contentHits;
        }

        if (matched == 0)
        {
            return Relevance.None;
        }

        // Breadth beats depth: an entry touching three of the question's words is more likely to be
        // about the question than one saying a single word nine times.
        score += matched * matched;
        score += KindBonus(candidate.Kind);

        if (focusProjectId is { } focus && candidate.ProjectId == focus)
        {
            // Focus is a lean, never a filter — the same rule the standing brief follows. A
            // cross-project question must still reach the project that answers it.
            score += 2;
        }

        return new Relevance(score, matched);
    }

    private static int Count(string haystack, string term, int cap)
    {
        var hits = 0;
        var index = 0;

        while (hits < cap
               && (index = haystack.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            hits++;
            index += term.Length;
        }

        return hits;
    }

    /// <summary>
    /// How far back an excerpt may move to open on a word boundary. A word, not a line: past this it
    /// is not a boundary nearby, it is content that has none.
    /// </summary>
    private const int MaxBoundaryNudge = 40;

    private readonly record struct ExcerptResult(string Text, bool IsExcerpted);

    /// <summary>
    /// A window of the content around the first matched term.
    ///
    /// Around the match rather than from the start, because the useful half of a long Decision entry
    /// is routinely in the middle, and a head-truncated entry is the specific way retrieval fails
    /// while appearing to have worked.
    /// </summary>
    private static ExcerptResult Excerpt(string content, IReadOnlyList<string> terms, int budget)
    {
        if (content.Length <= budget)
        {
            return new ExcerptResult(content, false);
        }

        var anchor = terms
            .Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, anchor - budget / 3);
        start = Math.Min(start, content.Length - budget);

        // Nudged back to a word boundary so the excerpt does not open mid-word, which reads as
        // corruption. Bounded, because content with no whitespace at all — a base64 blob, a long
        // hash, a minified payload — would otherwise walk the start all the way to zero and hand back
        // the head of the entry while claiming to be a window around the match. Losing a word boundary
        // is cosmetic; losing the match is the whole point of the excerpt.
        var limit = Math.Max(0, start - MaxBoundaryNudge);
        var boundary = start;

        while (boundary > limit && !char.IsWhiteSpace(content[boundary - 1]))
        {
            boundary--;
        }

        // Only taken when a boundary was actually reached. Hitting the limit means there is none
        // nearby, and moving anyway would shift the window for nothing.
        if (boundary == 0 || boundary > limit)
        {
            start = boundary;
        }

        var text = content.Substring(start, Math.Min(budget, content.Length - start)).Trim();

        return new ExcerptResult(
            (start > 0 ? "…" : string.Empty) + text + (start + budget < content.Length ? "…" : string.Empty),
            true);
    }
}
