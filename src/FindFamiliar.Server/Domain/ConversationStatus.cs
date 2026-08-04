namespace FindFamiliar.Server.Domain;

/// <summary>
/// Lifecycle of a work-intake conversation. Approved and Rejected are terminal in Sprint 08.
/// This status explains what the user asked for and whether they approved it — it is never
/// execution authority. <see cref="AgentSessionStatus"/> and <see cref="TaskStatus"/> remain
/// the authority for what may run (ADR-0009).
/// </summary>
public enum ConversationStatus
{
    AwaitingApproval,
    Approved,
    Rejected
}
