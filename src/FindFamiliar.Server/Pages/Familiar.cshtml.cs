using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// The Familiar: one project, read out from what is recorded, and asked about.
///
/// <c>GET</c> writes nothing on any branch — a project you only look at stays untouched, and no
/// conversation row is created to make rendering tidier. Every write is an <c>OnPost</c> handler with
/// antiforgery, and there are exactly three: Send, Confirm and Dismiss.
///
/// The page derives nothing. Project state comes from <see cref="IProjectSnapshotService"/> and its
/// account from <see cref="FamiliarSummaryWriter"/>, so the Familiar and the Demiplane cannot develop
/// separate opinions about what a task's state is (ADR-0011). Conversation rows come from
/// <see cref="IFamiliarConversationService"/> already ordered and already project-filtered.
///
/// No reasoning provider is called from here, on any path. A <c>GET</c> of a project is a read.
/// </summary>
public sealed class FamiliarModel(
    IProjectSnapshotService snapshots,
    IFamiliarConversationService conversations,
    IFamiliarActionService actions) : PageModel
{
    /// <summary>The bounded project state. Null only when the project could not be read at all.</summary>
    public ProjectSnapshot? Snapshot { get; private set; }

    /// <summary>The deterministic, provider-free account of the project.</summary>
    public FamiliarProjectSummary? Summary { get; private set; }

    /// <summary>Null when nobody has spoken to this project yet — no row is created to avoid it.</summary>
    public FamiliarConversationView? Conversation { get; private set; }

    /// <summary>
    /// Set when the project exists but could not be read for an operational reason this application
    /// classifies. Always text authored here, never a database or provider message.
    /// </summary>
    public string? UnavailableDetail { get; private set; }

    /// <summary>
    /// True when the snapshot was built but is too large to send to a reasoning provider safely. The
    /// page still renders in full: the deterministic summary is complete, and it is the sending that
    /// is refused, not the reading.
    /// </summary>
    public bool SnapshotOverBudget { get; private set; }

    public bool IsArchived => Snapshot?.ProjectStatus == ProjectStatus.Archived;

    /// <summary>
    /// The message being composed. The only value bound from the send form — the project comes from
    /// the route and everything else is server-side, so a crafted post cannot address another
    /// project's conversation.
    /// </summary>
    [BindProperty]
    public string? Message { get; set; }

    /// <summary>Shown beside the textarea when a send was refused. Authored by the service.</summary>
    public string? SendValidationMessage { get; private set; }

    /// <summary>
    /// The proposal decision being submitted.
    ///
    /// Only the id and the token the rendered page carried are bound, plus the human's edits to a
    /// CreateTask's title and outcome. Kind, project, target task and observed revision are read
    /// server-side from the row, so a crafted post cannot choose an action or retarget one.
    /// </summary>
    [BindProperty]
    public FamiliarProposalDecisionInput Decision { get; set; } = new();

    /// <summary>Shown beside the proposal when a confirmation was refused.</summary>
    public string? DecisionValidationMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid projectId, CancellationToken cancellationToken) =>
        await LoadAsync(projectId, cancellationToken) ? Page() : NotFound();

    /// <summary>
    /// Reads everything the page renders. False means the project does not exist.
    ///
    /// This is the only path that touches the database on a render, and it writes nothing on any
    /// branch — a project you only look at stays untouched.
    /// </summary>
    private async Task<bool> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await snapshots.GetSnapshotAsync(projectId, cancellationToken);

        if (result.Outcome == ProjectSnapshotOutcome.ProjectNotFound)
        {
            return false;
        }

        if (result.Snapshot is null)
        {
            // The project exists but the read failed. Saying so beats a 500, for the reason
            // ProviderCapacitySnapshot.Faulted exists: an operational hiccup is a fact to report.
            UnavailableDetail = result.Detail;
            return true;
        }

        Snapshot = result.Snapshot;
        SnapshotOverBudget = result.Outcome == ProjectSnapshotOutcome.TooLarge;
        Summary = FamiliarSummaryWriter.Compose(result.Snapshot);
        Conversation = await conversations.GetAsync(projectId, cancellationToken);

        return true;
    }

    /// <summary>
    /// Sends a message and redirects.
    ///
    /// The handler contains no business logic: validation, the two-transaction append, the snapshot,
    /// the envelope bound and the provider call all belong to the service, so the same rules apply
    /// however a send arrives. Post/redirect/get, so a refresh after sending does not send again.
    /// </summary>
    public async Task<IActionResult> OnPostSendAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await conversations.SendAsync(projectId, Message ?? string.Empty, cancellationToken);

        if (result.Status == FamiliarSendStatus.ProjectNotFound)
        {
            return NotFound();
        }

        if (result.Status == FamiliarSendStatus.Invalid)
        {
            // Re-render in place so the person keeps what they typed and sees why it was refused.
            SendValidationMessage = result.ValidationMessage;

            if (!await LoadAsync(projectId, cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        if (result.Status == FamiliarSendStatus.DatabaseBusy)
        {
            // No competitor is claimed, because none has been established.
            TempData["StatusMessage"] =
                "The database was busy and your message was not sent. Nothing was changed — try again.";
        }

        return RedirectToPage(new { projectId });
    }

    public async Task<IActionResult> OnPostConfirmAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var outcome = await actions.ConfirmAsync(projectId, Decision.ToRequest(), cancellationToken);

        if (outcome.Status == FamiliarActionStatusOutcome.NotFound)
        {
            return NotFound();
        }

        if (outcome.Status == FamiliarActionStatusOutcome.ValidationFailed)
        {
            DecisionValidationMessage = outcome.ValidationMessage;
            return await LoadAsync(projectId, cancellationToken) ? Page() : NotFound();
        }

        TempData["StatusMessage"] = Describe(outcome);
        return RedirectToPage(new { projectId });
    }

    public async Task<IActionResult> OnPostDismissAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var outcome = await actions.DismissAsync(projectId, Decision.ToRequest(), cancellationToken);

        if (outcome.Status == FamiliarActionStatusOutcome.NotFound)
        {
            return NotFound();
        }

        TempData["StatusMessage"] = Describe(outcome);
        return RedirectToPage(new { projectId });
    }

    /// <summary>
    /// Wording for each confirmation outcome, from user-experience.md §4.
    ///
    /// The page never claims more than the service reported. In particular the fall-through claims
    /// no competing actor: race wording is reserved for the statuses that establish a real one, for
    /// the reason ADR-0011 recorded.
    /// </summary>
    public static string Describe(FamiliarActionOutcome outcome) => outcome.Status switch
    {
        FamiliarActionStatusOutcome.Confirmed when outcome.CreatedTaskId is not null =>
            "Created the task. Nothing is running on it yet.",

        FamiliarActionStatusOutcome.Confirmed =>
            "Summoned a planner session. A worker may claim it automatically.",

        FamiliarActionStatusOutcome.Dismissed =>
            "Dismissed. Nothing was created.",

        FamiliarActionStatusOutcome.AlreadyConfirmed =>
            "That proposal was already decided, so nothing new was created.",

        FamiliarActionStatusOutcome.AlreadyDismissed =>
            "That proposal was already dismissed, so nothing was created.",

        FamiliarActionStatusOutcome.StaleToken =>
            "This view was out of date, so nothing was changed. Review the current proposal and try again.",

        FamiliarActionStatusOutcome.TaskAlreadyRunning =>
            "That task already has a running session, so another cannot begin. Nothing was started.",

        FamiliarActionStatusOutcome.ProjectInactive =>
            "This project is no longer active, so nothing was created.",

        FamiliarActionStatusOutcome.ContextMoved =>
            "The project's context changed after you reviewed this, so nothing was created. Ask again for a current proposal.",

        FamiliarActionStatusOutcome.TargetTaskInvalid =>
            "That task is no longer part of this project, so nothing was started.",

        FamiliarActionStatusOutcome.DatabaseBusy =>
            "The database was busy and this was not applied. Nothing was created and nobody else confirmed it — try again.",

        _ => "This could not be completed, and nothing was changed."
    };

    /// <summary>
    /// Why a pending proposal cannot be confirmed right now, or null when it can.
    ///
    /// Derived at render time from current state and never stored. A proposal does not rot on a
    /// clock; it becomes invalid when the world it described changes, and the page says which part
    /// changed. The confirming transaction re-checks all of this anyway — this exists so a person is
    /// not offered a button that is certain to fail.
    /// </summary>
    public string? StaleReason(FamiliarProposalView proposal)
    {
        if (Snapshot is null)
        {
            return null;
        }

        if (Snapshot.ProjectStatus != ProjectStatus.Active)
        {
            return "This project is no longer active, so nothing can be created from here.";
        }

        if (proposal.Kind == FamiliarActionKind.CreateTask
            && proposal.ObservedContextRevision != Snapshot.ContextRevision)
        {
            return "The project's context changed after this was proposed, so I have not offered to create it. "
                   + "Ask again and I'll use what's current.";
        }

        if (proposal.Kind == FamiliarActionKind.StartPlanner)
        {
            var target = Snapshot.Tasks.FirstOrDefault(task => task.TaskId == proposal.TargetTaskId);

            if (target is null)
            {
                // Absent from the snapshot is not proof of absence — the task list is capped — so
                // this says what is actually known rather than claiming the task is gone.
                return null;
            }

            if (target.DisplayState == TaskDisplayState.Running)
            {
                return "That task already has a running session, so another cannot begin.";
            }
        }

        return null;
    }

    /// <summary>Who a message is attributed to, in the page's own words rather than the enum's.</summary>
    public static string AuthorLabel(FamiliarMessageAuthor author) => author switch
    {
        FamiliarMessageAuthor.Human => "You",
        FamiliarMessageAuthor.Familiar => "Familiar",
        _ => "Find Familiar"
    };

    public static string AuthorCss(FamiliarMessageAuthor author) => author switch
    {
        FamiliarMessageAuthor.Human => "is-human",
        FamiliarMessageAuthor.Familiar => "is-familiar",
        _ => "is-system"
    };

    /// <summary>
    /// What a proposal would do, stated by this application. The provider's own words for it are not
    /// used here: a description of an effect is a claim about this system, and this system makes it.
    /// </summary>
    public static string ActionHeading(FamiliarActionKind kind) => kind switch
    {
        FamiliarActionKind.CreateTask => "Create a task",
        _ => "Start a Planner session"
    };

    public static string ActionEffect(FamiliarActionKind kind) => kind switch
    {
        FamiliarActionKind.CreateTask =>
            "One new task in this project, ready to be picked up. No session starts and no worker is notified.",

        _ => "One Planner session starts on the task below. An eligible worker may claim it automatically."
    };
}

/// <summary>
/// Everything a proposal decision may carry from the browser.
///
/// The identifier and the token the human reviewed, plus their edits to a CreateTask's two editable
/// fields. Nothing else is bindable — no project id, no conversation id, no action kind, no target
/// task, no status, no created id — so a crafted post can only ever decide the proposal it names,
/// and only in the way the row already permits.
/// </summary>
public sealed class FamiliarProposalDecisionInput
{
    public Guid ProposalId { get; set; }

    public Guid ExpectedConcurrencyToken { get; set; }

    /// <summary>The human's title for a CreateTask. Ignored for any other kind.</summary>
    public string? Title { get; set; }

    /// <summary>The human's requested outcome for a CreateTask. Ignored for any other kind.</summary>
    public string? RequestedOutcome { get; set; }

    public FamiliarActionRequest ToRequest() =>
        new(ProposalId, ExpectedConcurrencyToken, Title, RequestedOutcome);
}
