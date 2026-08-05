namespace FindFamiliar.Server.Domain;

/// <summary>
/// An append-only, user-visible turn in a project conversation.
///
/// This entity holds only what a person is shown, plus the operational metadata needed to attribute
/// a reply. There is deliberately no column for a prompt, a system contract, a thinking block, a tool
/// transcript, a raw request or response, or a provider exception. Hidden reasoning is not persisted,
/// not logged and not rendered — <see cref="ConversationMessage"/> already carries that rule, and it
/// is the one most easily lost while wiring a provider up, so it is stated here in the schema too:
/// the column does not exist, so nothing can write to it.
///
/// <see cref="ProviderName"/> and <see cref="ProviderModel"/> are nullable because Human and System
/// messages have no provider. Nothing here requires them for a Familiar message either: a degraded
/// or failed turn is a real outcome, and deciding what metadata each outcome carries belongs to the
/// conversation service, not to a database constraint that would reject the honest row.
/// </summary>
public sealed class FamiliarMessage
{
    public const int MaxContentLength = 8_000;
    public const int MaxProviderNameLength = 120;
    public const int MaxProviderModelLength = 120;
    public const int MaxFailureCodeLength = 64;

    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public FamiliarConversation Conversation { get; set; } = null!;

    public FamiliarMessageAuthor Author { get; set; }

    /// <summary>
    /// Stable per-conversation display order, unique within the conversation. Ordering never depends
    /// on timestamp ties.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>The visible text, and only the visible text.</summary>
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary>The reasoning provider that produced this reply. Null for Human and System messages.</summary>
    public string? ProviderName { get; set; }

    /// <summary>The model that produced this reply, so a later reader knows which one said what.</summary>
    public string? ProviderModel { get; set; }

    /// <summary>Round-trip time for the provider call. Operational metadata; never shown as content.</summary>
    public int? LatencyMs { get; set; }

    public FamiliarMessageDelivery Delivery { get; set; } = FamiliarMessageDelivery.Delivered;

    /// <summary>
    /// A fixed code this codebase writes, never provider text. Provider exception messages, hosts and
    /// paths have no route into this column because nothing composes it from them.
    /// </summary>
    public string? FailureCode { get; set; }

    public ICollection<FamiliarEvidence> Evidence { get; } = new List<FamiliarEvidence>();
}
