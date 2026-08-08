using FindFamiliar.Server.Services;

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
    ISessionHandoffApprovalService approvals) : IFamiliarDecisionGateway
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

        var request = new SessionHandoffDecisionRequest(decisionId, expectedConcurrencyToken);

        var outcome = choice switch
        {
            FamiliarDecisionChoice.Approve => await approvals.ApproveAsync(request, cancellationToken),
            FamiliarDecisionChoice.Decline => await approvals.DeclineAsync(request, cancellationToken),

            // Unreachable while the enum has two members, and an exception rather than a default so
            // adding a third member is a compile-time conversation instead of a silent approval.
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };

        return Translate(decisionId, decision.ProposedRole, outcome);
    }

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
