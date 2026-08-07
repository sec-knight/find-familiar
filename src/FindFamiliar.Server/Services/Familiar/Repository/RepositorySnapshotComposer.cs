using System.Text;

namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// The repository state as one bounded block of text a model reads.
///
/// Pure: git in one end, a string out the other. Everything interesting about this snapshot — what it
/// contains, what gets cut when it will not fit, and how a reader is told — is decided here and can be
/// asserted without a repository, a database or a clock.
///
/// <b>The ceiling is the design.</b> A snapshot of a repository this size is naturally three or four
/// times the budget, so a snapshot that does not trim is a snapshot that silently becomes the largest
/// thing in every prompt. Cutting is therefore normal rather than exceptional, and the interesting
/// question is not <i>whether</i> it was cut but whether the reader can tell — which is what the trim
/// notes are for. A file list that stops at 120 of 366 paths and says so is usable; the same list
/// stopping silently is a lie about the size of the repository.
/// </summary>
public static class RepositorySnapshotComposer
{
    /// <summary>
    /// The ceiling, in characters. Fixed rather than configured: it is a bound on how much of every
    /// prompt one automated entry may consume, and an operator raising it would be trading away the
    /// retrieval budget of every other entry without seeing that trade.
    /// </summary>
    public const int MaxCharacters = 8 * 1024;

    /// <summary>
    /// In the header, so a reader of the raw entry knows the rule without being told: there is one
    /// snapshot, and this is it. Supersession is by delete-on-write rather than by a filter applied at
    /// retrieval time, so no consumer has to know a rule for its results to be correct.
    /// </summary>
    public const string SupersedesMarker = "snapshot-supersedes-prior";

    /// <summary>
    /// Newlines are <c>\n</c> and sorts are ordinal, deliberately. The same repository must compose to
    /// a byte-identical entry on every machine and every run, or a snapshot rewrites itself on a
    /// platform difference and every prompt built on it stops matching the provider's prefix cache.
    /// </summary>
    private const char Newline = '\n';

    public static string Compose(RepositoryState state, DateTime capturedUtc)
    {
        // Render order puts the shape of the repository first and the exhaustive list last, because
        // the exhaustive list is the part most likely to be cut and a reader should not have to scroll
        // past a truncation to reach the summary.
        var sections = new List<Section>
        {
            new("two-level view of tracked paths", "paths", state.TwoLevelPaths),
            new("recent commits", "commits", state.RecentCommits),
            new("tracked files", "paths", state.TrackedPaths)
        };

        var header = Header(state, capturedUtc);
        var total = header.Length + sections.Sum(section => section.Length);

        // Trim order, which is not render order.
        //
        // The exhaustive file list goes first because it is the only section large enough to breach
        // the ceiling on its own — 366 tracked paths here is already twice the budget — and because
        // it is the section whose loss costs least: the two-level view above it already states what
        // is in the repository and roughly how much of it. Cutting the summary to preserve the raw
        // list would leave a reader holding a corner of the repository with nothing to tell them it
        // was a corner, which is the exact failure the trim notes exist to prevent.
        foreach (var index in (int[])[2, 0, 1])
        {
            if (total <= MaxCharacters)
            {
                break;
            }

            var section = sections[index];
            var others = total - section.Length;
            section.TrimTo(MaxCharacters - others);
            total = others + section.Length;
        }

        var builder = new StringBuilder(header);

        foreach (var section in sections)
        {
            section.Render(builder);
        }

        return builder.ToString();
    }

    private static string Header(RepositoryState state, DateTime capturedUtc)
    {
        var builder = new StringBuilder();

        builder.Append("date: ").Append(capturedUtc.ToString("yyyy-MM-dd")).Append(Newline);
        builder.Append("branch: ").Append(state.Branch).Append(Newline);
        builder.Append("head: ").Append(state.HeadSha).Append(Newline);
        builder.Append(SupersedesMarker).Append(Newline);
        builder.Append(Newline);

        return builder.ToString();
    }

    /// <summary>
    /// One block of lines that can be shortened from the end, and knows how to say that it was.
    ///
    /// Sized by prefix sums and chosen by binary search rather than by rendering repeatedly and
    /// measuring. A repository with ten thousand tracked files would otherwise cost ten thousand
    /// renders of a growing string to answer one question.
    /// </summary>
    private sealed class Section(string heading, string noun, IReadOnlyList<string> lines)
    {
        private readonly int[] _prefix = BuildPrefix(lines);

        private int _shown = lines.Count;

        public int Length => LengthWhenShowing(_shown);

        public void TrimTo(int allowance)
        {
            // Binary search for the most lines that fit. Zero always "fits" in the sense that it is
            // the floor: if even the heading and the note exceed the allowance, the next section in
            // trim order gives up the rest.
            var low = 0;
            var high = _shown;

            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);

                if (LengthWhenShowing(middle) <= allowance)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            _shown = low;
        }

        public void Render(StringBuilder builder)
        {
            builder.Append(HeadingLine()).Append(Newline);

            for (var index = 0; index < _shown; index++)
            {
                builder.Append(lines[index]).Append(Newline);
            }

            if (_shown < lines.Count)
            {
                builder.Append(TrimNote(_shown)).Append(Newline);
            }

            builder.Append(Newline);
        }

        /// <summary>
        /// The full count, always, whatever is shown. It is the number that tells a reader the size of
        /// the thing they are looking at part of.
        /// </summary>
        private string HeadingLine() => $"{heading} ({lines.Count} {noun}):";

        /// <summary>
        /// What was cut and by how much, in the reader's own units. "tracked files trimmed: 120 of 366
        /// paths shown" answers the only question that matters about a truncated section — am I
        /// looking at the whole repository or a corner of it — without the reader having to count.
        /// </summary>
        private string TrimNote(int shown) =>
            $"[{heading} trimmed: {shown:N0} of {lines.Count:N0} {noun} shown]";

        private int LengthWhenShowing(int shown) =>
            HeadingLine().Length + 1
            + _prefix[shown]
            + (shown < lines.Count ? TrimNote(shown).Length + 1 : 0)
            + 1;

        private static int[] BuildPrefix(IReadOnlyList<string> lines)
        {
            var prefix = new int[lines.Count + 1];

            for (var index = 0; index < lines.Count; index++)
            {
                prefix[index + 1] = prefix[index] + lines[index].Length + 1;
            }

            return prefix;
        }
    }
}
