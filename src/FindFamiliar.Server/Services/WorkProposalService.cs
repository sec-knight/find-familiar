using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace FindFamiliar.Server.Services;

public enum ProposalActionStatus
{
    Success,

    ValidationFailed,

    NotFound,

    /// <summary>The presented concurrency token is not the current one; newer data was not overwritten.</summary>
    StaleProposal,

    /// <summary>The conversation was already approved or rejected. Terminal states never reopen.</summary>
    AlreadyTerminal,

    /// <summary>A concurrent writer won. Nothing was changed by this caller.</summary>
    Conflict
}

public sealed record ProposalRevisionRequest(
    Guid ConversationId,
    Guid ExpectedConcurrencyToken,
    Guid? ProjectId,
    string? Title,
    string? RequestedOutcome);

public sealed record ProposalActionRequest(Guid ConversationId, Guid ExpectedConcurrencyToken);

public sealed record ProposalActionOutcome(
    ProposalActionStatus Status,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly ProposalActionOutcome Success = new(ProposalActionStatus.Success);
    public static readonly ProposalActionOutcome NotFound = new(ProposalActionStatus.NotFound);
    public static readonly ProposalActionOutcome StaleProposal = new(ProposalActionStatus.StaleProposal);
    public static readonly ProposalActionOutcome AlreadyTerminal = new(ProposalActionStatus.AlreadyTerminal);
    public static readonly ProposalActionOutcome Conflict = new(ProposalActionStatus.Conflict);

    public static ProposalActionOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(ProposalActionStatus.ValidationFailed, errors);
}

public interface IWorkProposalService
{
    Task<ProposalActionOutcome> ReviseAsync(
        ProposalRevisionRequest request,
        CancellationToken cancellationToken = default);

    Task<ProposalActionOutcome> RefreshContextAsync(
        ProposalActionRequest request,
        CancellationToken cancellationToken = default);

