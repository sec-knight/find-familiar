namespace FindFamiliar.Server.Domain;

/// <summary>
/// Where a turn is in its life. Persisted as text, and the two in-flight values are named in the
/// filter of <c>IX_FamiliarChatTurns_ChatId_InFlight</c> — the index that enforces one turn in flight
/// per conversation.
///
/// There is no Interrupted state. A generation the server never finished is a failure with a specific
/// code, exactly as <see cref="FamiliarMessageDelivery.Failed"/> carries one: the outcome is the same
/// — no usable reply — and the reason belongs in a code, not in a state that every reader of this
/// enum would then have to handle.
/// </summary>
public enum FamiliarChatTurnState
{
    /// <summary>Accepted and durable; no generator holds it yet.</summary>
    Pending,

    /// <summary>A generator holds it and is accumulating output into the row.</summary>
    Generating,

    /// <summary>Finished. <c>Output</c> is final and will not change again.</summary>
    Completed,

    /// <summary>
    /// No usable reply exists. <c>FailureCode</c> says why, and <c>Output</c> holds this
    /// application's own sentence about it — never a provider's error text.
    /// </summary>
    Failed
}
