using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Gateway;

public sealed record FamiliarSessionHandoffPlanSource(
    Guid HandoffId,
    Guid TaskId,
    Guid ProjectId,
    Guid SourceSessionId,
    AgentSessionRole SourceRole,
    AgentSessionStatus SourceOutcome,
    AgentSessionRole ProposedRole,
    SessionHandoffKind Kind,
    SessionHandoffStatus Status);

/// <summary>
/// The complete artifact stored behind a bounded context entry, as the plan path reads it.
/// <paramref name="OriginalLength"/> is the length before any retention bound, so a caller can tell a
/// whole artifact from a retained prefix of a longer one.
/// </summary>
public sealed record FamiliarCompleteArtifact(string Content, int OriginalLength);

public interface IFamiliarSessionHandoffPlanReader
{
    Task<FamiliarSessionHandoffPlanSource?> ReadAsync(
        Guid handoffId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The complete artifact behind one context entry, or null where none was retained.
    ///
    /// Takes the entry id the caller already resolved through the sensitivity-filtered projection
    /// rather than resolving its own: a document has no visibility rules of its own, and a lookup that
    /// started here could reach the artifact of an entry the caller was never entitled to see.
    /// </summary>
    Task<FamiliarCompleteArtifact?> ReadCompleteArtifactAsync(
        Guid contextEntryId,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only handoff metadata lookup; artifact content remains in ContextProjectionService.</summary>
public sealed class FamiliarSessionHandoffPlanReader(FamiliarDbContext dbContext) : IFamiliarSessionHandoffPlanReader
{
    public Task<FamiliarCompleteArtifact?> ReadCompleteArtifactAsync(
        Guid contextEntryId,
        CancellationToken cancellationToken = default) =>
        dbContext.ContextEntryArtifacts
            .AsNoTracking()
            .Where(document => document.ContextEntryId == contextEntryId)
            .Select(document => new FamiliarCompleteArtifact(document.Content, document.OriginalLength))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<FamiliarSessionHandoffPlanSource?> ReadAsync(
        Guid handoffId,
        CancellationToken cancellationToken = default) =>
        dbContext.SessionHandoffs
            .AsNoTracking()
            .Where(handoff => handoff.Id == handoffId)
            .Select(handoff => new FamiliarSessionHandoffPlanSource(
                handoff.Id,
                handoff.TaskId,
                handoff.Task.ProjectId,
                handoff.SourceSessionId,
                handoff.SourceSession.Role,
                handoff.SourceOutcome,
                handoff.ProposedRole,
                handoff.Kind,
                handoff.Status))
            .SingleOrDefaultAsync(cancellationToken);
}
