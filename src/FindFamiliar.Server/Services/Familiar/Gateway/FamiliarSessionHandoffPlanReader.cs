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

public interface IFamiliarSessionHandoffPlanReader
{
    Task<FamiliarSessionHandoffPlanSource?> ReadAsync(
        Guid handoffId,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only handoff metadata lookup; artifact content remains in ContextProjectionService.</summary>
public sealed class FamiliarSessionHandoffPlanReader(FamiliarDbContext dbContext) : IFamiliarSessionHandoffPlanReader
{
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
