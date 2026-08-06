using System.Text;

namespace FindFamiliar.Server.Services.Familiar.Chat.Retrieval;

/// <summary>
/// The retrieved context as text a model reads.
///
/// Same shape as the standing brief's writer, and deliberately so — one format for everything the
/// Familiar is shown means one thing to learn about how to read it. Ids are written on every entry
/// because an answer is expected to cite them, and slice 2 validates those citations against exactly
/// the ids written here.
///
/// This block is <b>volatile</b>: it changes with every message. It therefore belongs after the
/// history in the assembled request, not beside the standing brief — putting per-message content in
/// the prompt's stable head would invalidate the provider's prefix cache on every single turn, which
/// is roughly a six-fold cost increase for the part of the prompt that never changes.
/// </summary>
public static class FamiliarRetrievalWriter
{
    /// <summary>
    /// The block, or null when there is nothing to say and no search was made.
    ///
    /// Note the asymmetry: a search that ran and found nothing still writes a block. "I looked and
    /// there is nothing recorded about this" is a fact worth more than most of what retrieval returns
    /// when it succeeds, because it is the difference between an answer that says "no decision was
    /// recorded" and one that invents a plausible decision.
    /// </summary>
    public static string? Write(FamiliarRetrievalResult result)
    {
        if (result.Terms.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        builder.AppendLine("<recorded_context>");
        builder.AppendLine(
            "Searched this system's recorded context for the message below. These are the entries it "
            + "found — quotations from durable records, not recollection.");
        builder.Append("searched for: ").AppendLine(string.Join(", ", result.Terms));
        builder.AppendLine();

        if (result.FoundNothing)
        {
            builder.AppendLine(
                "Nothing recorded matches this. That is a fact about the records, not about the world: "
                + "say that nothing is written down, and do not supply an answer from general "
                + "knowledge as though it came from these records.");
        }

        foreach (var entry in result.Entries)
        {
            builder.Append("<entry id=\"").Append(entry.EntryId).AppendLine("\">");
            builder.Append("kind: ").AppendLine(entry.Kind.ToString());
            builder.Append("project: ").AppendLine(entry.ProjectName);
            builder.Append("recorded: ").AppendLine(entry.CreatedUtc.ToString("yyyy-MM-dd"));
            builder.Append("title: ").AppendLine(Collapse(entry.Title));
            builder.AppendLine(entry.IsExcerpted ? "content (excerpt):" : "content:");
            builder.AppendLine(entry.Excerpt);
            builder.AppendLine("</entry>");
            builder.AppendLine();
        }

        if (result.SensitiveWithheld > 0)
        {
            // What, not which. The count is the honest disclosure; the content is the thing being
            // protected.
            builder
                .Append(result.SensitiveWithheld)
                .AppendLine(
                    " further entr(ies) are marked sensitive and were excluded from this search "
                    + "entirely. They may be relevant and you cannot see them.");
        }

        builder.AppendLine(
            "Cite an entry by its id when you use it. Do not cite an id that does not appear above.");
        builder.AppendLine("</recorded_context>");

        return builder.ToString();
    }

    /// <summary>One line of single-spaced text, so a title with a newline cannot break the format.</summary>
    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
