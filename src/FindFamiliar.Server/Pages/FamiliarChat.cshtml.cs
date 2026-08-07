using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// <c>/Familiar/Chat/{chatId}</c> — one conversation, read from the server.
///
/// The server is the conversation and this page is a window onto it. Everything rendered comes from
/// <see cref="IFamiliarChatService"/> already ordered by sequence, so a reload, a service restart and
/// a second device all produce the same page. Nothing that matters lives in the browser.
///
/// A send commits a turn and returns; the reply is generated out of band. That means this page can be
/// closed the instant after sending without losing the answer, which is the property slice 1 exists
/// to establish.
///
/// <c>GET</c> writes nothing on any branch. The writes are <c>OnPostSend</c> and
/// <c>OnPostDecidePlan</c>, both with antiforgery. Neither touches project state here: deciding a
/// plan goes through <see cref="IFamiliarPlanApprovalService"/>, which re-checks every gate inside
/// the transaction that applies it.
/// </summary>
public sealed class FamiliarChatModel(
    IFamiliarChatService chats,
    IFamiliarPlanApprovalService plans,
    IFamiliarOpenDecisionsService decisions,
    ISessionHandoffApprovalService handoffs) : PageModel
{
    /// <summary>
    /// How long the page waits before re-reading while a turn is in flight.
    ///
    /// A meta refresh, as the Demiplane uses, and deliberately not a stream: slice 1 has no provider
    /// and therefore no tokens to stream. Slice 2 replaces this with SSE resuming from
    /// <see cref="Cursor"/>, which is why the cursor is already rendered here.
    /// </summary>
    public const int InFlightRefreshSeconds = 3;

    public FamiliarChatView? Chat { get; private set; }

    /// <summary>
    /// Everything waiting on a human right now, across every project this conversation may see.
    ///
    /// Rendered above the transcript rather than inside it, because a decision is not part of the
    /// conversation's history — it is current state, and it must be visible on a conversation that
    /// has nothing to do with the task it belongs to. A person should never have to remember which
    /// chat they started something in.
    /// </summary>
    public IReadOnlyList<FamiliarOpenDecision> OpenDecisions { get; private set; } = [];

    /// <summary>The message being composed. The only value bound from the send form.</summary>
    [BindProperty]
    public string? Message { get; set; }

    public string? SendValidationMessage { get; private set; }

    /// <summary>The highest sequence this render contains.</summary>
    public int Cursor => Chat?.LatestSequence ?? 0;

    /// <summary>
    /// What the client resumes from, which stops before a turn still arriving so the stream does not
    /// skip a half-written reply. Computed by the view, not here, so the page and the stream cannot
    /// disagree about it.
    /// </summary>
    public int ResumeCursor => Chat?.ResumeCursor ?? 0;

    /// <summary>True when a reply is being generated, on this device or any other.</summary>
    public bool HasTurnInFlight => Chat?.InFlightTurn is not null;

    public async Task<IActionResult> OnGetAsync(Guid chatId, CancellationToken cancellationToken) =>
        await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();

    private async Task<bool> LoadAsync(Guid chatId, CancellationToken cancellationToken)
    {
        Chat = await chats.GetAsync(chatId, cancellationToken);

        if (Chat is null)
        {
            return false;
        }

        OpenDecisions = await decisions.ReadAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// True when the person pressed "Plan this" rather than "Send".
    ///
    /// Two buttons on one form rather than intent guessed from the words. A question containing the
    /// word "plan" must not sometimes draft work and sometimes not, with no way to tell in advance
    /// which one is about to happen — and the turn that costs a second model call should be the one
    /// somebody chose to pay for.
    /// </summary>
    [BindProperty]
    public bool RequestPlan { get; set; }

    public async Task<IActionResult> OnPostSendAsync(Guid chatId, CancellationToken cancellationToken)
    {
        var result = await chats.SendAsync(chatId, Message ?? string.Empty, null, RequestPlan, cancellationToken);

        switch (result.Status)
        {
            case FamiliarChatSendStatus.Accepted:
                return RedirectToPage(new { chatId });

            case FamiliarChatSendStatus.ChatNotFound:
                return NotFound();

            case FamiliarChatSendStatus.Invalid:
                SendValidationMessage = result.ValidationMessage;
                return await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();

            case FamiliarChatSendStatus.Attached:
                // Attached, not queued. The message stays in the composer rather than being written
                // behind a reply that is still arriving — nothing typed is lost, and nothing is sent
                // that the person has not seen the answer to.
                SendValidationMessage =
                    "A reply is still arriving on this conversation, so this was not sent. "
                    + "It is still in the box — send it once the reply finishes.";
                return await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();

            default:
                TempData["StatusMessage"] =
                    "The database was busy and your message was not sent. Nothing was changed — try again.";
                return RedirectToPage(new { chatId });
        }
    }

    // ---------------------------------------------------------------- deciding a plan

    [BindProperty]
    public Guid PlanId { get; set; }

    [BindProperty]
    public Guid ExpectedConcurrencyToken { get; set; }

    /// <summary>True for Approve, false for Decline. Two buttons, one form, one decision.</summary>
    [BindProperty]
    public bool Approve { get; set; }

    [BindProperty]
    public List<PlanItemInput> Items { get; set; } = [];

    /// <summary>Shown on the plan card when a decision was refused. Authored here, never by a model.</summary>
    public string? PlanMessage { get; private set; }

    /// <summary>One item as the form posted it, before the service is told about it.</summary>
    public sealed class PlanItemInput
    {
        public Guid ItemId { get; set; }

        public bool IsIncluded { get; set; }

        public string? Title { get; set; }

        public string? RequestedOutcome { get; set; }
    }

    /// <summary>
    /// The one human gate of Sprint 13, answered in the conversation rather than on a task page.
    ///
    /// Post/redirect/get on success, so a refresh cannot resubmit — though a resubmission would be
    /// harmless anyway: the service consumes the Pending row by token before any effect, and a replay
    /// reports what the first decision created rather than creating a second copy.
    /// </summary>
    public async Task<IActionResult> OnPostDecidePlanAsync(Guid chatId, CancellationToken cancellationToken)
    {
        var request = new FamiliarPlanDecisionRequest(
            PlanId,
            ExpectedConcurrencyToken,
            Items
                .Select(item => new FamiliarPlanItemDecision(
                    item.ItemId,
                    item.IsIncluded,
                    item.Title,
                    item.RequestedOutcome))
                .ToList());

        var outcome = Approve
            ? await plans.ApproveAsync(chatId, request, cancellationToken)
            : await plans.DeclineAsync(chatId, request, cancellationToken);

        switch (outcome.Status)
        {
            case FamiliarPlanOutcomeStatus.Approved:
                TempData["StatusMessage"] = outcome.StartedSessionId is null
                    ? $"Created {outcome.CreatedTaskCount} task(s). No session was started."
                    : $"Created {outcome.CreatedTaskCount} task(s) and started one {outcome.StartedRole} session.";
                return RedirectToPage(new { chatId });

            case FamiliarPlanOutcomeStatus.Declined:
                TempData["StatusMessage"] = "The plan was declined. Nothing was created.";
                return RedirectToPage(new { chatId });

            case FamiliarPlanOutcomeStatus.AlreadyApproved:
                TempData["StatusMessage"] =
                    $"This plan was already approved; {outcome.CreatedTaskCount} task(s) came from it. Nothing was created again.";
                return RedirectToPage(new { chatId });

            case FamiliarPlanOutcomeStatus.AlreadyDeclined:
                TempData["StatusMessage"] = "This plan was already declined. Nothing was created.";
                return RedirectToPage(new { chatId });

            case FamiliarPlanOutcomeStatus.NotFound:
                return NotFound();
        }

        // Everything below refused and created nothing, so the plan is re-rendered in place with the
        // reason attached rather than the person being redirected away from what they were reading.
        PlanMessage = outcome.Status switch
        {
            FamiliarPlanOutcomeStatus.StaleToken =>
                "This plan changed since the page was loaded, so nothing was done. Reload and read it again.",

            FamiliarPlanOutcomeStatus.ProjectInactive =>
                "That project is no longer active, so nothing was created.",

            FamiliarPlanOutcomeStatus.ContextMoved =>
                "The project changed since this plan was drafted, so what you read is no longer what would be "
                + "created. Nothing was done — ask for a fresh plan.",

            FamiliarPlanOutcomeStatus.TaskAlreadyRunning =>
                "A session is already running on that task, so nothing was started. Nothing was created.",

            FamiliarPlanOutcomeStatus.NothingIncluded =>
                "Every item was excluded, so there was nothing to create. The plan is still here — include at "
                + "least one item, or decline it.",

            FamiliarPlanOutcomeStatus.ValidationFailed => outcome.ValidationMessage,

            _ => "The database was busy, so nothing was done. Nothing was changed — try again."
        };

        return await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();
    }

    // ---------------------------------------------------------------- deciding a handoff

    [BindProperty]
    public Guid HandoffId { get; set; }

    [BindProperty]
    public Guid HandoffToken { get; set; }

    /// <summary>True for Approve, false for Decline.</summary>
    [BindProperty]
    public bool ApproveHandoff { get; set; }

    /// <summary>
    /// The other end of the loop: the next session is started from the conversation, on the evidence
    /// of what the last one produced.
    ///
    /// Straight through <see cref="ISessionHandoffApprovalService"/> — the same transaction the task
    /// page uses, with the same fence and the same partial unique index behind it. Two doors to one
    /// decision; the rules live in one place.
    /// </summary>
    public async Task<IActionResult> OnPostDecideHandoffAsync(Guid chatId, CancellationToken cancellationToken)
    {
        var request = new SessionHandoffDecisionRequest(HandoffId, HandoffToken);

        var outcome = ApproveHandoff
            ? await handoffs.ApproveAsync(request, cancellationToken)
            : await handoffs.DeclineAsync(request, cancellationToken);

        TempData["StatusMessage"] = outcome.Status switch
        {
            SessionHandoffDecisionStatus.Approved =>
                $"Started a {outcome.Role} session. It will be picked up by a worker; ask me about it when it finishes.",

            SessionHandoffDecisionStatus.Declined =>
                "That step was declined. Nothing was started, and the task is no longer waiting on you.",

            SessionHandoffDecisionStatus.AlreadyApproved =>
                "That step was already approved, so nothing was started again.",

            SessionHandoffDecisionStatus.AlreadyDeclined =>
                "That step was already declined. Nothing was started.",

            SessionHandoffDecisionStatus.Superseded =>
                "Something newer happened on that task, so this decision no longer applies. Nothing was started.",

            SessionHandoffDecisionStatus.StaleHandoff =>
                "That decision changed since the page was loaded, so nothing was done. Reload and read it again.",

            SessionHandoffDecisionStatus.SessionAlreadyStarted =>
                "A session is already running on that task, so another was not started.",

            SessionHandoffDecisionStatus.TaskClosed =>
                "That task is closed, so nothing was started.",

            SessionHandoffDecisionStatus.ProjectInactive =>
                "That project is no longer active, so nothing was started.",

            SessionHandoffDecisionStatus.NotFound =>
                "That decision no longer exists. Nothing was started.",

            SessionHandoffDecisionStatus.DatabaseBusy =>
                "The database was busy, so nothing was done. Nothing was changed — try again.",

            _ => "Someone else decided that first, so nothing was started."
        };

        return RedirectToPage(new { chatId });
    }

    /// <summary>
    /// What a turn's state means, in the page's own words rather than the enum's. A failed turn says
    /// plainly that nothing was answered; it never speaks in the Familiar's voice.
    /// </summary>
    public static string StateLabel(FamiliarChatTurnState state) => state switch
    {
        FamiliarChatTurnState.Pending => "Queued",
        FamiliarChatTurnState.Generating => "Replying",
        FamiliarChatTurnState.Completed => "Familiar",
        _ => "Find Familiar"
    };

    public static string StateCss(FamiliarChatTurnState state) => state switch
    {
        FamiliarChatTurnState.Completed => "is-familiar",
        FamiliarChatTurnState.Failed => "is-system",
        _ => "is-pending"
    };
}
