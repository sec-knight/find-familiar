using FindFamiliar.Server.Domain;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public enum WorkQueueActionKind
{
    /// <summary>
    /// More than one Started session on a task. IX_AgentSessions_TaskId_Started makes this
    /// unreachable through the application, so it now indicates a database restored from before that
    /// migration, or one written to directly. It is kept deliberately: the index is absent from such
    /// a database, and this is the only place the corruption becomes visible (ADR-0010).
    /// </summary>
    NeedsAttention,
    ContinueSession,
    StartPlanner,
    RetryRole,
    StartImplementer,
    StartReviewer,
    HumanDecision,

    /// <summary>A proposed next step is waiting for a human decision on the task page.</summary>
    ApproveHandoff
}

public sealed record WorkQueueItem(
    Guid ProjectId,
    string ProjectName,
    Guid TaskId,
    string TaskTitle,
    TaskStatus TaskStatus,
    DateTime TaskUpdatedUtc,
    WorkQueueActionKind ActionKind,
    string ActionLabel,
    Guid? ActiveSessionId,
    AgentSessionRole? ActiveSessionRole,
    int StartedSessionCount,
    AgentSessionRole? PendingHandoffRole = null);
