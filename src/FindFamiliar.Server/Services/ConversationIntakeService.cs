using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum ConversationIntakeStatus
{
    Success,
    ValidationFailed
}

public sealed record ConversationIntakeRequest(string? Request);

public sealed record ConversationIntakeOutcome(
    ConversationIntakeStatus Status,
    Guid ConversationId = default,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static ConversationIntakeOutcome Success(Guid conversationId) =>
        new(ConversationIntakeStatus.Success, conversationId);

    public static ConversationIntakeOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(ConversationIntakeStatus.ValidationFailed, ValidationErrors: errors);
}

public interface IConversationIntakeService
{
    Task<ConversationIntakeOutcome> CreateAsync(
        ConversationIntakeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns one natural-language request into a conversation, a human message, a deterministic
/// proposal and a templated Familiar reply — and nothing else.
///
/// This is the pre-approval safety boundary in code: the only tables written are Conversations,
/// ConversationMessages and WorkProposals. No task, session or context entry is created, no
/// project context revision moves, no worker is notified and no provider is called. The first
/// provider call in the system is the Planner session that approval creates.
/// </summary>
public sealed class ConversationIntakeService(FamiliarDbContext dbContext, TimeProvider timeProvider)
    : IConversationIntakeService
{
    public const string RequestField = "Request";

    public async Task<ConversationIntakeOutcome> CreateAsync(
        ConversationIntakeRequest request,
        CancellationToken cancellationToken = default)
    {
        var trimmed = request.Request?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ConversationIntakeOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RequestField] = "Describe the work you want done."
            });
        }

        if (trimmed.Length > DeterministicProposalGenerator.MaxRequestLength)
        {
            return ConversationIntakeOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RequestField] =
                    $"Keep the request to {DeterministicProposalGenerator.MaxRequestLength:N0} characters or fewer."
            });
        }

        var candidates = await LoadActiveProjectCandidatesAsync(dbContext, cancellationToken);
        var resolution = DeterministicProposalGenerator.ResolveProject(
            trimmed,
            candidates.Select(candidate => new ProposalProjectCandidate(candidate.Id, candidate.Name)).ToList());

        var selected = resolution.ProjectId is { } projectId
            ? candidates.Single(candidate => candidate.Id == projectId)
            : null;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var title = DeterministicProposalGenerator.BuildTitle(trimmed);
        var requestedOutcome = DeterministicProposalGenerator.BuildRequestedOutcome(trimmed);

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Status = ConversationStatus.AwaitingApproval,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        var proposal = new WorkProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            ProjectId = selected?.Id,
            Title = title,
            RequestedOutcome = requestedOutcome,
            Role = AgentSessionRole.Planner,
            ObservedContextRevision = selected?.ContextRevision,
            Status = WorkProposalStatus.Pending,
            Revision = 1,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        dbContext.Conversations.Add(conversation);
        dbContext.WorkProposals.Add(proposal);
        dbContext.ConversationMessages.AddRange(
            new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Author = ConversationMessageAuthor.Human,
                Sequence = 1,
                Content = trimmed,
                CreatedUtc = nowUtc
            },
            new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Author = ConversationMessageAuthor.Familiar,
                Sequence = 2,
                Content = ProposalMessageComposer.InitialResponse(selected?.Name, title, requestedOutcome),
                CreatedUtc = nowUtc
            });

        await dbContext.SaveChangesAsync(cancellationToken);

        return ConversationIntakeOutcome.Success(conversation.Id);
    }

    /// <summary>
    /// The bounded, deterministically ordered candidate set. Archived projects are excluded here
    /// rather than filtered later, so an inactive project can never reach the generator at all.
    /// </summary>
    public static Task<List<ActiveProjectCandidate>> LoadActiveProjectCandidatesAsync(
        FamiliarDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Status == ProjectStatus.Active)
            .OrderBy(project => project.Name)
            .ThenBy(project => project.Id)
            .Take(DeterministicProposalGenerator.MaxCandidateProjects)
            .Select(project => new ActiveProjectCandidate(project.Id, project.Name, project.ContextRevision))
            .ToListAsync(cancellationToken);
}

public sealed record ActiveProjectCandidate(Guid Id, string Name, int ContextRevision);
