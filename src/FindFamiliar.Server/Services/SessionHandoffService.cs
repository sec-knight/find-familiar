using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public interface ISessionHandoffService
{
    /// <summary>
    /// Stages the handoff a terminal session proposes, superseding any handoff already pending on the
    /// task. The caller owns <c>SaveChangesAsync</c> and the surrounding transaction.
    /// </summary>
    Task StageHandoffAsync(
        AgentSession terminalSession,
        int observedContextRevision,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Derives the proposed next step when a session reaches a terminal state (ADR-0010).
///
/// The mapping reads <see cref="AgentSession.Role"/> and <see cref="AgentSession.Status"/> and nothing
/// else. It never reads a summary, a raw output, or any other model-authored text: ADR-0005 rejected
/// verdict parsing because it would let a worker's own output advance work without human sign-off, and
/// that reasoning is unchanged.
///
/// Staging runs inside the result-capture and cancellation transactions, which buys a
/// database-guaranteed invariant — a terminal session that proposes a next step always has exactly one
/// handoff row, with no window where the queue shows a finished session and nothing to approve.
///
/// The cost of that placement is that a fault here would fail result capture, the most safety-critical
/// path in the system. Two properties keep that safe and must be preserved:
///
/// 1. <b>It is total.</b> Every role and terminal-status combination either stages exactly one handoff
///    or stages nothing. Nothing throws, including for a role this method does not recognise.
/// 2. <b>It issues one query.</b> Only the supersede update touches the database; the rest is staging.
/// </summary>
public sealed class SessionHandoffService(FamiliarDbContext dbContext) : ISessionHandoffService
{
    public async Task StageHandoffAsync(
        AgentSession terminalSession,
        int observedContextRevision,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminalSession);

        var proposal = Propose(terminalSession.Role, terminalSession.Status);
        if (proposal is null)
        {
            return;
        }

        // At most one handoff per task is actionable. Superseding first is what lets the filtered
        // unique index hold, and it means contenders can only ever race for one row.
        await dbContext.SessionHandoffs
            .Where(candidate =>
                candidate.TaskId == terminalSession.TaskId
                && candidate.Status == SessionHandoffStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, SessionHandoffStatus.Superseded)
                    .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                cancellationToken);

        dbContext.SessionHandoffs.Add(new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = terminalSession.TaskId,
            SourceSessionId = terminalSession.Id,
            SourceOutcome = terminalSession.Status,
            ProposedRole = proposal.Value.Role,
            Kind = proposal.Value.Kind,
            Status = SessionHandoffStatus.Pending,
            ObservedContextRevision = observedContextRevision,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        });
    }

    /// <summary>
    /// The whole role-progression rule. Returns null when nothing should be proposed.
    ///
    /// A completed Reviewer proposes nothing: the chain ends at a human decision about the task, which
    /// ADR-0003 and ADR-0005 both keep out of the software's hands.
    /// </summary>
    internal static (AgentSessionRole Role, SessionHandoffKind Kind)? Propose(
        AgentSessionRole role,
        AgentSessionStatus terminalStatus) =>
        terminalStatus switch
        {
            AgentSessionStatus.Completed => role switch
            {
                AgentSessionRole.Planner => (AgentSessionRole.Implementer, SessionHandoffKind.NextRole),
                AgentSessionRole.Implementer => (AgentSessionRole.Reviewer, SessionHandoffKind.NextRole),
                AgentSessionRole.Reviewer => null,
                _ => null
            },

            // A cancelled attempt proposes the same role again. WorkQueueService already derives this
            // advisorily; recording it makes the suggestion actionable and auditable without adding
            // any new role semantics.
            AgentSessionStatus.Cancelled => (role, SessionHandoffKind.RetrySameRole),

            _ => null
        };
}
