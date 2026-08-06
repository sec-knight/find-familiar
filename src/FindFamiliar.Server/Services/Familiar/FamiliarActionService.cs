using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// Turns a human's confirmation into work, under the Sprint 08/09 transaction shape.
///
/// The order is the whole design, and it is <c>WorkApprovalService</c>'s unchanged: take the write
/// lock, conditionally consume the Pending row by token <b>before any effect</b>, re-validate every
/// gate inside the transaction, dispatch through the shared boundary, write the durable link after
/// the rows exist, and commit everything together. The database picks the winner, and the winner's
/// complete effects commit or none of them do.
///
/// Nothing about a proposal is authority. It records what a person was shown; the world may have
/// moved since they read it, so the preflight below is only a fast, friendly failure and the checks
/// that actually gate dispatch are the ones inside the transaction.
///
/// Provider text reaches nothing here. The action kind, the project, the target task and the
/// observed revision are all read from the row; the only provider-derived values a human can carry
/// forward are the CreateTask title and outcome, and those are theirs to edit and are re-validated
/// as their own.
/// </summary>
public sealed class FamiliarActionService(
    FamiliarDbContext dbContext,
    IWorkflowDispatchService workflowDispatch,
    TimeProvider timeProvider) : IFamiliarActionService
{
    public async Task<FamiliarActionOutcome> ConfirmAsync(
        Guid projectId,
        FamiliarActionRequest request,
        CancellationToken cancellationToken = default)
    {
        FamiliarActionProposal? proposal;

        try
        {
            proposal = await LoadAsync(projectId, request.ProposalId, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            return Classify(exception);
        }

        if (proposal is null)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.NotFound);
        }

        // Replay. A resubmitted confirmation reports the work the first one created rather than a
        // second copy of it, which is what makes a double-click and a refresh both harmless.
        if (proposal.Status == FamiliarActionStatus.Confirmed)
        {
            return new FamiliarActionOutcome(
                FamiliarActionStatusOutcome.AlreadyConfirmed,
                proposal.CreatedTaskId,
                proposal.CreatedSessionId);
        }

        if (proposal.Status == FamiliarActionStatus.Dismissed)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.AlreadyDismissed);
        }

        if (proposal.ConcurrencyToken != request.ExpectedConcurrencyToken)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.StaleToken);
        }

        // The human's edits, validated as theirs. Only CreateTask has editable fields.
        var title = proposal.Title;
        var requestedOutcome = proposal.RequestedOutcome;

        if (proposal.Kind == FamiliarActionKind.CreateTask)
        {
            title = (request.Title ?? proposal.Title)?.Trim();
            requestedOutcome = (request.RequestedOutcome ?? proposal.RequestedOutcome)?.Trim();

            if (!ProposedActionValidator.IsWithinBounds(title, FamiliarActionProposal.MaxTitleLength))
            {
                return new FamiliarActionOutcome(
                    FamiliarActionStatusOutcome.ValidationFailed,
                    ValidationMessage:
                    $"Give the task a title of {FamiliarActionProposal.MaxTitleLength:N0} characters or fewer.");
            }

            if (!ProposedActionValidator.IsWithinBounds(
                    requestedOutcome, FamiliarActionProposal.MaxRequestedOutcomeLength))
            {
                return new FamiliarActionOutcome(
                    FamiliarActionStatusOutcome.ValidationFailed,
                    ValidationMessage:
                    $"Describe the requested outcome in {FamiliarActionProposal.MaxRequestedOutcomeLength:N0} characters or fewer.");
            }
        }

        try
        {
            return await ConfirmCoreAsync(proposal, request, title, requestedOutcome, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            // Acquiring the transaction is itself a write lock, and so the likeliest place to meet
            // SQLITE_BUSY on a contended database. It sits outside the core's own try block, as does
            // the rollback inside that block's catch; both mean nothing was committed.
            dbContext.ChangeTracker.Clear();
            return Classify(exception);
        }
    }

    public async Task<FamiliarActionOutcome> DismissAsync(
        Guid projectId,
        FamiliarActionRequest request,
        CancellationToken cancellationToken = default)
    {
        FamiliarActionProposal? proposal;

        try
        {
            proposal = await LoadAsync(projectId, request.ProposalId, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            return Classify(exception);
        }

        if (proposal is null)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.NotFound);
        }

        if (proposal.Status == FamiliarActionStatus.Dismissed)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.AlreadyDismissed);
        }

        if (proposal.Status == FamiliarActionStatus.Confirmed)
        {
            return new FamiliarActionOutcome(
                FamiliarActionStatusOutcome.AlreadyConfirmed,
                proposal.CreatedTaskId,
                proposal.CreatedSessionId);
        }

        if (proposal.ConcurrencyToken != request.ExpectedConcurrencyToken)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.StaleToken);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            // The same conditional consume as a confirmation, and deliberately so: dismissal is a
            // decision, it is terminal, and two people cannot both make it. It simply has no effect
            // to commit alongside itself.
            var consumed = await dbContext.FamiliarActionProposals
                .Where(candidate =>
                    candidate.Id == proposal.Id
                    && candidate.Status == FamiliarActionStatus.Pending
                    && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken
                    && candidate.CreatedTaskId == null
                    && candidate.CreatedSessionId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, FamiliarActionStatus.Dismissed)
                        .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                        .SetProperty(candidate => candidate.DecidedUtc, nowUtc)
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            return consumed == 1
                ? FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.Dismissed)
                : await DescribeLostRaceAsync(proposal.Id, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            dbContext.ChangeTracker.Clear();
            return Classify(exception);
        }
    }

    private async Task<FamiliarActionOutcome> ConfirmCoreAsync(
        FamiliarActionProposal proposal,
        FamiliarActionRequest request,
        string? title,
        string? requestedOutcome,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // (1) The fence, before any effect. Only one contender can turn this Pending proposal
            // into a Confirmed one, and the null created-link predicates make a second dispatch
            // impossible even if a token leaked.
            var consumed = await dbContext.FamiliarActionProposals
                .Where(candidate =>
                    candidate.Id == proposal.Id
                    && candidate.Status == FamiliarActionStatus.Pending
                    && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken
                    && candidate.CreatedTaskId == null
                    && candidate.CreatedSessionId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, FamiliarActionStatus.Confirmed)
                        .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                        .SetProperty(candidate => candidate.DecidedUtc, nowUtc)
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            if (consumed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DescribeLostRaceAsync(proposal.Id, cancellationToken);
            }

            // (2) The authoritative re-read. Everything above was a preflight; these are the checks
            // that gate dispatch, and they run against rows this transaction now holds.
            var project = await dbContext.Projects
                .SingleOrDefaultAsync(candidate => candidate.Id == proposal.ProjectId, cancellationToken);

            if (project is null || project.Status != ProjectStatus.Active)
            {
                await transaction.RollbackAsync(cancellationToken);
                return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.ProjectInactive);
            }

            var outcome = proposal.Kind switch
            {
                FamiliarActionKind.CreateTask => await CreateTaskAsync(
                    proposal, project, title!, requestedOutcome!, nowUtc, cancellationToken),

                _ => await StartPlannerAsync(proposal, project, nowUtc, cancellationToken)
            };

            if (outcome.Status is not (FamiliarActionStatusOutcome.Confirmed))
            {
                await transaction.RollbackAsync(cancellationToken);
                return outcome;
            }

            await transaction.CommitAsync(cancellationToken);
            return outcome;
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            // Nothing this caller staged was committed. Report what happened rather than presenting
            // a rolled-back transaction as success.
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return Classify(exception);
        }
    }

    /// <summary>
    /// One Ready task, and nothing else. No session starts and no worker is notified.
    ///
    /// Revision-gated: the human approved <i>content</i> they reviewed, which is exactly the case
    /// ADR-0009's gate protects. If the project moved underneath them, what they read is no longer
    /// what they would be creating.
    /// </summary>
    private async Task<FamiliarActionOutcome> CreateTaskAsync(
        FamiliarActionProposal proposal,
        FamiliarProject project,
        string title,
        string requestedOutcome,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (project.ContextRevision != proposal.ObservedContextRevision)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.ContextMoved);
        }

        var task = workflowDispatch.CreateReadyTask(project, title, requestedOutcome, nowUtc);

        dbContext.FamiliarMessages.Add(await ConfirmationMessageAsync(
            proposal,
            $"Created the task \"{task.Title}\". Nothing is running on it yet.",
            nowUtc,
            cancellationToken));

        await dbContext.SaveChangesAsync(cancellationToken);

        // (4) The durable link, written after the row it references exists.
        await dbContext.FamiliarActionProposals
            .Where(candidate => candidate.Id == proposal.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.CreatedTaskId, task.Id)
                    .SetProperty(candidate => candidate.Title, title)
                    .SetProperty(candidate => candidate.RequestedOutcome, requestedOutcome),
                cancellationToken);

        return new FamiliarActionOutcome(FamiliarActionStatusOutcome.Confirmed, CreatedTaskId: task.Id);
    }

    /// <summary>
    /// One Started Planner session on a task that already exists.
    ///
    /// <b>No revision gate</b>, deliberately. The decision is "run this role now", and the session
    /// reads whatever context is current at its own start — exactly the reasoning ADR-0010 recorded
    /// for handoffs. Gating on a revision here would refuse a perfectly good session because an
    /// unrelated task changed.
    /// </summary>
    private async Task<FamiliarActionOutcome> StartPlannerAsync(
        FamiliarActionProposal proposal,
        FamiliarProject project,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (proposal.TargetTaskId is not { } targetTaskId)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.TargetTaskInvalid);
        }

        // Ownership re-checked here rather than trusted from proposal time: the target is read back
        // with its project, so a task that moved or vanished cannot be started from this page.
        var task = await dbContext.Tasks
            .SingleOrDefaultAsync(
                candidate => candidate.Id == targetTaskId && candidate.ProjectId == project.Id,
                cancellationToken);

        if (task is null)
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.TargetTaskInvalid);
        }

        // Ultimately enforced by IX_AgentSessions_TaskId_Started; checked here so the person gets a
        // specific sentence instead of a constraint violation.
        if (await workflowDispatch.HasStartedSessionAsync(task.Id, cancellationToken))
        {
            return FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.TaskAlreadyRunning);
        }

        // Provider and external session reference stay null: the Familiar never chooses a worker.
        var session = workflowDispatch.StartSession(
            task,
            project,
            AgentSessionRole.Planner,
            provider: null,
            externalSessionReference: null,
            startedUtc: nowUtc);

        dbContext.FamiliarMessages.Add(await ConfirmationMessageAsync(
            proposal,
            $"Summoned a planner session on \"{task.Title}\". A worker may claim it automatically.",
            nowUtc,
            cancellationToken));

        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.FamiliarActionProposals
            .Where(candidate => candidate.Id == proposal.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.CreatedSessionId, session.Id),
                cancellationToken);

        return new FamiliarActionOutcome(FamiliarActionStatusOutcome.Confirmed, CreatedSessionId: session.Id);
    }

    /// <summary>
    /// The message stating exactly what was created, written inside the same transaction as the
    /// effect it describes. A transcript that claims a task exists is committed with the task or not
    /// at all.
    /// </summary>
    private async Task<FamiliarMessage> ConfirmationMessageAsync(
        FamiliarActionProposal proposal,
        string content,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var highest = await dbContext.FamiliarMessages
            .Where(message => message.ConversationId == proposal.ConversationId)
            .Select(message => (int?)message.Sequence)
            .MaxAsync(cancellationToken);

        return new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = proposal.ConversationId,
            Author = FamiliarMessageAuthor.Familiar,
            Sequence = (highest ?? 0) + 1,
            Content = content,
            CreatedUtc = nowUtc,
            Delivery = FamiliarMessageDelivery.Delivered
        };
    }

    /// <summary>
    /// The proposal, if it belongs to this project.
    ///
    /// Filtering on <c>projectId</c> here is what stops a proposal id from another project being
    /// confirmed from this page, and it uses the denormalised column so ownership needs no join.
    /// </summary>
    private Task<FamiliarActionProposal?> LoadAsync(
        Guid projectId,
        Guid proposalId,
        CancellationToken cancellationToken) =>
        dbContext.FamiliarActionProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == proposalId && candidate.ProjectId == projectId,
                cancellationToken);

    /// <summary>
    /// Zero affected rows means a real competitor consumed the row first. The committed state is
    /// re-read so the report names what actually happened rather than guessing at it.
    /// </summary>
    private async Task<FamiliarActionOutcome> DescribeLostRaceAsync(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        try
        {
            dbContext.ChangeTracker.Clear();

            var current = await dbContext.FamiliarActionProposals
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == proposalId, cancellationToken);

            return current?.Status switch
            {
                FamiliarActionStatus.Confirmed => new FamiliarActionOutcome(
                    FamiliarActionStatusOutcome.AlreadyConfirmed,
                    current.CreatedTaskId,
                    current.CreatedSessionId),

                FamiliarActionStatus.Dismissed => FamiliarActionOutcome.Of(
                    FamiliarActionStatusOutcome.AlreadyDismissed),

                // Still Pending with the update refused: the token moved under this caller.
                FamiliarActionStatus.Pending => FamiliarActionOutcome.Of(
                    FamiliarActionStatusOutcome.StaleToken),

                _ => FamiliarActionOutcome.Of(FamiliarActionStatusOutcome.NotFound)
            };
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            // Even the post-race read can meet a busy database, and a retryable read failure must
            // not be reported as a decision somebody else made.
            return Classify(exception);
        }
    }

    /// <summary>
    /// Busy and locked are retryable and nothing changed. Anything else is a real fault and must not
    /// be dressed up as either a lock or a lost race.
    /// </summary>
    private static FamiliarActionOutcome Classify(Exception exception) =>
        FamiliarActionOutcome.Of(
            SessionHandoffApprovalService.IsDatabaseBusy(exception)
                ? FamiliarActionStatusOutcome.DatabaseBusy
                : FamiliarActionStatusOutcome.Failed);

    private static bool IsExpectedDatabaseFault(Exception exception) =>
        exception is DbUpdateException or SqliteException;
}