    Task<ProposalActionOutcome> RejectAsync(
        ProposalActionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The non-dispatching proposal transitions: revise, refresh observed context, and reject.
///
/// Every one of them is fenced the same way as approval — the caller presents the concurrency
/// token it reviewed, and the transition is a conditional UPDATE that only matches while the
/// proposal is still Pending and still carries that token. A stale form therefore fails loudly
/// instead of overwriting a newer revision, and rejection cannot revive an approved proposal.
///
/// None of these paths may create a task, a session or a context entry, or move a project's
/// context revision.
/// </summary>
public sealed class WorkProposalService(FamiliarDbContext dbContext, TimeProvider timeProvider) : IWorkProposalService
{
    public const string ProjectField = "ProjectId";
    public const string TitleField = "Title";
    public const string RequestedOutcomeField = "RequestedOutcome";

    public async Task<ProposalActionOutcome> ReviseAsync(
        ProposalRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        var title = request.Title?.Trim() ?? string.Empty;
        var requestedOutcome = request.RequestedOutcome?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title))
        {
            errors[TitleField] = "A task title is required.";
        }
        else if (title.Length > WorkProposal.MaxTitleLength)
        {
            errors[TitleField] = $"The task title must be {WorkProposal.MaxTitleLength} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(requestedOutcome))
        {
            errors[RequestedOutcomeField] = "A requested outcome is required.";
        }
        else if (requestedOutcome.Length > WorkProposal.MaxRequestedOutcomeLength)
        {
            errors[RequestedOutcomeField] =
                $"The requested outcome must be {WorkProposal.MaxRequestedOutcomeLength:N0} characters or fewer.";
        }

        if (request.ProjectId is null || request.ProjectId == Guid.Empty)
        {
            errors[ProjectField] = "Select the project this work belongs to.";
        }

        if (errors.Count > 0)
        {
            return ProposalActionOutcome.ValidationFailed(errors);
        }

        var state = await LoadPendingAsync(request.ConversationId, request.ExpectedConcurrencyToken, cancellationToken);
        if (state.Rejection is { } rejection)
        {
            return rejection;
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.ProjectId!.Value)
            .Select(candidate => new { candidate.Name, candidate.Status, candidate.ContextRevision })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null || project.Status != ProjectStatus.Active)
        {
            return ProposalActionOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectField] = "Select an active project."
            });
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var newRevision = state.Proposal!.Revision + 1;

        return await TransitionAsync(
            state,
            request.ExpectedConcurrencyToken,
            nowUtc,
            setters => setters
                .SetProperty(proposal => proposal.ProjectId, request.ProjectId)
                .SetProperty(proposal => proposal.Title, title)
                .SetProperty(proposal => proposal.RequestedOutcome, requestedOutcome)
                .SetProperty(proposal => proposal.ObservedContextRevision, project.ContextRevision)
                .SetProperty(proposal => proposal.Revision, newRevision),
            ConversationStatus.AwaitingApproval,
            ProposalMessageComposer.RevisionSummary(newRevision, project.Name, title, requestedOutcome),
            cancellationToken);
    }

    public async Task<ProposalActionOutcome> RefreshContextAsync(
        ProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadPendingAsync(request.ConversationId, request.ExpectedConcurrencyToken, cancellationToken);
        if (state.Rejection is { } rejection)
        {
            return rejection;
        }

        var proposal = state.Proposal!;

        if (proposal.ProjectId is not { } projectId)
        {
            return ProposalActionOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectField] = "Select a project before refreshing its context."
            });
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == projectId)
            .Select(candidate => new { candidate.Name, candidate.Status, candidate.ContextRevision })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null || project.Status != ProjectStatus.Active)
        {
            return ProposalActionOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectField] = "Select an active project."
            });
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var newRevision = proposal.Revision + 1;

        // Rotating the token is what forces renewed human review: every form rendered before this
        // refresh now carries a stale token and can no longer approve.
        return await TransitionAsync(
            state,
            request.ExpectedConcurrencyToken,
            nowUtc,
            setters => setters
                .SetProperty(candidate => candidate.ObservedContextRevision, project.ContextRevision)
                .SetProperty(candidate => candidate.Revision, newRevision),
            ConversationStatus.AwaitingApproval,
            ProposalMessageComposer.ContextRefreshed(
                project.Name,
                proposal.ObservedContextRevision ?? project.ContextRevision,
                project.ContextRevision),
            cancellationToken);
    }

    public async Task<ProposalActionOutcome> RejectAsync(
        ProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadPendingAsync(request.ConversationId, request.ExpectedConcurrencyToken, cancellationToken);
        if (state.Rejection is { } rejection)
        {
            return rejection;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        return await TransitionAsync(
            state,
            request.ExpectedConcurrencyToken,
            nowUtc,
            setters => setters.SetProperty(proposal => proposal.Status, WorkProposalStatus.Rejected),
            ConversationStatus.Rejected,
            ProposalMessageComposer.Rejected(),
            cancellationToken);
    }

    /// <summary>
    /// Applies one fenced transition: a conditional UPDATE that must match a still-Pending proposal
    /// carrying the presented token, plus the conversation update and the appended message, all in
    /// one transaction. Losing the conditional UPDATE changes nothing and is reported, never masked.
    /// </summary>
    private async Task<ProposalActionOutcome> TransitionAsync(
        ProposalState state,
        Guid expectedToken,
        DateTime nowUtc,
        Action<UpdateSettersBuilder<WorkProposal>> applyTransitionSetters,
        ConversationStatus conversationStatus,
        string message,
        CancellationToken cancellationToken)
    {
        var proposalId = state.Proposal!.Id;
        var conversationId = state.Conversation!.Id;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var transitioned = await dbContext.WorkProposals
                .Where(proposal =>
                    proposal.Id == proposalId
                    && proposal.Status == WorkProposalStatus.Pending
                    && proposal.ConcurrencyToken == expectedToken)
                .ExecuteUpdateAsync(
                    calls =>
                    {
                        applyTransitionSetters(calls);
                        calls
                            .SetProperty(proposal => proposal.ConcurrencyToken, Guid.NewGuid())
                            .SetProperty(proposal => proposal.UpdatedUtc, nowUtc);
                    },
                    cancellationToken);

            if (transitioned != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ProposalActionOutcome.StaleProposal;
            }

            await dbContext.Conversations
                .Where(conversation => conversation.Id == conversationId)
                .ExecuteUpdateAsync(
                    calls => calls
                        .SetProperty(conversation => conversation.Status, conversationStatus)
                        .SetProperty(conversation => conversation.UpdatedUtc, nowUtc),
                    cancellationToken);

            dbContext.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Author = ConversationMessageAuthor.Familiar,
                Sequence = await NextSequenceAsync(dbContext, conversationId, cancellationToken),
                Content = message,
                CreatedUtc = nowUtc
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProposalActionOutcome.Success;
        }
        catch (DbUpdateException)
        {
            // A concurrent append took the sequence number, or a concurrent transition changed the
            // row underneath. Nothing this caller intended was committed.
            await transaction.RollbackAsync(CancellationToken.None);
            return ProposalActionOutcome.Conflict;
        }
    }

    private async Task<ProposalState> LoadPendingAsync(
        Guid conversationId,
        Guid expectedToken,
        CancellationToken cancellationToken)
    {
        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return ProposalState.Rejected(ProposalActionOutcome.NotFound);
        }

        var proposal = await dbContext.WorkProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ConversationId == conversationId, cancellationToken);

        if (proposal is null)
        {
            return ProposalState.Rejected(ProposalActionOutcome.NotFound);
        }

        if (conversation.Status != ConversationStatus.AwaitingApproval
            || proposal.Status != WorkProposalStatus.Pending)
        {
            return ProposalState.Rejected(ProposalActionOutcome.AlreadyTerminal);
        }

        if (proposal.ConcurrencyToken != expectedToken)
        {
            return ProposalState.Rejected(ProposalActionOutcome.StaleProposal);
        }

        return new ProposalState(conversation, proposal, null);
    }

    /// <summary>
    /// Next stable display position. The unique (ConversationId, Sequence) index is what actually
    /// enforces the ordering — this only proposes the value, and a losing racer is reported as a
    /// conflict rather than silently reordering history.
    /// </summary>
    public static async Task<int> NextSequenceAsync(
        FamiliarDbContext dbContext,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var highest = await dbContext.ConversationMessages
            .Where(message => message.ConversationId == conversationId)
            .MaxAsync(message => (int?)message.Sequence, cancellationToken);

        return (highest ?? 0) + 1;
    }

    private sealed record ProposalState(
        Conversation? Conversation,
        WorkProposal? Proposal,
        ProposalActionOutcome? Rejection)
    {
        public static ProposalState Rejected(ProposalActionOutcome outcome) => new(null, null, outcome);
    }
}
