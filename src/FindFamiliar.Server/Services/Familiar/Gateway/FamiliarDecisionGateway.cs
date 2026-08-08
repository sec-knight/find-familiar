using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;

namespace FindFamiliar.Server.Services.Familiar.Gateway;

/// <summary>What the human chose. Two members, closed, and no third that could mean "decide for me".</summary>
public enum FamiliarDecisionChoice
{
    Approve,
    Decline
}

/// <summary>
/// How a relayed decision ended, in terms an external client can act on.
///
/// Every member is something the workflow actually reported. There is no member meaning "probably
/// worked" and none meaning "something went wrong, try again blindly" — a client that cannot tell a
/// stale view from a lost race will tell the human the wrong thing about their own decision.
/// </summary>
public enum FamiliarDecisionOutcome
{
    /// <summary>Approved, and the session it authorised was created.</summary>
    Approved,

    /// <summary>Declined. Nothing was created, and the step will not run.</summary>
    Declined,

    /// <summary>
    /// A real earlier decision already settled this, and the result of that first decision is
    /// returned. A replayed submission reports the original rather than acting twice.
    /// </summary>
    AlreadyDecided,

    /// <summary>
    /// The token presented was not the current one: the decision was taken against a view that has
    /// since moved. Nothing was changed, and the client should re-read before asking again.
    /// </summary>
    StaleDecision,

    /// <summary>No decision with that id is available to this caller. Nothing was changed.</summary>
    NotFound,

    /// <summary>The workflow refused: the step is no longer legal to take. Nothing was changed.</summary>
    NotCurrentlyLegal,

    /// <summary>The database was busy. Nothing was changed and nobody else decided anything — retry.</summary>
    Busy
}

/// <param name="CreatedSessionId">The session an approval created, or the one the first approval created on replay.</param>
/// <param name="Detail">
/// One sentence a client may read to the human. Authored here, never provider text, and never a
/// description of the credential or of anything the caller could not already see.
/// </param>
public sealed record FamiliarDecisionResult(
    FamiliarDecisionOutcome Outcome,
    Guid DecisionId,
    Guid? CreatedSessionId,
    string? ProposedRole,
    string Detail);

