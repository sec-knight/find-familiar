using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public enum StartSessionStatus
{
    Started,
    NotFound,

    /// <summary>The task already owns a Started session.</summary>
    AlreadyStarted,

    ProjectInactive
}

public sealed record StartSessionOutcome(StartSessionStatus Status, AgentSession? Session = null)
{
    public static readonly StartSessionOutcome NotFound = new(StartSessionStatus.NotFound);
    public static readonly StartSessionOutcome AlreadyStarted = new(StartSessionStatus.AlreadyStarted);
    public static readonly StartSessionOutcome ProjectInactive = new(StartSessionStatus.ProjectInactive);
}

public interface IWorkflowDispatchService
{
    /// <summary>True when the task already owns a Started session, which forbids starting another.</summary>
    Task<bool> HasStartedSessionAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a session on an existing task and commits it, mapping a lost race to a typed outcome.
    /// Used by the manual start form, which has no proposal row to fence against.
    /// </summary>
    Task<StartSessionOutcome> StartSessionForTaskAsync(
        Guid taskId,
        AgentSessionRole role,
        string? provider,
        string? externalSessionReference,
        DateTime startedUtc,
        CancellationToken cancellationToken = default);

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

    public async Task<StartSessionOutcome> StartSessionForTaskAsync(
        Guid taskId,
        AgentSessionRole role,
        string? provider,
        string? externalSessionReference,
        DateTime startedUtc,
        CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks
            .Include(candidate => candidate.Project)
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);

        if (task is null)
        {
            return StartSessionOutcome.NotFound;
        }

        // A friendly pre-check only. Unlike handoff approval there is no proposal row to consume
        // conditionally, so IX_AgentSessions_TaskId_Started is the actual enforcement here — this
        // read just turns the common case into a readable message instead of a constraint violation.
        if (await HasStartedSessionAsync(taskId, cancellationToken))
        {
            return StartSessionOutcome.AlreadyStarted;
        }

        var session = StartSession(task, task.Project, role, provider, externalSessionReference, startedUtc);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is DbUpdateException or SqliteException
            && SessionHandoffApprovalService.IsUniqueConstraintViolation(exception))
        {
            // The index caught a session that committed between the check above and this write.
            dbContext.ChangeTracker.Clear();
            return StartSessionOutcome.AlreadyStarted;
        }

        return new StartSessionOutcome(StartSessionStatus.Started, session);
    }

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
