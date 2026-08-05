using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Providers;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services.Demiplane;

/// <summary>
/// One step in a task's session chain. This is the only real graph structure the domain has: tasks
/// have no dependencies on each other, so the map draws what exists rather than inventing edges.
/// </summary>
public sealed record TaskChainStep(
    Guid? SessionId,
    AgentSessionRole Role,
    AgentSessionStatus? Status,
    bool IsProposed,
    bool IsCurrent,
    DateTime? StartedUtc,
    DateTime? CompletedUtc);

/// <summary>
/// The Familiar's plain-language account of a task. Every field is derived from persisted history;
/// when the data cannot support a statement the field is null and the UI says so rather than guessing.
/// </summary>
public sealed record FamiliarSummary(
    string WhatHappened,
    string CurrentState,
    string? WhyWaiting,
    string? NeedsAttention,
    string? RecommendedNextAction,
    string? OutcomeDetail);

/// <summary>A task as the Demiplane shows it.</summary>
public sealed record DemiplaneTask(
    Guid TaskId,
    string Title,
    TaskStatus TaskStatus,
    TaskDisplayState DisplayState,
    TaskDisplayReasonCode ReasonCode,
    string ReasonText,
    bool NeedsHumanAttention,
    DateTime UpdatedUtc,
    IReadOnlyList<TaskChainStep> Chain,
    Guid? CurrentSessionId,
    AgentSessionRole? CurrentRole,
    string? Provider,
    Guid? PendingHandoffId,
    Guid? PendingHandoffToken,
    AgentSessionRole? ProposedRole,
    SessionHandoffKind? ProposedKind,
    FamiliarSummary Summary);

/// <summary>The whole project surface: health, activity, decisions, tasks and provider readiness.</summary>
public sealed record DemiplaneProjection(
    Guid ProjectId,
    string ProjectName,
    string ProjectPurpose,
    ProjectStatus ProjectStatus,
    int ContextRevision,
    IReadOnlyList<DemiplaneTask> Tasks,
    IReadOnlyList<ProviderCapacitySnapshot> Providers,
    DateTimeOffset ObservedAt)
{
    public IReadOnlyList<DemiplaneTask> NeedsAttention =>
        Tasks.Where(task => task.NeedsHumanAttention).ToList();

    public IReadOnlyList<DemiplaneTask> Running =>
        Tasks.Where(task => task.DisplayState == TaskDisplayState.Running).ToList();

    public int CountOf(TaskDisplayState state) => Tasks.Count(task => task.DisplayState == state);

    /// <summary>True when any task is running or waiting, so the page has a reason to refresh.</summary>
    public bool HasActiveWork =>
        Tasks.Any(task => task.DisplayState is TaskDisplayState.Running or TaskDisplayState.Waiting);
}