public interface IFamiliarDecisionGateway
{
    Task<FamiliarDecisionResult> SubmitAsync(
        Guid decisionId,
        Guid expectedConcurrencyToken,
        FamiliarDecisionChoice choice,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The one place an external client's message can change this system's state.
///
/// <b>Deliberately not part of <see cref="IFamiliarGateway"/>.</b> That type documents, as a
/// structural fact rather than a promise, that it holds nothing which can write — and that fact is
/// worth more than the convenience of one more method on it. So the single write lives here, in a
/// type whose whole purpose is visible in its name, and the read gateway stays provably read-only.
///
/// <b>It decides nothing.</b> It resolves what the caller is permitted to see, then hands the
/// human's choice to <see cref="ISessionHandoffApprovalService"/> — the same transaction the
/// Demiplane's own button posts to, with the same conditional consume, the same token fence and the
/// same partial unique index behind it. Whether the step is legal is re-evaluated inside that
/// transaction, after this class has stopped being involved. A model that fabricated a decision id
/// would reach a service that refuses it, not a table.
///
/// <b>Visibility first, and for a reason.</b> The approval service answers about handoffs; it knows
/// nothing about sensitivity, because the Demiplane's user is the owner of every project. An external
/// client is not. So a submission is matched against the decisions this caller may actually read, and
/// a decision in a sensitive project answers exactly as one that does not exist — naming which of the
/// two applied would itself be the disclosure the sensitivity rule withholds.
/// </summary>
public sealed class FamiliarDecisionGateway(
    IFamiliarGateway gateway,
    ISessionHandoffApprovalService approvals,
    IPendingPlanReader pendingPlans,
    IFamiliarPlanApprovalService planApprovals) : IFamiliarDecisionGateway
{
    public async Task<FamiliarDecisionResult> SubmitAsync(
        Guid decisionId,
        Guid expectedConcurrencyToken,
        FamiliarDecisionChoice choice,
        CancellationToken cancellationToken = default)
    {
        // The same list the caller was shown, recomputed rather than trusted from the request. This is
        // what enforces sensitivity and project isolation on the write path, and it is deliberately
        // not a cheaper lookup by id: a cheaper lookup would be one that did not know about them.
        var open = await gateway.ListOpenDecisionsAsync(cancellationToken);
        var decision = open.Decisions.SingleOrDefault(candidate => candidate.DecisionId == decisionId);

        if (decision is null)
        {
            return new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotFound,
                decisionId,
                null,
                null,
                "No decision with that id is currently waiting. It may have been decided already, or it "
                + "may not be one this connection can see. Nothing was changed.");
        }

        // Two decision kinds, two authoritative services, and the kind comes from the row rather than
        // from the caller — a client cannot choose which gate its decision is carried to.
        return decision.DecisionKind switch
        {
            "PlanProposal" => await SubmitPlanAsync(decisionId, expectedConcurrencyToken, choice, cancellationToken),
            _ => await SubmitHandoffAsync(decisionId, expectedConcurrencyToken, choice, decision, cancellationToken)
        };
    }

    private async Task<FamiliarDecisionResult> SubmitHandoffAsync(
        Guid decisionId,
        Guid expectedConcurrencyToken,
        FamiliarDecisionChoice choice,
        FamiliarOpenDecision decision,
        CancellationToken cancellationToken)
    {
        var request = new SessionHandoffDecisionRequest(decisionId, expectedConcurrencyToken);

        var outcome = choice switch
        {
            FamiliarDecisionChoice.Approve => await approvals.ApproveAsync(request, cancellationToken),
            FamiliarDecisionChoice.Decline => await approvals.DeclineAsync(request, cancellationToken),

            // Unreachable while the enum has two members, and an exception rather than a default so
            // adding a third member is a compile-time conversation instead of a silent approval.
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };

        return Translate(decisionId, decision.ProposedRole ?? "session", outcome);
    }

    /// <summary>
    /// A plan decision, carried to the same service the chat page's buttons post to.
    ///
    /// <b>Approved exactly as drafted, and that is the whole design.</b> The request carries no item
    /// decisions, which the approval service reads as "the human changed nothing" — every item keeps
    /// the inclusion and the wording they were shown. The Familiar therefore cannot include an item
    /// the person excluded, exclude one they wanted, or reword a task into something they never read.
    /// Editing a plan stays where the editing controls are; what travels through here is a yes or a no.
    /// </summary>
    private async Task<FamiliarDecisionResult> SubmitPlanAsync(
        Guid planId,
        Guid expectedConcurrencyToken,
        FamiliarDecisionChoice choice,
        CancellationToken cancellationToken)
    {
        // The chat the plan belongs to. The approval service is addressed by chat and checks the two
        // agree, so this is resolved from the row rather than accepted from the caller.
        var plan = (await pendingPlans.ListPendingAsync(cancellationToken))
            .SingleOrDefault(candidate => candidate.PlanId == planId);

        if (plan is null)
        {
            return new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotFound, planId, null, null,
                "That plan is no longer waiting. It may have been decided already. Nothing was changed.");
        }

        var request = new FamiliarPlanDecisionRequest(planId, expectedConcurrencyToken, []);

        var outcome = choice switch
        {
            FamiliarDecisionChoice.Approve => await planApprovals.ApproveAsync(plan.ChatId, request, cancellationToken),
            FamiliarDecisionChoice.Decline => await planApprovals.DeclineAsync(plan.ChatId, request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };

        return TranslatePlan(planId, outcome);
    }

    /// <summary>
    /// The plan service's outcome, restated. The distinctions it draws are preserved rather than
    /// flattened — a stale token, a project whose context moved, and a busy database are three
    /// different things to tell a person, and only one of them means "ask again".
    /// </summary>
    private static FamiliarDecisionResult TranslatePlan(Guid planId, FamiliarPlanOutcome outcome) =>
        outcome.Status switch
        {
            FamiliarPlanOutcomeStatus.Approved => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.Approved, planId, outcome.StartedSessionId, outcome.StartedRole?.ToString(),
                $"Approved. {outcome.CreatedTaskCount} task{(outcome.CreatedTaskCount == 1 ? "" : "s")} created"
                + (outcome.StartedRole is { } role ? $", and a {role} session has started." : ", and no session started.")),

            FamiliarPlanOutcomeStatus.Declined => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.Declined, planId, null, null,
                "Declined. Nothing was created."),

