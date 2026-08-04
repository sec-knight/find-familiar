using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public interface IWorkflowDispatchService
{
    /// <summary>True when the task already owns a Started session, which forbids starting another.</summary>
    Task<bool> HasStartedSessionAsync(Guid taskId, CancellationToken cancellationToken = default);

    FamiliarTask CreateReadyTask(FamiliarProject project, string title, string requestedOutcome, DateTime nowUtc);

    AgentSession StartSession(
        FamiliarTask task,
        FamiliarProject project,
        AgentSessionRole role,
        string? provider,
        string? externalSessionReference,
        DateTime startedUtc);
}

/// <summary>
/// The two workflow mutations that both the manual pages and conversational approval perform:
/// creating a Ready task and starting a session.
///
/// They live here so the context-revision effects have exactly one definition. Task creation
/// advances the project revision once; starting a session advances it again and then records the
/// revision the session actually reads. A second copy of those rules is how manual and approved
/// creation would silently develop conflicting invariants.
///
/// Both methods only stage entities on the supplied <see cref="FamiliarDbContext"/>. The caller
/// owns <c>SaveChangesAsync</c> and any surrounding transaction, so conversational approval can
/// commit a task, a session, links and a message as one unit.
/// </summary>
public sealed class WorkflowDispatchService(FamiliarDbContext dbContext) : IWorkflowDispatchService
{
    public Task<bool> HasStartedSessionAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        dbContext.AgentSessions.AnyAsync(
            candidate => candidate.TaskId == taskId && candidate.Status == AgentSessionStatus.Started,
            cancellationToken);

    public FamiliarTask CreateReadyTask(
        FamiliarProject project,
        string title,
        string requestedOutcome,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(project);

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = title.Trim(),
            RequestedOutcome = requestedOutcome.Trim(),
            Status = TaskStatus.Ready,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        project.IncrementContextRevision();
        dbContext.Tasks.Add(task);
        return task;
    }

    public AgentSession StartSession(
        FamiliarTask task,
        FamiliarProject project,
        AgentSessionRole role,
        string? provider,
        string? externalSessionReference,
        DateTime startedUtc)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(project);

        // Increment first, then read: the session records the revision visible at its own start,
        // including the increment that starting it caused.
        project.IncrementContextRevision();

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Provider = NullIfWhiteSpace(provider),
            ExternalSessionReference = NullIfWhiteSpace(externalSessionReference),
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = project.ContextRevision,
            StartedUtc = startedUtc
        };

        dbContext.AgentSessions.Add(session);
        return session;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
