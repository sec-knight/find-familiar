using System.Text;

namespace FindFamiliar.Server.Services.Familiar.Chat.Retrieval;

/// <summary>
/// Turns a person's message into the words worth searching for.
///
/// Deliberately unclever. No stemming, no synonyms, no embeddings — those are the right tools for a
/// corpus this one is nowhere near, and every one of them makes it harder to answer the question that
/// matters when retrieval goes wrong: <i>why did it find that?</i> A term list a person can read is
/// worth more here than a ranking a person cannot.
/// </summary>
public static class FamiliarQueryTerms
{
    /// <summary>
    /// Below this length a word carries no signal in a corpus of technical prose, and matching it
    /// makes every entry look relevant. "id" and "db" are the losses; both appear next to longer
    /// words that survive.
    /// </summary>
    public const int MinimumTermLength = 3;

    /// <summary>Terms taken from one message, most-selective work being done by the stop list.</summary>
    public const int MaxTerms = 24;

    /// <summary>
    /// Words that appear in nearly every question and therefore separate nothing.
    ///
    /// Short and English-only on purpose. A long stop list starts discarding words that carry real
    /// meaning in this domain — "state", "work" and "run" are stop words in general prose and are
    /// three of the most load-bearing nouns in this system.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "our", "with", "that", "this",
        "these", "those", "what", "when", "where", "which", "who", "why", "how", "was", "were",
        "has", "have", "had", "can", "could", "would", "should", "will", "shall", "did", "does",
        "from", "into", "about", "any", "all", "some", "there", "their", "them", "then", "than",
        "its", "it's", "his", "her", "they", "get", "got", "please", "tell", "give", "show", "know",
        "just", "like", "want", "need", "make", "made", "let", "now", "one", "two", "also", "more",
        "most", "much", "many", "each", "other", "over", "under", "out", "off", "yes", "yeah"
    };

    /// <summary>
    /// The distinct, lowercased, non-trivial words of a message, in the order they first appear.
    ///
    /// Order is preserved rather than sorted because it makes the term list read like the question it
    /// came from, and because a deterministic order keeps the same message producing byte-identical
    /// prompts.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();

        foreach (var character in message)
        {
            // Digits and hyphens are kept inside a word: "ADR-0013", "grok-4", "sprint-12" are the
            // most precise queries a person can type here, and splitting them destroys exactly the
            // signal worth having.
            if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            Take(word, terms, seen);
        }

        Take(word, terms, seen);

        return terms.Count <= MaxTerms ? terms : terms[..MaxTerms];
    }

    private static void Take(StringBuilder word, List<string> terms, HashSet<string> seen)
    {
        if (word.Length == 0)
        {
            return;
        }

        var candidate = word.ToString().Trim('-', '_');
        word.Clear();

        if (candidate.Length < MinimumTermLength || StopWords.Contains(candidate) || !seen.Add(candidate))
        {
            return;
        }

        terms.Add(candidate);
    }
}
