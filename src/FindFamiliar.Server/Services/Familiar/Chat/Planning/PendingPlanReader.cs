using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>One item of a drafted plan, as a reader outside the chat page is shown it.</summary>
public sealed record PendingPlanItemView(
    Guid ItemId,
    int Position,
    string Title,
    string RequestedOutcome,
    AgentSessionRole? Role,
    bool IsIncluded);

/// <summary>
/// A plan waiting on a human, with the chat it belongs to.
///
/// <see cref="ChatId"/> is carried because the approval service is addressed by chat — a plan is a
/// thing that happened in a conversation, and the service checks the two agree. A caller that only
/// knows the plan id could not otherwise reach it.
/// </summary>
public sealed record PendingPlanView(
    Guid PlanId,
    Guid ChatId,
    Guid ProjectId,
    Guid ConcurrencyToken,
    string Summary,
    IReadOnlyList<PendingPlanItemView> Items,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public interface IPendingPlanReader
{
    /// <summary>Every plan currently awaiting a human, across all chats. Read-only.</summary>
    Task<IReadOnlyList<PendingPlanView>> ListPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads plans awaiting a decision, so a frontend that is not the chat page can show them.
///
/// <b>Why this is separate from the approval service.</b> That service decides; this one looks. Adding
/// a read to it would have put a query on a type whose whole discipline is that every method consumes
/// a Pending row inside a transaction, and a reader that shares a home with a writer eventually
/// shares a code path with one.
///
/// <b>It applies no sensitivity rule</b>, exactly as the other projections do not. The chat page's
/// reader is the owner; the Familiar gateway's is a vendor-held credential, and only the caller knows
/// which it is. <see cref="PendingPlanView.ProjectId"/> is carried so the caller that must filter can.
/// </summary>
public sealed class PendingPlanReader(FamiliarDbContext dbContext) : IPendingPlanReader
{
    public async Task<IReadOnlyList<PendingPlanView>> ListPendingAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.FamiliarPlanProposals
            .AsNoTracking()
            .Where(plan => plan.Status == FamiliarPlanStatus.Pending)
            .OrderBy(plan => plan.CreatedUtc)
            .Select(plan => new PendingPlanView(
                plan.Id,
                plan.ChatId,
                plan.ProjectId,
                plan.ConcurrencyToken,
                plan.Summary,
                plan.Items
                    .OrderBy(item => item.Position)
                    .Select(item => new PendingPlanItemView(
                        item.Id,
                        item.Position,
                        item.Title,
                        item.RequestedOutcome,
                        item.Role,
                        item.IsIncluded))
                    .ToList(),
                plan.CreatedUtc,
                plan.UpdatedUtc))
            .ToListAsync(cancellationToken);
}
