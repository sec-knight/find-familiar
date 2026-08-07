using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>
/// Drafts a plan from a turn that asked for one, and persists it as a proposal. Creates no work.
/// </summary>
public interface IFamiliarPlanDraftingService
{
    Task DraftAsync(
        FamiliarPlanDraftRequest request,
        CancellationToken cancellationToken = default);
}

/// <param name="RecordedContext">
/// The same retrieval block the conversational reply was given, so the plan is drawn from the same
/// evidence the person just read rather than from a second, different search.
/// </param>
/// <param name="Intent">
/// Whether the person asked for this plan or the pass is deciding for itself whether there is one.
/// Only the prompt differs; everything this service does with the result is the same either way, and
/// deliberately so — an uninvited plan is held to exactly the rules an invited one is.
/// </param>
public sealed record FamiliarPlanDraftRequest(
    Guid ChatId,
    Guid TurnId,
    Guid? FocusProjectId,
    string UserText,
    string ConversationalReply,
    string? StandingBrief,
    string? RecordedContext,
    IReadOnlyCollection<Guid> OfferedEvidence,
    FamiliarPlanDraftIntent Intent = FamiliarPlanDraftIntent.Requested);

/// <summary>
/// The second pass: turn what was just said into something a person can approve.
///
/// <b>This writes a proposal and nothing else.</b> No task, no session, no context entry, no revision
/// change. The talk lane still holds no reference to <c>IWorkflowDispatchService</c>, and the row this
/// creates is a record of what a human will be shown — ADR-0014's rule, unchanged.
///
/// Every failure here is silent by design. Drafting runs after the conversational reply is already
/// durable and already on the person's screen; a provider that times out while drafting must not
/// retract or overwrite an answer that was good. The person asked for a plan and did not get one,
/// which the page states plainly, and their reply is untouched.
/// </summary>
public sealed class FamiliarPlanDraftingService(
    FamiliarDbContext dbContext,
    IFamiliarChatProvider provider,
    TimeProvider timeProvider,
    ILogger<FamiliarPlanDraftingService> logger) : IFamiliarPlanDraftingService
{
    public async Task DraftAsync(
        FamiliarPlanDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveProjectAsync(request.FocusProjectId, cancellationToken) is not { } project)
        {
            // A plan has to belong to a project. With none focused and none inferable, there is
            // nowhere for the work to go, and choosing one from model text is exactly the decision
            // this design does not let a model make.
            return;
        }

        var draft = await DraftFromProviderAsync(request, cancellationToken);

        if (draft is null || draft.Items.Count == 0)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var plan = new FamiliarPlanProposal
        {
            Id = Guid.NewGuid(),
            ChatId = request.ChatId,
            TurnId = request.TurnId,
            ProjectId = project.Id,
            Status = FamiliarPlanStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = project.ContextRevision,
            Summary = draft.Summary,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            Items = draft.Items
                .Select((item, position) => new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = position,
                    Title = item.Title,
                    RequestedOutcome = item.RequestedOutcome,
                    Role = item.Role,
                    EvidenceEntryIds = FamiliarChatCitations.SerialiseEvidence(item.EvidenceEntryIds),
                    IsIncluded = true
                })
                .ToList()
        };

        dbContext.FamiliarPlanProposals.Add(plan);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Almost certainly IX_FamiliarPlanProposals_ChatId_Pending: a plan is already awaiting a
            // decision in this conversation. That is the invariant working, not a fault — the person
            // is asked to decide the one they have before a second is drafted.
            dbContext.ChangeTracker.Clear();
            logger.LogInformation(
                exception,
                "A plan was not drafted for chat {ChatId}; one is already awaiting a decision.",
                request.ChatId);
        }
    }

    /// <summary>
    /// Asks the provider for the structured plan, accumulating the stream into one string.
    ///
    /// The stream is collected rather than surfaced: nobody watches JSON arrive. Streaming is still
    /// how it is fetched because the provider has one method and a second, non-streaming path would be
    /// a second wire implementation to keep correct.
    /// </summary>
    private async Task<DraftedPlan?> DraftFromProviderAsync(
        FamiliarPlanDraftRequest request,
        CancellationToken cancellationToken)
    {
        // The conversation's own reply travels as the last exchange, so the plan follows from what the
        // person just read rather than from a second opinion they never saw.
        var prompt = new FamiliarChatRequest(
            FamiliarPlanDraftPrompt.For(request.Intent),
            [new FamiliarChatHistoryTurn(request.UserText, request.ConversationalReply)],
            "Draft the plan as JSON now.",
            request.StandingBrief,
            request.RecordedContext);

        var output = new System.Text.StringBuilder();
        FamiliarChatStreamEvent.Finished? finished = null;

        try
        {
            await foreach (var streamEvent in provider.StreamAsync(prompt, cancellationToken))
            {
                switch (streamEvent)
                {
                    case FamiliarChatStreamEvent.Delta delta:
                        output.Append(delta.Text);
                        break;

                    case FamiliarChatStreamEvent.Finished terminal:
                        finished = terminal;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (finished?.Status != FamiliarChatProviderStatus.Completed)
        {
            // No plan, and no note recorded against the turn: the conversational reply stands on its
            // own and must not be edited to explain a second call the person never saw.
            return null;
        }

        return FamiliarPlanDraftReader.Read(output.ToString(), request.OfferedEvidence);
    }

    /// <summary>
    /// The project a plan belongs to: the conversation's focus, or the only project there is.
    ///
    /// The second case is a convenience for the ordinary situation and not a guess — with exactly one
    /// non-sensitive project, "which project?" has one answer. With several and no focus, the plan is
    /// not drafted, because picking would be inventing intent.
    /// </summary>
    private async Task<FamiliarProject?> ResolveProjectAsync(
        Guid? focusProjectId,
        CancellationToken cancellationToken)
    {
        if (focusProjectId is { } focus)
        {
            return await dbContext.Projects
                .AsNoTracking()
                .SingleOrDefaultAsync(project => project.Id == focus && !project.IsSensitive, cancellationToken);
        }

        var candidates = await dbContext.Projects
            .AsNoTracking()
            .Where(project => !project.IsSensitive && project.Status == ProjectStatus.Active)
            .Take(2)
            .ToListAsync(cancellationToken);

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
