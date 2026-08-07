using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>
/// One decision a person owes the system, with everything needed to make it in place.
/// </summary>
/// <param name="ConcurrencyToken">
/// The token this rendering carried. It travels to the client and back, so a handoff decided or
/// superseded between the render and the click is refused rather than applied to something the person
/// never read.
/// </param>
/// <param name="LastResultTitle">
/// The context entry the finished session produced, when it produced one. This is what makes the
/// decision answerable in place: "approve the Implementer" is not a question anybody can answer
/// without seeing what the Planner actually wrote.
/// </param>
public sealed record FamiliarOpenDecision(
    Guid HandoffId,
    Guid ConcurrencyToken,
    Guid TaskId,
    string TaskTitle,
    Guid ProjectId,
    string ProjectName,
    AgentSessionRole SourceRole,
    AgentSessionStatus SourceOutcome,
    AgentSessionRole ProposedRole,
    SessionHandoffKind Kind,
    DateTime CreatedUtc,
    Guid? LastResultEntryId = null,
    string? LastResultTitle = null)
{
    /// <summary>What approving would do, in the words the card shows before the button.</summary>
    public string Consequence =>
        Kind == SessionHandoffKind.RetrySameRole
            ? $"Approving starts another {ProposedRole} session on “{TaskTitle}”. The last one did not complete."
            : $"Approving starts one {ProposedRole} session on “{TaskTitle}”. Nothing else starts.";
}

public interface IFamiliarOpenDecisionsService
{
    Task<IReadOnlyList<FamiliarOpenDecision>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything waiting on a human, across every project the Familiar may be told about.
///
/// This is the half of the loop that was missing. Sessions have produced results since Sprint 9 and
/// handoffs have been human-gated since ADR-0010, but the only place either surfaced was a task page —
/// so a conversation could start work and then go silent about it, which is not a loop, it is a
/// launcher. A decision nobody is told about is indistinguishable from a system that stopped.
///
/// Read-only on every path. This service reports what is waiting; deciding goes through
/// <c>ISessionHandoffApprovalService</c>, which is the same transaction the task pages use.
///
/// <b>Sensitive projects are excluded at the query</b>, on both the project and the entry read, so
/// there is no moment at which a flagged row is held in memory beside something being rendered into a
/// conversation. A withheld decision is simply absent: the count is not disclosed here because the
/// standing brief already states how many projects are withheld, and repeating it beside a decision
/// list would say which project a hidden decision belongs to.
/// </summary>
public sealed class FamiliarOpenDecisionsService(FamiliarDbContext dbContext) : IFamiliarOpenDecisionsService
{
    /// <summary>
    /// A bound on what one conversation shows at once. Not a technical limit — a list nobody can hold
    /// in their head is one nobody reads, and the oldest decisions are the ones most likely to be
    /// blocking something.
    /// </summary>
    public const int MaxDecisions = 10;

    public async Task<IReadOnlyList<FamiliarOpenDecision>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await dbContext.SessionHandoffs
            .AsNoTracking()
            .Where(handoff =>
                handoff.Status == SessionHandoffStatus.Pending
                && !handoff.Task.Project.IsSensitive
                && handoff.Task.Project.Status == ProjectStatus.Active)
            // Oldest first: a decision that has been waiting longest is the one most likely to be
            // holding something up, and it is the one a person is most likely to have forgotten.
            .OrderBy(handoff => handoff.CreatedUtc)
            .Take(MaxDecisions)
            .Select(handoff => new
            {
                handoff.Id,
                handoff.ConcurrencyToken,
                handoff.TaskId,
                TaskTitle = handoff.Task.Title,
                handoff.Task.ProjectId,
                ProjectName = handoff.Task.Project.Name,
                SourceRole = handoff.SourceSession.Role,
                handoff.SourceOutcome,
                handoff.ProposedRole,
                handoff.Kind,
                handoff.CreatedUtc,
                handoff.SourceSessionId
            })
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return [];
        }

        // What the finished session actually produced, so the decision can be made from the result
        // rather than from the fact that something ended. Sensitivity is re-applied here: an entry
        // flagged after it was written is not shown, and the decision simply carries no result.
        var sourceSessionIds = pending.Select(handoff => handoff.SourceSessionId).ToList();

        var results = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry =>
                entry.SourceSessionId != null
                && sourceSessionIds.Contains(entry.SourceSessionId.Value)
                && entry.State == ContextEntryState.Active
                && !entry.IsSensitive
                && !entry.Project.IsSensitive)
            .OrderByDescending(entry => entry.CreatedUtc)
            .Select(entry => new { entry.Id, entry.SourceSessionId, entry.Title })
            .ToListAsync(cancellationToken);

        var latestBySession = results
            .GroupBy(entry => entry.SourceSessionId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        return pending
            .Select(handoff =>
            {
                latestBySession.TryGetValue(handoff.SourceSessionId, out var result);

                return new FamiliarOpenDecision(
                    handoff.Id,
                    handoff.ConcurrencyToken,
                    handoff.TaskId,
                    handoff.TaskTitle,
                    handoff.ProjectId,
                    handoff.ProjectName,
                    handoff.SourceRole,
                    handoff.SourceOutcome,
                    handoff.ProposedRole,
                    handoff.Kind,
                    handoff.CreatedUtc,
                    result?.Id,
                    result?.Title);
            })
            .ToList();
    }
}
