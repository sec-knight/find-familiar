using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>One piece of a reply: either text, or a reference to a context entry.</summary>
/// <param name="IsSupported">
/// True when the id was in the pack this turn was answered from. False means the reply named
/// something it was never shown, and the renderer must not present it as a source.
/// </param>
public sealed record FamiliarReplySegment(string Text, Guid? EntryId = null, bool IsSupported = false)
{
    public bool IsCitation => EntryId is not null;
}

/// <summary>
/// Finds the citations in a reply and says which of them were earned.
///
/// A reply is model-written text and stays inert everywhere — no HTML is produced here, only a list
/// of pieces a renderer walks. Both renderers walk it: the Razor page and the browser script build
/// the same shape from the same segmentation, because a page that arrived by render and one built by
/// the stream must be indistinguishable.
///
/// <b>Ids are recognised wherever they appear, rather than inside a marker syntax.</b> Asking a model
/// to wrap citations in delimiters means a citation is lost every time it does not comply, and it is
/// least likely to comply on exactly the long, dense answers where sourcing matters most. A bare id
/// cannot be missed, and the check that matters is not whether the syntax was right — it is whether
/// the id was in the pack.
///
/// An id that was not in the pack is kept and marked, never silently deleted. Dropping it would hide
/// the most diagnostic thing a reply can do: name a source that does not exist. A reader should see
/// that happen, and so should anyone reading the transcript afterwards.
///
/// <b>"The pack" is not the same test for every kind of id, and the difference is epistemic rather
/// than a relaxation.</b> A context entry is one of thousands, of which a handful were retrieved, so
/// its existence proves nothing about its having been shown and the recorded pack is the only honest
/// check. A project id or a task id reaches a reply through one channel only — the standing brief —
/// so "this names a real project or task the reader may see" <i>is</i> evidence it was shown, and it
/// is a check the brief cannot fail to have made, because it is what the brief is built from. Holding
/// project and task ids to the entry pack instead is what put the literal words "unsupported
/// reference" in front of readers wherever the Familiar named a task it had been handed.
/// </summary>
public static class FamiliarChatCitations
{
    /// <summary>The canonical 8-4-4-4-12 form, which is how a model repeats an id it was shown.</summary>
    private const int IdLength = 36;

    /// <summary>
    /// Splits a reply into text and citations.
    ///
    /// Returns a single text segment when there is nothing to find, which is the common case and
    /// costs one allocation.
    /// </summary>
    public static IReadOnlyList<FamiliarReplySegment> Segment(string? output, IReadOnlyCollection<Guid> offered)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        List<FamiliarReplySegment>? segments = null;
        var textStart = 0;
        var index = 0;

        while (index <= output.Length - IdLength)
        {
            if (!TryReadId(output, index, out var entryId))
            {
                index++;
                continue;
            }

            segments ??= [];

            if (index > textStart)
            {
                segments.Add(new FamiliarReplySegment(output[textStart..index]));
            }

            segments.Add(new FamiliarReplySegment(
                output.Substring(index, IdLength),
                entryId,
                offered.Contains(entryId)));

            index += IdLength;
            textStart = index;
        }

        if (segments is null)
        {
            return [new FamiliarReplySegment(output)];
        }

        if (textStart < output.Length)
        {
            segments.Add(new FamiliarReplySegment(output[textStart..]));
        }

        return segments;
    }

    /// <summary>
    /// Every canonical id a reply names, distinct, in the order they appear.
    ///
    /// The same scan <see cref="Segment"/> performs, exposed so a caller can go and find out what the
    /// ids point at before deciding how to render them. Sharing <see cref="TryReadId"/> is the point:
    /// a second scanner with its own boundary rules would eventually disagree with the segmenter about
    /// what an id is, and the disagreement would show as a chip in one renderer and plain text in the
    /// other.
    /// </summary>
    /// <param name="limit">
    /// A bound, not a guess. Resolving these costs a query, and a reply that is somehow a thousand
    /// ids long must not turn one page render into a thousand-row lookup.
    /// </param>
    public static IReadOnlyList<Guid> FindIds(string? output, int limit = 32)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var found = new List<Guid>();
        var index = 0;

        while (index <= output.Length - IdLength && found.Count < limit)
        {
            if (!TryReadId(output, index, out var entryId))
            {
                index++;
                continue;
            }

            if (!found.Contains(entryId))
            {
                found.Add(entryId);
            }

            index += IdLength;
        }

        return found;
    }

    /// <summary>
    /// True when a canonical id starts exactly here and is not part of a longer run of id characters.
    ///
    /// The boundary check matters: without it the tail of a longer token would parse as an id, and a
    /// citation would be invented out of something that was never one.
    /// </summary>
    private static bool TryReadId(string output, int index, out Guid entryId)
    {
        entryId = default;

        if (index > 0 && IsIdCharacter(output[index - 1]))
        {
            return false;
        }

        var end = index + IdLength;

        if (end < output.Length && IsIdCharacter(output[end]))
        {
            return false;
        }

        return Guid.TryParseExact(output.AsSpan(index, IdLength), "D", out entryId);
    }

    private static bool IsIdCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '-';

    /// <summary>
    /// The ids a turn was offered, as stored. Order preserved; unreadable fragments skipped rather
    /// than failing a read of an otherwise good transcript.
    /// </summary>
    public static IReadOnlyList<Guid> ParseEvidence(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var ids = new List<Guid>();

        foreach (var part in stored.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// The compact stored form: 32-character ids, space separated, bounded by the column.
    ///
    /// Compact rather than canonical because the column is a fixed budget and this doubles what fits
    /// in it. The canonical form is what a model reads and writes; this is only how the row records
    /// what it was given.
    /// </summary>
    public static string? SerialiseEvidence(IReadOnlyCollection<Guid> entryIds)
    {
        if (entryIds.Count == 0)
        {
            return null;
        }

        var stored = string.Join(' ', entryIds.Select(id => id.ToString("N")));

        return stored.Length <= FamiliarChatTurn.MaxEvidenceLength
            ? stored
            // Trimmed at a whole id rather than mid-token, so what survives is still readable as ids.
            : stored[..(FamiliarChatTurn.MaxEvidenceLength / 33 * 33 - 1)];
    }
}
