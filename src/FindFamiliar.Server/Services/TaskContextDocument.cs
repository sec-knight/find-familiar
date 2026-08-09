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

/// <param name="IsSensitive">
/// Whether the user marked this entry as never leaving the machine.
///
/// Carried rather than filtered here on purpose. This document serves assignment packets, whose
/// reader is a worker running on the owner's own hardware and is entitled to the whole record; and it
/// serves the Familiar gateway, whose reader is a credential a vendor holds and is not. A projection
/// that dropped the flag would force the second caller to enforce a rule it had no way to evaluate —
/// which is exactly the bug this field was added to fix.
/// </param>
public sealed record ContextEntryDocument(
    Guid Id,
    ContextEntryKind Kind,
    string Title,
    string Content,
    DateTime CreatedUtc,
    Guid? SourceSessionId,
    bool IsSensitive = false);

public sealed record AgentSessionDocument(
    Guid Id,
    AgentSessionRole Role,
    string? Provider,
    string? ExternalSessionReference,
    AgentSessionStatus Status,
    int ContextRevisionRead,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string? FailureCategory = null,
    int? FailureAdapterExitCode = null,
    bool? FailureProviderLaunched = null,
    int? FailureProviderExitCode = null,
    string? FailureMessage = null);
