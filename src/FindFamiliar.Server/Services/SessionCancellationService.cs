using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum SessionCancellationStatus
{
    Success,
    ValidationFailed,
    NotFound,
    NotStarted
}

public sealed record SessionCancellationRequest(Guid TaskId, Guid SessionId, string? Reason);

public sealed record SessionCancellationOutcome(
    SessionCancellationStatus Status,
    AgentSessionRole? Role = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly SessionCancellationOutcome NotFound = new(SessionCancellationStatus.NotFound);
    public static readonly SessionCancellationOutcome NotStarted = new(SessionCancellationStatus.NotStarted);

    public static SessionCancellationOutcome Success(AgentSessionRole role) =>
        new(SessionCancellationStatus.Success, role);

    public static SessionCancellationOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(SessionCancellationStatus.ValidationFailed, ValidationErrors: errors);
}

public interface ISessionCancellationService
{
    Task<SessionCancellationOutcome> CancelAsync(
        SessionCancellationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single atomic cancellation transaction, used by both Task Details and the runner API
/// (including pre-submit adapter-failure cancellation).
/// </summary>
public sealed class SessionCancellationService(FamiliarDbContext dbContext) : ISessionCancellationService
{
    public const int ReasonMaxLength = 2_000;

    public async Task<SessionCancellationOutcome> CancelAsync(
        SessionCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return SessionCancellationOutcome.ValidationFailed(
                new Dictionary<string, string> { ["Reason"] = "A cancellation reason is required." });
        }

        if (request.Reason.Length > ReasonMaxLength)
        {
            return SessionCancellationOutcome.ValidationFailed(
                new Dictionary<string, string> { ["Reason"] = $"Reason must be {ReasonMaxLength} characters or fewer." });
        }

        var session = await dbContext.AgentSessions
            .Include(candidate => candidate.Task)
            .ThenInclude(task => task.Project)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == request.SessionId && candidate.TaskId == request.TaskId,
                cancellationToken);

        if (session is null)
        {
            return SessionCancellationOutcome.NotFound;
        }

        if (session.Status != AgentSessionStatus.Started)
        {
            return SessionCancellationOutcome.NotStarted;
        }

        var cancelledUtc = DateTime.UtcNow;
        var role = session.Role;

        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = session.Task.ProjectId,
            TaskId = session.TaskId,
            SourceSessionId = session.Id,
            Kind = ContextEntryKind.Handoff,
            Title = $"{role} session cancelled",
            Content = request.Reason.Trim(),
            State = ContextEntryState.Active,
            CreatedUtc = cancelledUtc
        });

        session.Status = AgentSessionStatus.Cancelled;
        session.CompletedUtc = cancelledUtc;
        session.Task.UpdatedUtc = cancelledUtc;
        session.Task.Project.IncrementContextRevision();

        await dbContext.SaveChangesAsync(cancellationToken);

        return SessionCancellationOutcome.Success(role);
    }
}
