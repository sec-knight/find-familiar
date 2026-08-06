namespace FindFamiliar.Server.Domain;

/// <summary>
/// A system-wide conversation with the Familiar: an ordered run of exchanges, owned by the server.
///
/// Deliberately not <see cref="FamiliarConversation"/>, for the same reason that entity is not
/// <see cref="Conversation"/>. A <see cref="FamiliarConversation"/> is one per project, enforced by a
/// unique index, and <c>FamiliarActionService</c>'s safety rests on that plus its at-most-one-pending
/// proposal index. This aggregate is the opposite shape — many per system, none per project, no
/// proposals at all — so it lives in its own tables and shares nothing but the database.
///
/// <see cref="FocusProjectId"/> is nullable and mutable, and it never restricts what the Familiar can
/// see. It biases retrieval ranking and resolves pronouns; cross-project questions are the point of a
/// system-wide conversation, and a focus that filtered evidence would quietly defeat that.
///
/// The server is the conversation and the client is a window onto it. Nothing that matters lives in
/// the browser: the turn list, its order, and which turn is generating are all read from here, so two
/// devices looking at the same chat necessarily see the same thing.
/// </summary>
public sealed class FamiliarChat
{
    public const int MaxTitleLength = 120;

    public Guid Id { get; set; }

    /// <summary>
    /// A short label composed by this application from the opening message, never by a model. It is
    /// navigation text, so it is the server's words about what was asked rather than a provider's
    /// summary of it.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The project this conversation is currently about, or null for no particular one. Mutable, and
    /// never a filter — see the type remarks.
    /// </summary>
    public Guid? FocusProjectId { get; set; }

    public FamiliarProject? FocusProject { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Moves on every turn, so the conversation list can order by recent activity.</summary>
    public DateTime UpdatedUtc { get; set; }

    public ICollection<FamiliarChatTurn> Turns { get; } = new List<FamiliarChatTurn>();
}
