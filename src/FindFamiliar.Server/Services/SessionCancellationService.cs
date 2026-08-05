using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum SessionCancellationStatus
{
    Success,
    ValidationFailed,
    NotFound,
    NotStarted,
    ClaimLost
}

public sealed record SessionCancellationRequest(
    Guid TaskId,
    Guid SessionId,
    string? Reason,
    Guid? ClaimId = null,
    bool RequireClaimOwnership = false);

public sealed record SessionCancellationOutcome(
    SessionCancellationStatus Status,
    AgentSessionRole? Role = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly SessionCancellationOutcome NotFound = new(SessionCancellationStatus.NotFound);
    public static readonly SessionCancellationOutcome NotStarted = new(SessionCancellationStatus.NotStarted);
    public static readonly SessionCancellationOutcome ClaimLost = new(SessionCancellationStatus.ClaimLost);

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
public sealed class SessionCancellationService(
    FamiliarDbContext dbContext,
    ISessionHandoffService sessionHandoffs) : ISessionCancellationService
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

        if (request.RequireClaimOwnership &&
            (session.ClaimId != request.ClaimId
                || (session.ClaimId is not null && session.ClaimExpiresUtc <= cancelledUtc)))
        {
            return SessionCancellationOutcome.ClaimLost;
        }

        var role = session.Role;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var transition = dbContext.AgentSessions.Where(candidate =>
            candidate.Id == request.SessionId
            && candidate.TaskId == request.TaskId
            && candidate.Status == AgentSessionStatus.Started);

        if (request.RequireClaimOwnership)
        {
            transition = transition.Where(candidate =>
                candidate.ClaimId == request.ClaimId
                && (candidate.ClaimId == null || candidate.ClaimExpiresUtc > cancelledUtc));
        }

        var transitioned = await transition.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(candidate => candidate.Status, AgentSessionStatus.Cancelled)
                .SetProperty(candidate => candidate.CompletedUtc, cancelledUtc),
            cancellationToken);

        if (transitioned != 1)
        {
            return request.RequireClaimOwnership
                ? SessionCancellationOutcome.ClaimLost
                : SessionCancellationOutcome.NotStarted;
        }

        session.Status = AgentSessionStatus.Cancelled;
        session.CompletedUtc = cancelledUtc;
        dbContext.Entry(session).Property(candidate => candidate.Status).OriginalValue = AgentSessionStatus.Cancelled;
        dbContext.Entry(session).Property(candidate => candidate.CompletedUtc).OriginalValue = cancelledUtc;

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

        session.Task.UpdatedUtc = cancelledUtc;
        session.Task.Project.IncrementContextRevision();

        // A cancelled attempt proposes a retry of the same role, staged in this same transaction.
        // It creates no work and does not move the revision (ADR-0010).
        await sessionHandoffs.StageHandoffAsync(
            session,
            session.Task.Project.ContextRevision,
            cancelledUtc,
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return request.RequireClaimOwnership
                ? SessionCancellationOutcome.ClaimLost
                : SessionCancellationOutcome.NotStarted;
        }

        return SessionCancellationOutcome.Success(role);
    }
}
