namespace FindFamiliar.Server.Domain;

/// <summary>
/// One exchange in a <see cref="FamiliarChat"/>: what a person said, and what came back.
///
/// A turn is the pair, not a message. That is what makes "at most one turn in flight per
/// conversation" a statement the database can enforce with a single filtered unique index, and what
/// makes resume-from-sequence a single ordered read rather than a join across two authors.
///
/// <see cref="Sequence"/> is monotonic within the conversation and unique. Order never depends on a
/// timestamp: two turns written in the same tick must still have one correct order, and this is the
/// column that guarantees it. Every client read is "give me everything after sequence N", so a device
/// that has been gone four hours takes exactly the same path as one that has been gone four seconds.
///
/// <see cref="Output"/> accumulates while the state is <see cref="FamiliarChatTurnState.Generating"/>
/// and is final afterwards. It accumulates into this row rather than into the connection that asked
/// for it: closing a laptop mid-reply must not end the reply.
///
/// As with <see cref="FamiliarMessage"/>, there is no column for a prompt, a system contract, a
/// thinking block, a tool transcript, a raw payload or a provider exception. The column does not
/// exist, so nothing can write to it.
/// </summary>
public sealed class FamiliarChatTurn
{
    /// <summary>Longer than any question a person types in one go.</summary>
    public const int MaxUserTextLength = 4_000;

    /// <summary>
    /// A cap on one reply, not on a conversation. Sprint 12 caps working memory rather than
    /// compacting it, and this is where that decision lands in the schema.
    /// </summary>
    public const int MaxOutputLength = 24_000;

    public const int MaxFailureCodeLength = 64;

    public const int MaxProviderNameLength = 120;

    public const int MaxProviderModelLength = 120;

    public Guid Id { get; set; }

    public Guid ChatId { get; set; }

    public FamiliarChat Chat { get; set; } = null!;

    /// <summary>Stable per-conversation order, unique within the conversation.</summary>
    public int Sequence { get; set; }

    public FamiliarChatTurnState State { get; set; } = FamiliarChatTurnState.Pending;

    /// <summary>What the person typed. Never rewritten, including on a retry.</summary>
    public string UserText { get; set; } = string.Empty;

    /// <summary>
    /// The conversation's focus when this turn was accepted, recorded because the focus is mutable
    /// and a later reader must be able to tell what it was at the time. Deliberately without a
    /// foreign key: this is a historical fact about a turn, and deleting the project it names must
    /// not silently rewrite it.
    /// </summary>
    public Guid? FocusProjectIdAtTime { get; set; }

    /// <summary>
    /// The visible reply, and only the visible reply. Grows while generating; final once the state
    /// is terminal. On a <see cref="FamiliarChatTurnState.Failed"/> turn this holds this
    /// application's own sentence about the failure.
    /// </summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// A fixed code this codebase writes, never provider text. Provider exception messages, hosts
    /// and paths have no route into this column because nothing composes it from them.
    /// </summary>
    public string? FailureCode { get; set; }

    /// <summary>
    /// The provider that produced this reply, and the model it actually used. Null while pending, and
    /// null for a turn that failed before anything answered.
    ///
    /// The model is recorded as the endpoint resolved it, not as configuration asked for it: a proxy
    /// may resolve an alias, and a transcript should say what really replied. Storing it is what makes
    /// "AI providers are replaceable" testable — a turn can be re-run against another model and the
    /// two compared.
    /// </summary>
    public string? ProviderName { get; set; }

    public string? ProviderModel { get; set; }

    /// <summary>
    /// Token counts as the provider reported them. Operational metadata, never shown as content.
    ///
    /// Named Input/Output rather than Prompt/Completion on purpose. <c>FamiliarConversationModelTests</c>
    /// scans every column in the model and in the migrated database for names suggesting forbidden
    /// storage, and "Prompt" is one of the fragments it rejects. A count is not a prompt, so this is a
    /// false positive — but the guard's bluntness is exactly its value, and carving an exception into it
    /// is how a genuine <c>PromptText</c> column would later slip past. Renaming the count is the cheaper
    /// side of that trade. The wire names stay <c>prompt_tokens</c> and <c>completion_tokens</c>, because
    /// those belong to the provider's schema rather than to this one.
    /// </summary>
    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary>
    /// How much of the input was served from the provider's prefix cache, when it says.
    ///
    /// Recorded because it is the measurement that tells whether the stable-to-volatile prompt
    /// ordering is actually working. A standing brief that stopped being cached would cost several
    /// times more per turn and change nothing else observable, so without this column the regression
    /// would be invisible until a bill arrived.
    /// </summary>
    public int? CachedInputTokens { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When a generator took the turn. Null while Pending.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>When the turn reached a terminal state. Null until then.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>True for the two states <c>IX_FamiliarChatTurns_ChatId_InFlight</c> covers.</summary>
    public bool IsInFlight =>
        State is FamiliarChatTurnState.Pending or FamiliarChatTurnState.Generating;
}
