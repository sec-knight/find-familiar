using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// The read side of a project's conversation.
///
/// Every query filters on <c>projectId</c>, as <c>DemiplaneProjectionService</c> does: a conversation
/// is reached through its project and never through an id supplied by a caller, so no id from another
/// project can pull that project's transcript onto this page. Pending proposals are filtered on their
/// denormalised <see cref="FamiliarActionProposal.ProjectId"/> as well as their conversation, so
/// ownership holds even if the two ever disagreed.
///
/// Reads are <c>AsNoTracking</c> and ordered by <see cref="FamiliarMessage.Sequence"/>, never by
/// timestamp: two messages written in the same tick must still have one correct order, and the
/// sequence is the column that guarantees it.
/// </summary>
public sealed class FamiliarConversationService(FamiliarDbContext dbContext) : IFamiliarConversationService
{
    public async Task<FamiliarConversationView?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.FamiliarConversations
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId)
            .Select(candidate => new { candidate.Id, candidate.ProjectId })
            .SingleOrDefaultAsync(cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        var messages = await dbContext.FamiliarMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.Sequence)
            .Select(message => new FamiliarMessageView(
                message.Id,
                message.Author,
                message.Sequence,
                message.Content,
                message.CreatedUtc,
                message.ProviderName,
                message.ProviderModel,
                message.Delivery,
                message.FailureCode,
                message.Evidence
                    .OrderBy(evidence => evidence.Label)
                    .Select(evidence => new FamiliarEvidenceView(
                        evidence.Kind,
                        evidence.ReferenceId,
                        evidence.Label))
                    .ToList()))
            .ToListAsync(cancellationToken);

        var proposals = await dbContext.FamiliarActionProposals
            .AsNoTracking()
            .Where(proposal =>
                proposal.ConversationId == conversation.Id
                && proposal.ProjectId == projectId
                && proposal.Status == FamiliarActionStatus.Pending)
            .OrderBy(proposal => proposal.CreatedUtc)
            .Select(proposal => new FamiliarProposalView(
                proposal.Id,
                proposal.MessageId,
                proposal.Kind,
                proposal.ConcurrencyToken,
                proposal.ObservedContextRevision,
                proposal.Title,
                proposal.RequestedOutcome,
                proposal.TargetTaskId,
                // Resolved server-side from the task row, so the target's name on the page is
                // persisted state rather than anything a provider wrote about it.
                proposal.TargetTask == null ? null : proposal.TargetTask.Title,
                proposal.CreatedUtc))
            .ToListAsync(cancellationToken);

        return new FamiliarConversationView(conversation.Id, conversation.ProjectId, messages, proposals);
    }
}
