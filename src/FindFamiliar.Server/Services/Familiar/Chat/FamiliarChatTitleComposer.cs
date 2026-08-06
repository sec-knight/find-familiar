using System.Text;
using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// The label a conversation carries in the list, composed by this application from the opening
/// message.
///
/// Composed here rather than asked of a model, for the same reason <c>FamiliarEvidence.Label</c> is:
/// navigation text is a claim this system makes about its own records. A model-written title would
/// also mean the list could not be rendered until a reply arrived, and the conversation is durable
/// from the moment it is created.
/// </summary>
public static class FamiliarChatTitleComposer
{
    /// <summary>Shorter than the column, because this is a list entry and not a heading.</summary>
    public const int MaxLength = 72;

    /// <summary>Used when the opening message carries nothing renderable, e.g. only punctuation.</summary>
    public const string Fallback = "New conversation";

    public static string Compose(string openingMessage)
    {
        var collapsed = Collapse(openingMessage);

        if (collapsed.Length == 0)
        {
            return Fallback;
        }

        if (collapsed.Length <= MaxLength)
        {
            return collapsed;
        }

        // Cut on a word boundary when there is one reasonably near the end, so the label reads as a
        // truncated phrase rather than a severed word.
        var cut = collapsed[..MaxLength];
        var lastSpace = cut.LastIndexOf(' ');
        var stem = lastSpace >= MaxLength / 2 ? cut[..lastSpace] : cut;

        return stem.TrimEnd() + "…";
    }

    /// <summary>
    /// One line of single-spaced text. Line breaks and runs of whitespace are collapsed rather than
    /// preserved: a title is rendered in a fixed-height list row, and a newline there would either
    /// be swallowed silently or break the row.
    /// </summary>
    private static string Collapse(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, FamiliarChat.MaxTitleLength));
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

            if (builder.Length >= FamiliarChat.MaxTitleLength)
            {
                break;
            }
        }

        return builder.ToString();
    }
}