            FamiliarPlanOutcomeStatus.AlreadyApproved => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.AlreadyDecided, planId, outcome.StartedSessionId, null,
                "This plan was already approved. The work from that first approval is unchanged, and "
                + "this request created nothing."),

            FamiliarPlanOutcomeStatus.AlreadyDeclined => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.AlreadyDecided, planId, null, null,
                "This plan was already declined. Nothing was changed."),

            FamiliarPlanOutcomeStatus.StaleToken => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.StaleDecision, planId, null, null,
                "This plan changed after you were shown it, so nothing was done. Ask me what needs you "
                + "again, and decide against the current plan."),

            // Distinct from a stale token on purpose: the plan is unchanged, but the project it was
            // drafted against has moved, so the plan is about a world that no longer exists.
            FamiliarPlanOutcomeStatus.ContextMoved => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.StaleDecision, planId, null, null,
                "The project's context changed after this plan was drafted, so it was not applied. "
                + "Nothing was created."),

            FamiliarPlanOutcomeStatus.NotFound => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotFound, planId, null, null,
                "That plan is no longer available. Nothing was changed."),

            FamiliarPlanOutcomeStatus.NothingIncluded => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, planId, null, null,
                "Every item in that plan is excluded, so approving it would create nothing."),

            FamiliarPlanOutcomeStatus.TaskAlreadyRunning => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, planId, null, null,
                "A session is already running on the task this plan would have started. Nothing was changed."),

            FamiliarPlanOutcomeStatus.ProjectInactive => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, planId, null, null,
                "That project is not active. Nothing was changed."),

            FamiliarPlanOutcomeStatus.DatabaseBusy => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.Busy, planId, null, null,
                "The database was busy. Nothing was changed and nobody else decided anything — this can "
                + "be retried."),

            _ => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, planId, null, null,
                "The workflow did not accept that decision. Nothing was changed.")
        };

    /// <summary>
    /// The workflow's outcome, restated for a client that must explain it to a person.
    ///
    /// The distinctions the domain draws are preserved rather than flattened. In particular a busy
    /// database is never reported as a conflict: telling someone another actor got there first, when
    /// no such actor exists, sends them looking for a person who was never there.
    /// </summary>
    private static FamiliarDecisionResult Translate(
        Guid decisionId,
        string proposedRole,
        SessionHandoffDecisionOutcome outcome) =>
        outcome.Status switch
        {
            SessionHandoffDecisionStatus.Approved => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.Approved, decisionId, outcome.SessionId, proposedRole,
                $"Approved. A {proposedRole} session has started."),

            SessionHandoffDecisionStatus.Declined => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.Declined, decisionId, null, proposedRole,
                $"Declined. The {proposedRole} session will not run, and nothing was created."),

            SessionHandoffDecisionStatus.AlreadyApproved => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.AlreadyDecided, decisionId, outcome.SessionId, proposedRole,
                "This was already approved. The session from that first approval is unchanged, and this "
                + "request created nothing."),

            SessionHandoffDecisionStatus.AlreadyDeclined => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.AlreadyDecided, decisionId, null, proposedRole,
                "This was already declined. Nothing was changed."),

            SessionHandoffDecisionStatus.StaleHandoff => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.StaleDecision, decisionId, null, proposedRole,
                "This decision changed after you were shown it, so nothing was done. Ask me what needs "
                + "you again, and decide against the current state."),

            SessionHandoffDecisionStatus.Superseded => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.StaleDecision, decisionId, null, proposedRole,
                "Something newer happened on this task and replaced this decision point. Nothing was "
                + "changed."),

            SessionHandoffDecisionStatus.NotFound => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotFound, decisionId, null, proposedRole,
                "That decision is no longer available. Nothing was changed."),

            SessionHandoffDecisionStatus.SessionAlreadyStarted => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, decisionId, null, proposedRole,
                "That task already has a session running, so another cannot start. Nothing was changed."),

            SessionHandoffDecisionStatus.TaskClosed => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, decisionId, null, proposedRole,
                "That task is closed, so no further work starts on it. Nothing was changed."),

            SessionHandoffDecisionStatus.ProjectInactive => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, decisionId, null, proposedRole,
                "That project is not active. Nothing was changed."),

            SessionHandoffDecisionStatus.DatabaseBusy => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.Busy, decisionId, null, proposedRole,
                "The database was busy. Nothing was changed and nobody else decided anything — this can "
                + "be retried."),

            _ => new FamiliarDecisionResult(
                FamiliarDecisionOutcome.NotCurrentlyLegal, decisionId, null, proposedRole,
                "The workflow did not accept that decision. Nothing was changed.")
        };
}
