namespace FindFamiliar.Server.Domain;

/// <summary>
/// An append-only, visible entry in a conversation. Messages hold only what the user is shown.
/// They must never store hidden reasoning, provider tool chatter, credentials, or an unbounded
/// transcript — revising a proposal appends a new summary rather than rewriting history.
/// </summary>
public sealed class ConversationMessage
{
    public const int MaxContentLength = 8_000;

    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public ConversationMessageAuthor Author { get; set; }

    /// <summary>
    /// Stable per-conversation display order, unique within the conversation. Ordering never
    /// depends on timestamp ties.
    /// </summary>
    public int Sequence { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}
