using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum WorkApprovalStatus
{
    /// <summary>This caller won the approval and created the work.</summary>
    Approved,

    /// <summary>The conversation was already approved. The original links are returned unchanged.</summary>
    AlreadyApproved,

    AlreadyRejected,

    NotFound,

    ValidationFailed,

    /// <summary>The presented token is not current — the proposal changed since it was reviewed.</summary>
    StaleProposal,

    /// <summary>The project's context advanced since the proposal was reviewed. Refresh and review again.</summary>
    StaleContext,

    /// <summary>A concurrent writer won. This caller created nothing.</summary>
    Conflict,

    /// <summary>
    /// SQLite could not take the lock this request needed. Nothing was written and nobody won, so
    /// retrying is correct.
    ///
    /// This is deliberately not <see cref="Conflict"/>. Until Sprint 10 every SqliteException here —
    /// including a busy database, a locked file, or a disk error — was reported as a lost approval
    /// race, which is both untrue and misleading: it sends the user looking for a second decision
    /// that never happened, and it can leave an approval with no winner at all.
    /// </summary>
    DatabaseBusy
}

public sealed record WorkApprovalRequest(Guid ConversationId, Guid ExpectedConcurrencyToken);

public sealed record WorkApprovalOutcome(
    WorkApprovalStatus Status,
    Guid? TaskId = null,
    Guid? SessionId = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly WorkApprovalOutcome NotFound = new(WorkApprovalStatus.NotFound);
    public static readonly WorkApprovalOutcome AlreadyRejected = new(WorkApprovalStatus.AlreadyRejected);
    public static readonly WorkApprovalOutcome StaleProposal = new(WorkApprovalStatus.StaleProposal);
    public static readonly WorkApprovalOutcome StaleContext = new(WorkApprovalStatus.StaleContext);
    public static readonly WorkApprovalOutcome Conflict = new(WorkApprovalStatus.Conflict);

    public static WorkApprovalOutcome Approved(Guid taskId, Guid sessionId) =>
        new(WorkApprovalStatus.Approved, taskId, sessionId);

    public static WorkApprovalOutcome AlreadyApproved(Guid? taskId, Guid? sessionId) =>
        new(WorkApprovalStatus.AlreadyApproved, taskId, sessionId);

    public static WorkApprovalOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(WorkApprovalStatus.ValidationFailed, ValidationErrors: errors);
}

public interface IWorkApprovalService
{
    Task<WorkApprovalOutcome> ApproveAsync(
        WorkApprovalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sprint 08's critical transaction: the single point where an approved proposal becomes real work.
///
/// The concurrency design has two parts, and both are required:
///
/// 1. <b>A conditional consume.</b> The first statement in the transaction is one UPDATE that
///    matches only a Pending proposal still carrying the presented token and still holding no
///    created task. Exactly one contender can affect a row, so the winner is chosen by the
///    database, not by a preflight read. A check-then-insert would let two contenders both pass
///    inspection and both dispatch.
/// 2. <b>One transaction around the winner's complete effects.</b> The task, the session, both
///    context-revision increments, the durable links and the visible message commit together or
///    not at all. A failure anywhere leaves no partial task or session behind.
///
/// Doing the conditional write first also keeps SQLite honest: the transaction takes its write
/// lock immediately instead of upgrading from a read, which is the shape that deadlocks rather
/// than waiting politely.
///
/// The created work is an ordinary Ready task and an ordinary Started Planner session, produced by
/// the same <see cref="IWorkflowDispatchService"/> the manual pages use. Nothing about it is
/// conversational: the work queue, assignment projection, claim service, runner, adapter and
/// result capture cannot tell it apart from a manually started session, and none of them ever
/// consult conversation state.
/// </summary>
public sealed class WorkApprovalService(
    FamiliarDbContext dbContext,
    IWorkflowDispatchService workflowDispatch,
    TimeProvider timeProvider) : IWorkApprovalService
{
    public const string ProjectField = "ProjectId";
    public const string TitleField = "Title";
    public const string RequestedOutcomeField = "RequestedOutcome";

    public async Task<WorkApprovalOutcome> ApproveAsync(
        WorkApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.ConversationId, cancellationToken);

        if (conversation is null)
        {
            return WorkApprovalOutcome.NotFound;
        }

        var proposal = await dbContext.WorkProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ConversationId == request.ConversationId, cancellationToken);

        if (proposal is null)
        {
            return WorkApprovalOutcome.NotFound;
        }

        // Replay: a resubmitted approval returns the work the first one created, never a second copy.
        if (conversation.Status == ConversationStatus.Approved || proposal.Status == WorkProposalStatus.Approved)
        {
            return WorkApprovalOutcome.AlreadyApproved(
                conversation.ApprovedTaskId ?? proposal.CreatedTaskId,
                conversation.ApprovedSessionId ?? proposal.CreatedSessionId);
        }

        if (conversation.Status == ConversationStatus.Rejected || proposal.Status == WorkProposalStatus.Rejected)
        {
            return WorkApprovalOutcome.AlreadyRejected;
        }

        if (proposal.ConcurrencyToken != request.ExpectedConcurrencyToken)
        {
            return WorkApprovalOutcome.StaleProposal;
        }

        var errors = ValidateProposalFields(proposal);
        if (errors.Count > 0)
        {
            return WorkApprovalOutcome.ValidationFailed(errors);
        }

        var projectId = proposal.ProjectId!.Value;

        var preflight = await dbContext.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == projectId)
            .Select(candidate => new { candidate.Status, candidate.ContextRevision })
            .SingleOrDefaultAsync(cancellationToken);

