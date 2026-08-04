using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public sealed record ConversationMessageView(
    ConversationMessageAuthor Author,
    int Sequence,
    string Content,
    DateTime CreatedUtc);

public sealed record ProposalView(
    Guid Id,
    Guid? ProjectId,
    string? ProjectName,
    bool ProjectIsActive,
    string Title,
    string RequestedOutcome,
    AgentSessionRole Role,
    int? ObservedContextRevision,
    int? CurrentContextRevision,
    WorkProposalStatus Status,
    int Revision,
    Guid ConcurrencyToken,
    Guid? CreatedTaskId,
    Guid? CreatedSessionId)
{
    public bool IsProjectResolved => ProjectId.HasValue;

    /// <summary>
    /// True when the project's context advanced after the user last reviewed the proposal.
    /// Approval is blocked until the user refreshes and reviews again.
    /// </summary>
    public bool IsContextStale =>
        IsProjectResolved && ObservedContextRevision != CurrentContextRevision;
}

/// <summary>
/// The current state of the dispatched session, shown for information only. The conversation
/// displays this; it never derives authority from it.
/// </summary>
public sealed record DispatchedWorkView(
    Guid TaskId,
    string TaskTitle,
    TaskStatus TaskStatus,
    Guid SessionId,
    AgentSessionRole SessionRole,
    AgentSessionStatus SessionStatus,
    int SessionContextRevisionRead,
    bool SessionIsClaimed);

public sealed record ConversationDetailsDocument(
    Guid Id,
    ConversationStatus Status,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<ConversationMessageView> Messages,
    ProposalView Proposal,
    DispatchedWorkView? DispatchedWork,
    IReadOnlyList<ProposalProjectCandidate> SelectableProjects);

public interface IConversationDetailsService
{
    Task<ConversationDetailsDocument?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>Read-only projection for the conversation details page. Performs no mutation.</summary>
public sealed class ConversationDetailsService(FamiliarDbContext dbContext) : IConversationDetailsService
{
    public async Task<ConversationDetailsDocument?> GetAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        var proposal = await dbContext.WorkProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ConversationId == conversationId, cancellationToken);

        if (proposal is null)
        {
            // The unique one-proposal-per-conversation relationship makes this unreachable through
            // the intake path; treating it as not-found keeps the page from rendering half a state.
            return null;
        }

        var messages = await dbContext.ConversationMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Sequence)
            .Select(message => new ConversationMessageView(
                message.Author,
                message.Sequence,
                message.Content,
                message.CreatedUtc))
            .ToListAsync(cancellationToken);

        var project = proposal.ProjectId is { } projectId
            ? await dbContext.Projects
                .AsNoTracking()
                .Where(candidate => candidate.Id == projectId)
                .Select(candidate => new
                {
                    candidate.Name,
                    candidate.Status,
                    candidate.ContextRevision
                })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var proposalView = new ProposalView(
            proposal.Id,
            proposal.ProjectId,
            project?.Name,
            project?.Status == ProjectStatus.Active,
            proposal.Title,
            proposal.RequestedOutcome,
            proposal.Role,
            proposal.ObservedContextRevision,
            project?.ContextRevision,
            proposal.Status,
            proposal.Revision,
            proposal.ConcurrencyToken,
            proposal.CreatedTaskId,
            proposal.CreatedSessionId);

        var dispatched = await LoadDispatchedWorkAsync(conversation, cancellationToken);

        var selectableProjects = await dbContext.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Status == ProjectStatus.Active)
            .OrderBy(candidate => candidate.Name)
            .ThenBy(candidate => candidate.Id)
            .Take(DeterministicProposalGenerator.MaxCandidateProjects)
            .Select(candidate => new ProposalProjectCandidate(candidate.Id, candidate.Name))
            .ToListAsync(cancellationToken);

        return new ConversationDetailsDocument(
            conversation.Id,
            conversation.Status,
            conversation.CreatedUtc,
            conversation.UpdatedUtc,
            messages,
            proposalView,
            dispatched,
            selectableProjects);
    }

    private async Task<DispatchedWorkView?> LoadDispatchedWorkAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        if (conversation.ApprovedTaskId is not { } taskId || conversation.ApprovedSessionId is not { } sessionId)
        {
            return null;
        }

        var task = await dbContext.Tasks
            .AsNoTracking()
            .Where(candidate => candidate.Id == taskId)
            .Select(candidate => new { candidate.Title, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);

        var session = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == sessionId)
            .Select(candidate => new
            {
                candidate.Role,
                candidate.Status,
                candidate.ContextRevisionRead,
                candidate.ClaimedByWorkerId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (task is null || session is null)
        {
            return null;
        }

        return new DispatchedWorkView(
            taskId,
            task.Title,
            task.Status,
            sessionId,
            session.Role,
            session.Status,
            session.ContextRevisionRead,
            session.ClaimedByWorkerId is not null);
    }
}
