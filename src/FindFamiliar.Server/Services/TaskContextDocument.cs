using FindFamiliar.Server.Domain;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public sealed record TaskContextDocument(
    ProjectContextDocument Project,
    TaskContextTaskDocument Task,
    IReadOnlyList<ContextEntryDocument> ProjectEntries,
    IReadOnlyList<ContextEntryDocument> TaskEntries,
    IReadOnlyList<AgentSessionDocument> Sessions);

public sealed record ProjectContextDocument(
    Guid Id,
    string Name,
    string Purpose,
    ProjectStatus Status,
    int ContextRevision);

public sealed record TaskContextTaskDocument(
    Guid Id,
    string Title,
    string RequestedOutcome,
    TaskStatus Status,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record ContextEntryDocument(
    Guid Id,
    ContextEntryKind Kind,
    string Title,
    string Content,
    DateTime CreatedUtc,
    Guid? SourceSessionId);

public sealed record AgentSessionDocument(
    Guid Id,
    AgentSessionRole Role,
    string? Provider,
    string? ExternalSessionReference,
    AgentSessionStatus Status,
    int ContextRevisionRead,
    DateTime StartedUtc,
    DateTime? CompletedUtc);
