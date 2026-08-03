using FindFamiliar.Server.Domain;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public enum WorkQueueActionKind
{
    NeedsAttention,
    ContinueSession,
    StartPlanner,
    RetryRole,
    StartImplementer,
    StartReviewer,
    HumanDecision
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
    int StartedSessionCount);
