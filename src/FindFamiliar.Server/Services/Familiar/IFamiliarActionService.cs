namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// A confirmation request. Only these values come from the rendered page.
///
/// <see cref="ProposalId"/> and <see cref="ExpectedConcurrencyToken"/> identify and fence the row.
/// <see cref="Title"/> and <see cref="RequestedOutcome"/> are the human's edits for a
/// <c>CreateTask</c> and are ignored for anything else — a person approves content they reviewed, so
/// what gets created is what they actually saw, not what a provider originally wrote.
///
/// Everything else — the action kind, the project, the target task, the observed revision — is read
/// server-side from the proposal row, so a crafted post cannot choose an action or retarget one.
/// </summary>
public sealed record FamiliarActionRequest(
    Guid ProposalId,
    Guid ExpectedConcurrencyToken,
    string? Title = null,
    string? RequestedOutcome = null);

/// <summary>
/// The only bridge from a proposal to persisted work.
///
/// Provider text is inert until a human confirms here, and every gate is re-evaluated inside the
/// confirming transaction — the proposal row records what somebody was shown, never what may
/// execute. Effects go through <c>IWorkflowDispatchService</c>, the same boundary the manual pages
/// and conversational approval already use, so work created from a conversation is indistinguishable
/// from work created by hand.
/// </summary>
public interface IFamiliarActionService
{
    /// <summary>
    /// Consumes a Pending proposal by token and commits all of its effects, or none of them.
    ///
    /// <paramref name="projectId"/> is the project whose page the request came from, and the
    /// proposal must belong to it: a proposal id from another project cannot be confirmed from here.
    /// </summary>
    Task<FamiliarActionOutcome> ConfirmAsync(
        Guid projectId,
        FamiliarActionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes a Pending proposal and creates nothing. The token rotates, so the decision is
    /// terminal and a replay reports the truth rather than dismissing something twice.
    /// </summary>
    Task<FamiliarActionOutcome> DismissAsync(
        Guid projectId,
        FamiliarActionRequest request,
        CancellationToken cancellationToken = default);
}
