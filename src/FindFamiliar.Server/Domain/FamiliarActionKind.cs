namespace FindFamiliar.Server.Domain;

/// <summary>
/// The complete set of actions a conversation may propose. Two, and there is deliberately no
/// <c>Unknown</c> or placeholder member: an unparseable kind must produce no proposal at all, and a
/// catch-all value would give one somewhere to be stored instead.
///
/// Every other candidate — handoff decisions, worker restarts, Git operations — either already has a
/// human-facing home or has no service boundary safe to drive from chat.
/// </summary>
public enum FamiliarActionKind
{
    /// <summary>One new Ready task in this project. No session starts.</summary>
    CreateTask,

    /// <summary>One Started Planner session on an existing task in this project.</summary>
    StartPlanner
}