        if (preflight is null || preflight.Status != ProjectStatus.Active)
        {
            return WorkApprovalOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectField] = "The proposed project is no longer active. Choose an active project."
            });
        }

        if (preflight.ContextRevision != proposal.ObservedContextRevision)
        {
            return WorkApprovalOutcome.StaleContext;
        }

        return await DispatchAsync(request, proposal, projectId, cancellationToken);
    }

    /// <summary>
    /// Classifies anything that escapes <see cref="DispatchCoreAsync"/> rather than letting it reach
    /// the user as an unhandled exception.
    ///
    /// Acquiring the transaction is itself a write lock, and so the most likely place to meet
    /// SQLITE_BUSY on a contended database; it sits outside the core's own try block, as does the
    /// rollback inside that block's catch. Both mean nothing was committed.
    /// </summary>
    private async Task<WorkApprovalOutcome> DispatchAsync(
        WorkApprovalRequest request,
        WorkProposal proposal,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DispatchCoreAsync(request, proposal, projectId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            dbContext.ChangeTracker.Clear();

            return SessionHandoffApprovalService.IsDatabaseBusy(exception)
                ? new WorkApprovalOutcome(WorkApprovalStatus.DatabaseBusy)
                : WorkApprovalOutcome.Conflict;
        }
    }

    private async Task<WorkApprovalOutcome> DispatchCoreAsync(
        WorkApprovalRequest request,
        WorkProposal proposal,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // (1) The fence. Only one contender can turn this Pending proposal into an Approved one,
            // and CreatedTaskId == null makes a second dispatch impossible even if a token leaked.
            var consumed = await dbContext.WorkProposals
                .Where(candidate =>
                    candidate.Id == proposal.Id
                    && candidate.Status == WorkProposalStatus.Pending
                    && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken
                    && candidate.CreatedTaskId == null
                    && candidate.CreatedSessionId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, WorkProposalStatus.Approved)
                        .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            if (consumed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DescribeLostRaceAsync(request.ConversationId, cancellationToken);
            }

            // (2) Authoritative re-read inside the transaction. The preflight above is only a fast,
            // friendly failure; this is the check that actually gates dispatch.
            var project = await dbContext.Projects
                .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);

            if (project is null || project.Status != ProjectStatus.Active)
            {
                await transaction.RollbackAsync(cancellationToken);
                return WorkApprovalOutcome.ValidationFailed(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ProjectField] = "The proposed project is no longer active. Choose an active project."
                });
            }

            if (project.ContextRevision != proposal.ObservedContextRevision)
            {
                await transaction.RollbackAsync(cancellationToken);
                return WorkApprovalOutcome.StaleContext;
            }

            // (3) Ordinary task and ordinary Started Planner session, through the shared boundary.
            // Provider and ExternalSessionReference stay null: approval never chooses Claude.
            var task = workflowDispatch.CreateReadyTask(project, proposal.Title, proposal.RequestedOutcome, nowUtc);

            var session = workflowDispatch.StartSession(
                task,
                project,
                AgentSessionRole.Planner,
                provider: null,
                externalSessionReference: null,
                startedUtc: nowUtc);

            dbContext.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = request.ConversationId,
                Author = ConversationMessageAuthor.Familiar,
                Sequence = await WorkProposalService.NextSequenceAsync(
                    dbContext,
                    request.ConversationId,
                    cancellationToken),
                Content = ProposalMessageComposer.Approved(project.Name, task.Title, session.ContextRevisionRead),
                CreatedUtc = nowUtc
            });

            await dbContext.SaveChangesAsync(cancellationToken);

            // (4) Durable links, written after the rows they reference exist.
            await dbContext.WorkProposals
                .Where(candidate => candidate.Id == proposal.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.CreatedTaskId, task.Id)
                        .SetProperty(candidate => candidate.CreatedSessionId, session.Id),
                    cancellationToken);

            await dbContext.Conversations
                .Where(candidate => candidate.Id == request.ConversationId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, ConversationStatus.Approved)
                        .SetProperty(candidate => candidate.ApprovedTaskId, task.Id)
                        .SetProperty(candidate => candidate.ApprovedSessionId, session.Id)
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return WorkApprovalOutcome.Approved(task.Id, session.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            // Nothing this caller staged was committed. Report what actually happened rather than
            // presenting a rolled-back transaction as success.
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();

            // A locked database is not a lost race. Saying it is would claim a competing approval
            // that never happened, and — because the winner can hit this too — could leave a
            // contended approval reporting no winner at all.
            if (SessionHandoffApprovalService.IsDatabaseBusy(exception))
            {
                return new WorkApprovalOutcome(WorkApprovalStatus.DatabaseBusy);
            }

            return await DescribeLostRaceAsync(request.ConversationId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Re-reads committed state to tell a losing contender the truth: usually that someone else
    /// approved the same proposal, in which case the original links are returned.
    /// </summary>
    private async Task<WorkApprovalOutcome> DescribeLostRaceAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.Conversations
            .AsNoTracking()
            .Where(candidate => candidate.Id == conversationId)
            .Select(candidate => new
            {
                candidate.Status,
                candidate.ApprovedTaskId,
                candidate.ApprovedSessionId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            return WorkApprovalOutcome.NotFound;
        }

        return current.Status switch
        {
            ConversationStatus.Approved =>
                WorkApprovalOutcome.AlreadyApproved(current.ApprovedTaskId, current.ApprovedSessionId),
            ConversationStatus.Rejected => WorkApprovalOutcome.AlreadyRejected,
            _ => WorkApprovalOutcome.Conflict
        };
    }

    private static Dictionary<string, string> ValidateProposalFields(WorkProposal proposal)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (proposal.ProjectId is null || proposal.ProjectId == Guid.Empty)
        {
            errors[ProjectField] = "Select the project this work belongs to before approving.";
        }

        if (string.IsNullOrWhiteSpace(proposal.Title))
        {
            errors[TitleField] = "A task title is required.";
        }
        else if (proposal.Title.Length > WorkProposal.MaxTitleLength)
        {
            errors[TitleField] = $"The task title must be {WorkProposal.MaxTitleLength} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(proposal.RequestedOutcome))
        {
            errors[RequestedOutcomeField] = "A requested outcome is required.";
        }
        else if (proposal.RequestedOutcome.Length > WorkProposal.MaxRequestedOutcomeLength)
        {
            errors[RequestedOutcomeField] =
                $"The requested outcome must be {WorkProposal.MaxRequestedOutcomeLength:N0} characters or fewer.";
        }

        if (proposal.ObservedContextRevision is null)
        {
            errors[ProjectField] = "Refresh the project context and review the proposal before approving.";
        }

        return errors;
    }
}
