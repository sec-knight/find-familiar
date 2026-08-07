using FindFamiliar.Server.Data;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>What a person decided about one item of a plan, before it is believed.</summary>
public sealed record FamiliarPlanItemDecision(
    Guid ItemId,
    bool IsIncluded,
    string? Title = null,
    string? RequestedOutcome = null);

public sealed record FamiliarPlanDecisionRequest(
    Guid PlanId,
    Guid ExpectedConcurrencyToken,
    IReadOnlyList<FamiliarPlanItemDecision> Items);

public enum FamiliarPlanOutcomeStatus
{
    Approved,
    Declined,

    /// <summary>A resubmitted decision, reporting the work the first one created.</summary>
    AlreadyApproved,

    AlreadyDeclined,
    NotFound,

    /// <summary>The plan moved between rendering and deciding. Nothing was applied.</summary>
    StaleToken,

    ProjectInactive,

    /// <summary>The project's context revision moved since the plan was drafted.</summary>
    ContextMoved,

    /// <summary>The task the first session would run on already has one started.</summary>
    TaskAlreadyRunning,

    /// <summary>Every item was excluded, so approving would create nothing.</summary>
    NothingIncluded,

    ValidationFailed,
    DatabaseBusy
}

public sealed record FamiliarPlanOutcome(
    FamiliarPlanOutcomeStatus Status,
    int CreatedTaskCount = 0,
    Guid? StartedSessionId = null,
    AgentSessionRole? StartedRole = null,
    string? ValidationMessage = null)
{
    public static FamiliarPlanOutcome Of(FamiliarPlanOutcomeStatus status) => new(status);
}

public interface IFamiliarPlanApprovalService
{
    Task<FamiliarPlanOutcome> ApproveAsync(
        Guid chatId,
        FamiliarPlanDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<FamiliarPlanOutcome> DeclineAsync(
        Guid chatId,
        FamiliarPlanDecisionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a human's approval of a drafted plan into work, under the same transaction shape
/// <see cref="FamiliarActionService"/> uses.
///
/// The order is the design, and it is not a new one: take the write lock, conditionally consume the
/// Pending row by token <b>before any effect</b>, re-validate every gate inside the transaction,
/// dispatch through <c>IWorkflowDispatchService</c> — the same boundary the manual pages use — write
/// the durable links after the rows exist, and commit it all together. The database picks the winner,
/// and the winner's complete effects commit or none of them do.
///
/// <b>On ADR-0014's "exactly one path".</b> That constraint is about the shape, not the file: every
/// effect goes through the shared dispatch boundary with gates re-checked inside the committing
/// transaction, so work approved in conversation is indistinguishable from work created by hand.
/// This is a second service rather than a branch inside <see cref="FamiliarActionService"/> because
/// that one is bound to the per-project conversation's proposal and message rows, and threading a
/// second, multi-item proposal type through it would have made both harder to read. The rule it must
/// not break — and does not — is that no code path creates work without a consumed proposal row and a
/// re-validated gate.
///
/// <b>Approval creates every included task and starts exactly one session</b> (ADR-0014 §4). A plan
/// written before any of it ran is a guess, and the first session's result is the best evidence about
/// whether the second step is still right; a plan that drained on its own would throw that away at
/// the moment it is worth most.
///
/// Provider text reaches nothing structural. The project, the revision and the roles are read from
/// rows; the only model-derived values a human carries forward are the titles and outcomes they were
/// shown, which are theirs to edit and are re-validated as their own.
/// </summary>
public sealed class FamiliarPlanApprovalService(
    FamiliarDbContext dbContext,
    IWorkflowDispatchService workflowDispatch,
    TimeProvider timeProvider) : IFamiliarPlanApprovalService
{
    public async Task<FamiliarPlanOutcome> ApproveAsync(
        Guid chatId,
        FamiliarPlanDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        FamiliarPlanProposal? plan;

        try
        {
            plan = await LoadAsync(chatId, request.PlanId, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            return Classify(exception);
        }

        if (plan is null)
        {
            return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.NotFound);
        }

        if (Settled(plan) is { } settled)
        {
            return settled;
        }

        if (plan.ConcurrencyToken != request.ExpectedConcurrencyToken)
        {
            // The plan was decided or redrawn between the render and the click. Refused with the
            // specific reason rather than applied to a plan the person did not read.
            return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.StaleToken);
        }

        var decided = Apply(plan, request);

        if (Validate(decided) is { } invalid)
        {
            return invalid;
        }

        if (decided.All(item => !item.IsIncluded))
        {
            return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.NothingIncluded);
        }

        try
        {
            return await ApproveCoreAsync(plan, request, decided, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            dbContext.ChangeTracker.Clear();
            return Classify(exception);
        }
    }

    public async Task<FamiliarPlanOutcome> DeclineAsync(
        Guid chatId,
        FamiliarPlanDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await LoadAsync(chatId, request.PlanId, cancellationToken);

            if (plan is null)
            {
                return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.NotFound);
            }

            if (Settled(plan) is { } settled)
            {
                return settled;
            }

            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

            // The same conditional consume as approval, with no effects to follow it. Declining
            // creates nothing, so there is nothing to roll back and no transaction to hold.
            var consumed = await dbContext.FamiliarPlanProposals
                .Where(candidate =>
                    candidate.Id == plan.Id
                    && candidate.Status == FamiliarPlanStatus.Pending
                    && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, FamiliarPlanStatus.Declined)
                        .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                        .SetProperty(candidate => candidate.DecidedUtc, nowUtc)
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            return consumed == 1
                ? FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.Declined)
                : await DescribeLostRaceAsync(plan.Id, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            dbContext.ChangeTracker.Clear();
            return Classify(exception);
        }
    }

