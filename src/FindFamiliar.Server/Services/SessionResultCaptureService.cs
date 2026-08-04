using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum SessionResultCaptureStatus
{
    Success,
    ValidationFailed,
    NotFound,
    NotStarted,
    ClaimLost
}

public sealed record SessionResultCaptureRequest(
    Guid TaskId,
    Guid SessionId,
    string? Prompt,
    string? RawOutput,
    string? Summary,
    string? ArtifactTitle,
    string? ArtifactContent,
    Guid? ClaimId = null,
    bool RequireClaimOwnership = false);

public sealed record SessionResultCaptureOutcome(
    SessionResultCaptureStatus Status,
    AgentSessionRole? Role = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly SessionResultCaptureOutcome NotFound = new(SessionResultCaptureStatus.NotFound);
    public static readonly SessionResultCaptureOutcome NotStarted = new(SessionResultCaptureStatus.NotStarted);
    public static readonly SessionResultCaptureOutcome ClaimLost = new(SessionResultCaptureStatus.ClaimLost);

    public static SessionResultCaptureOutcome Success(AgentSessionRole role) =>
        new(SessionResultCaptureStatus.Success, role);

    public static SessionResultCaptureOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(SessionResultCaptureStatus.ValidationFailed, ValidationErrors: errors);
}

public interface ISessionResultCaptureService
{
    Task<SessionResultCaptureOutcome> CaptureAsync(
        SessionResultCaptureRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single atomic result-capture transaction, used by both Task Details and the runner API.
/// Central validation and provenance derivation live here so neither caller can bypass them.
/// </summary>
public sealed class SessionResultCaptureService(FamiliarDbContext dbContext) : ISessionResultCaptureService
{
    public const int LongFieldMaxLength = 12_000;
    public const int SummaryMaxLength = 4_000;
    public const int ArtifactTitleMaxLength = 200;

    public async Task<SessionResultCaptureOutcome> CaptureAsync(
        SessionResultCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return SessionResultCaptureOutcome.ValidationFailed(errors);
        }

        var session = await dbContext.AgentSessions
            .Include(candidate => candidate.Task)
            .ThenInclude(task => task.Project)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == request.SessionId && candidate.TaskId == request.TaskId,
                cancellationToken);

        if (session is null)
        {
            return SessionResultCaptureOutcome.NotFound;
        }

        if (session.Status != AgentSessionStatus.Started)
        {
            return SessionResultCaptureOutcome.NotStarted;
        }


        var capturedUtc = DateTime.UtcNow;

        if (request.RequireClaimOwnership &&
            (session.ClaimId != request.ClaimId
                || (session.ClaimId is not null && session.ClaimExpiresUtc <= capturedUtc)))
        {
            return SessionResultCaptureOutcome.ClaimLost;
        }

        var artifactKind = session.Role switch
        {
            AgentSessionRole.Planner => ContextEntryKind.Plan,
            AgentSessionRole.Implementer => ContextEntryKind.Implementation,
            AgentSessionRole.Reviewer => ContextEntryKind.Review,
            _ => throw new InvalidOperationException($"Unmapped agent session role '{session.Role}'.")
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var transition = dbContext.AgentSessions.Where(candidate =>
            candidate.Id == request.SessionId
            && candidate.TaskId == request.TaskId
            && candidate.Status == AgentSessionStatus.Started);

        if (request.RequireClaimOwnership)
        {
            transition = transition.Where(candidate =>
                candidate.ClaimId == request.ClaimId
                && (candidate.ClaimId == null || candidate.ClaimExpiresUtc > capturedUtc));
        }

        var transitioned = await transition.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(candidate => candidate.Status, AgentSessionStatus.Completed)
                .SetProperty(candidate => candidate.CompletedUtc, capturedUtc),
            cancellationToken);

        if (transitioned != 1)
        {
            return request.RequireClaimOwnership
                ? SessionResultCaptureOutcome.ClaimLost
                : SessionResultCaptureOutcome.NotStarted;
        }

        // ExecuteUpdate bypasses tracking. Mirror the committed-in-this-transaction transition in
        // memory, then mark it unchanged so SaveChanges only writes the entries/task/project.
        session.Status = AgentSessionStatus.Completed;
        session.CompletedUtc = capturedUtc;
        dbContext.Entry(session).Property(candidate => candidate.Status).OriginalValue = AgentSessionStatus.Completed;
        dbContext.Entry(session).Property(candidate => candidate.CompletedUtc).OriginalValue = capturedUtc;

        dbContext.ContextEntries.AddRange(
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.Prompt,
                Title = $"{session.Role} session prompt",
                Content = request.Prompt!.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.RawOutput,
                Title = $"{session.Role} raw output",
                Content = request.RawOutput!.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.Summary,
                Title = $"{session.Role} summary",
                Content = request.Summary!.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = artifactKind,
                Title = request.ArtifactTitle!.Trim(),
                Content = request.ArtifactContent!.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            });

        session.Task.UpdatedUtc = capturedUtc;
        session.Task.Project.IncrementContextRevision();

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return request.RequireClaimOwnership
                ? SessionResultCaptureOutcome.ClaimLost
                : SessionResultCaptureOutcome.NotStarted;
        }

        return SessionResultCaptureOutcome.Success(session.Role);
    }

    private static Dictionary<string, string> Validate(SessionResultCaptureRequest request)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        ValidateField(errors, nameof(request.Prompt), request.Prompt, LongFieldMaxLength);
        ValidateField(errors, nameof(request.RawOutput), request.RawOutput, LongFieldMaxLength);
        ValidateField(errors, nameof(request.Summary), request.Summary, SummaryMaxLength);
        ValidateField(errors, nameof(request.ArtifactTitle), request.ArtifactTitle, ArtifactTitleMaxLength);
        ValidateField(errors, nameof(request.ArtifactContent), request.ArtifactContent, LongFieldMaxLength);
        return errors;
    }

    private static void ValidateField(Dictionary<string, string> errors, string name, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[name] = $"{name} is required.";
            return;
        }

        if (value.Length > maxLength)
        {
            errors[name] = $"{name} must be {maxLength} characters or fewer.";
        }
    }
}