    private async Task<FamiliarPlanOutcome> ApproveCoreAsync(
        FamiliarPlanProposal plan,
        FamiliarPlanDecisionRequest request,
        IReadOnlyList<DecidedItem> decided,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // (1) The fence, before any effect. Only one contender turns this Pending plan into an
            // Approved one, so a double-click cannot create two sets of tasks.
            var consumed = await dbContext.FamiliarPlanProposals
                .Where(candidate =>
                    candidate.Id == plan.Id
                    && candidate.Status == FamiliarPlanStatus.Pending
                    && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, FamiliarPlanStatus.Approved)
                        .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                        .SetProperty(candidate => candidate.DecidedUtc, nowUtc)
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            if (consumed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DescribeLostRaceAsync(plan.Id, cancellationToken);
            }

            // (2) The authoritative re-read. Everything before this was a preflight; these are the
            // checks that gate dispatch, and they run against rows this transaction holds.
            var project = await dbContext.Projects
                .SingleOrDefaultAsync(candidate => candidate.Id == plan.ProjectId, cancellationToken);

            if (project is null || project.Status != ProjectStatus.Active)
            {
                await transaction.RollbackAsync(cancellationToken);
                return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.ProjectInactive);
            }

            // A plan is content a person read and approved, which is exactly the case ADR-0009's
            // revision gate protects. If the project moved underneath them, what they read is no
            // longer what they would be creating.
            if (project.ContextRevision != plan.ObservedContextRevision)
            {
                await transaction.RollbackAsync(cancellationToken);
                return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.ContextMoved);
            }

            // (3) The effects, through the shared dispatch boundary. Tasks first, in the order the
            // plan read, so the created work matches what was on screen top to bottom.
            var created = new List<(DecidedItem Item, FamiliarTask Task)>();

            foreach (var item in decided.Where(candidate => candidate.IsIncluded))
            {
                created.Add((item, workflowDispatch.CreateReadyTask(project, item.Title, item.RequestedOutcome, nowUtc)));
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            // (4) Exactly one session, on the first included item that named a role.
            var starting = created.FirstOrDefault(entry => entry.Item.Role is not null);
            AgentSession? session = null;

            if (starting.Task is not null && starting.Item.Role is { } role)
            {
                if (await workflowDispatch.HasStartedSessionAsync(starting.Task.Id, cancellationToken))
                {
                    // Unreachable for a task created moments ago in this transaction, and checked
                    // anyway: the alternative to a specific sentence is a constraint violation.
                    await transaction.RollbackAsync(cancellationToken);
                    return FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.TaskAlreadyRunning);
                }

                // Provider and external reference stay null: the Familiar never chooses a worker.
                session = workflowDispatch.StartSession(
                    starting.Task,
                    project,
                    role,
                    provider: null,
                    externalSessionReference: null,
                    startedUtc: nowUtc);

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            // (5) The durable links, written after the rows they reference exist, along with the
            // human's edits — so the transcript shows what was created rather than what was drafted.
            foreach (var (item, task) in created)
            {
                await dbContext.FamiliarPlanItems
                    .Where(candidate => candidate.Id == item.ItemId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(candidate => candidate.CreatedTaskId, task.Id)
                            .SetProperty(candidate => candidate.Title, item.Title)
                            .SetProperty(candidate => candidate.RequestedOutcome, item.RequestedOutcome),
                        cancellationToken);
            }

            foreach (var item in decided.Where(candidate => !candidate.IsIncluded))
            {
                await dbContext.FamiliarPlanItems
                    .Where(candidate => candidate.Id == item.ItemId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(candidate => candidate.IsIncluded, false),
                        cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new FamiliarPlanOutcome(
                FamiliarPlanOutcomeStatus.Approved,
                created.Count,
                session?.Id,
                session is null ? null : starting.Item.Role);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            // Nothing staged here was committed. Reported as what happened rather than presented as
            // a success that rolled back.
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return Classify(exception);
        }
    }

    /// <summary>One item after the human's decision has been folded into it.</summary>
    private sealed record DecidedItem(
        Guid ItemId,
        bool IsIncluded,
        string Title,
        string RequestedOutcome,
        AgentSessionRole? Role);

    /// <summary>
    /// The plan as the human left it: their inclusions and their wording, falling back to what was
    /// drafted where they changed nothing.
    ///
    /// An item the request does not mention keeps its drafted state. A form that failed to post one
    /// checkbox must not silently drop work the person believed they were approving.
    /// </summary>
    private static IReadOnlyList<DecidedItem> Apply(
        FamiliarPlanProposal plan,
        FamiliarPlanDecisionRequest request)
    {
        var decisions = request.Items.ToDictionary(item => item.ItemId);

        return plan.Items
            .OrderBy(item => item.Position)
            .Select(item =>
            {
                if (!decisions.TryGetValue(item.Id, out var decision))
                {
                    return new DecidedItem(item.Id, item.IsIncluded, item.Title, item.RequestedOutcome, item.Role);
                }

                return new DecidedItem(
                    item.Id,
                    decision.IsIncluded,
                    (decision.Title ?? item.Title).Trim(),
                    (decision.RequestedOutcome ?? item.RequestedOutcome).Trim(),
                    item.Role);
            })
            .ToList();
    }

    /// <summary>
    /// The human's edits, validated as theirs — the same bounds a task typed by hand must meet.
    /// Excluded items are not validated: nothing will be created from them.
    /// </summary>
    private static FamiliarPlanOutcome? Validate(IReadOnlyList<DecidedItem> decided)
    {
        foreach (var item in decided.Where(candidate => candidate.IsIncluded))
        {
            if (item.Title.Length == 0 || item.Title.Length > FamiliarPlanItem.MaxTitleLength)
            {
                return new FamiliarPlanOutcome(
                    FamiliarPlanOutcomeStatus.ValidationFailed,
                    ValidationMessage:
                    $"Give every included item a title of {FamiliarPlanItem.MaxTitleLength:N0} characters or fewer.");
            }

            if (item.RequestedOutcome.Length == 0
                || item.RequestedOutcome.Length > FamiliarPlanItem.MaxRequestedOutcomeLength)
            {
                return new FamiliarPlanOutcome(
                    FamiliarPlanOutcomeStatus.ValidationFailed,
                    ValidationMessage:
                    $"Describe every included item's outcome in {FamiliarPlanItem.MaxRequestedOutcomeLength:N0} characters or fewer.");
            }
        }

        return null;
    }

    /// <summary>
    /// A replayed decision reports what the first one did rather than doing it again, which is what
    /// makes a double-click and a refresh both harmless.
    /// </summary>
    private static FamiliarPlanOutcome? Settled(FamiliarPlanProposal plan) => plan.Status switch
    {
        FamiliarPlanStatus.Approved => new FamiliarPlanOutcome(
            FamiliarPlanOutcomeStatus.AlreadyApproved,
            plan.Items.Count(item => item.CreatedTaskId is not null)),
        FamiliarPlanStatus.Declined => FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.AlreadyDeclined),
        _ => null
    };

    private async Task<FamiliarPlanProposal?> LoadAsync(
        Guid chatId,
        Guid planId,
        CancellationToken cancellationToken) =>
        await dbContext.FamiliarPlanProposals
            .AsNoTracking()
            .Include(plan => plan.Items)
            // Filtered on the conversation as well as the id, so a plan id from another conversation
            // cannot be decided from this page.
            .SingleOrDefaultAsync(plan => plan.Id == planId && plan.ChatId == chatId, cancellationToken);

    /// <summary>
    /// Why the conditional consume matched nothing: somebody else decided it first, or the token was
    /// stale. Read after the fact, so the answer describes what actually happened.
    /// </summary>
    private async Task<FamiliarPlanOutcome> DescribeLostRaceAsync(Guid planId, CancellationToken cancellationToken)
    {
        var current = await dbContext.FamiliarPlanProposals
            .AsNoTracking()
            .Include(plan => plan.Items)
            .SingleOrDefaultAsync(plan => plan.Id == planId, cancellationToken);

        return current is null
            ? FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.NotFound)
            : Settled(current) ?? FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.StaleToken);
    }

    private static bool IsExpectedDatabaseFault(Exception exception) =>
        exception is DbUpdateException or SqliteException;

    private static FamiliarPlanOutcome Classify(Exception exception) =>
        FamiliarPlanOutcome.Of(FamiliarPlanOutcomeStatus.DatabaseBusy);
}
