using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages.Talk;

public sealed class DetailsModel(
    IConversationDetailsService details,
    IWorkProposalService proposals,
    IWorkApprovalService approval) : PageModel
{
    public ConversationDetailsDocument? Document { get; private set; }

    /// <summary>
    /// Revise, approve and reject are only offered while the proposal is genuinely actionable.
    /// The services re-check this; the page only decides what to render.
    /// </summary>
    public bool IsPending =>
        Document is { Status: ConversationStatus.AwaitingApproval, Proposal.Status: WorkProposalStatus.Pending };

    [BindProperty]
    public ProposalRevisionInput Revision { get; set; } = new();

    /// <summary>
    /// The token the user's rendered page carried. Bound separately from the revision form so a
    /// reject or approve post cannot inherit a token from an unrelated form.
    /// </summary>
    [BindProperty]
    public Guid ActionConcurrencyToken { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        Revision = new ProposalRevisionInput
        {
            ProjectId = Document!.Proposal.ProjectId,
            Title = Document.Proposal.Title,
            RequestedOutcome = Document.Proposal.RequestedOutcome,
            ConcurrencyToken = Document.Proposal.ConcurrencyToken
        };

        return Page();
    }

    public async Task<IActionResult> OnPostReviseAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(Revision, nameof(Revision)))
        {
            return await ReturnPageAsync(id, cancellationToken);
        }

        var outcome = await proposals.ReviseAsync(
            new ProposalRevisionRequest(
                id,
                Revision.ConcurrencyToken,
                Revision.ProjectId,
                Revision.Title,
                Revision.RequestedOutcome),
            cancellationToken);

        return await HandleProposalOutcomeAsync(
            id,
            outcome,
            "Revised the proposal. Nothing has been created or started yet.",
            "Revision.",
            cancellationToken);
    }

    public async Task<IActionResult> OnPostRefreshContextAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        var outcome = await proposals.RefreshContextAsync(
            new ProposalActionRequest(id, ActionConcurrencyToken),
            cancellationToken);

        return await HandleProposalOutcomeAsync(
            id,
            outcome,
            "Refreshed the observed project context. Review the proposal again before approving.",
            "Revision.",
            cancellationToken);
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        var outcome = await proposals.RejectAsync(
            new ProposalActionRequest(id, ActionConcurrencyToken),
            cancellationToken);

        return await HandleProposalOutcomeAsync(
            id,
            outcome,
            "Rejected the proposal. No task and no session were created.",
            "Revision.",
            cancellationToken);
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        var outcome = await approval.ApproveAsync(
            new WorkApprovalRequest(id, ActionConcurrencyToken),
            cancellationToken);

        switch (outcome.Status)
        {
            case WorkApprovalStatus.Approved:
                TempData["StatusMessage"] =
                    "Approved. Created one Ready task and one Started Planner session. An eligible worker will pick it up automatically.";
                return RedirectToPage(new { id });

            case WorkApprovalStatus.AlreadyApproved:
                // Replay is not an error: the user is shown the work the first approval created.
                TempData["StatusMessage"] =
                    "This conversation was already approved. It still links to the one task and one session it created.";
                return RedirectToPage(new { id });

            case WorkApprovalStatus.NotFound:
                return NotFound();

            case WorkApprovalStatus.AlreadyRejected:
                ModelState.AddModelError(
                    string.Empty,
                    "This proposal was rejected. A rejected proposal cannot be approved.");
                return await ReturnPageAsync(id, cancellationToken);

            case WorkApprovalStatus.StaleProposal:
                ModelState.AddModelError(
                    string.Empty,
                    "The proposal changed after this page was loaded. Review the current proposal, then approve it.");
                return await ReturnPageAsync(id, cancellationToken);

            case WorkApprovalStatus.StaleContext:
                ModelState.AddModelError(
                    string.Empty,
                    "The project's context changed after you reviewed this proposal. Refresh the context and review it again before approving. No work was created.");
                return await ReturnPageAsync(id, cancellationToken);

            // The generic fall-through. WorkApprovalService reaches it when a database fault rolled
            // the approval back and the conversation is still Pending — so no competing approval was
            // established. Foreign-key violations, disk and I/O errors all land here. A genuine lost
            // race reports AlreadyApproved or AlreadyRejected instead, and keeps its race wording.
            case WorkApprovalStatus.Conflict:
                ModelState.AddModelError(
                    string.Empty,
                    "This step could not be completed, and nothing was changed. Review the current state and try again.");
                return await ReturnPageAsync(id, cancellationToken);

            case WorkApprovalStatus.DatabaseBusy:
                ModelState.AddModelError(
                    string.Empty,
                    "The database was busy and this approval was not applied. Nothing was created and nobody else approved it — try again.");
                return await ReturnPageAsync(id, cancellationToken);

            case WorkApprovalStatus.ValidationFailed:
            default:
                foreach (var (field, message) in outcome.ValidationErrors ?? new Dictionary<string, string>())
                {
                    ModelState.AddModelError($"Revision.{field}", message);
                }

                return await ReturnPageAsync(id, cancellationToken);
        }
    }

    private async Task<IActionResult> HandleProposalOutcomeAsync(
        Guid id,
        ProposalActionOutcome outcome,
        string successMessage,
        string fieldPrefix,
        CancellationToken cancellationToken)
    {
        switch (outcome.Status)
        {
            case ProposalActionStatus.Success:
                TempData["StatusMessage"] = successMessage;
                return RedirectToPage(new { id });

            case ProposalActionStatus.NotFound:
                return NotFound();

            case ProposalActionStatus.AlreadyTerminal:
                ModelState.AddModelError(
                    string.Empty,
                    "This conversation is already approved or rejected and can no longer be changed.");
                return await ReturnPageAsync(id, cancellationToken);

            case ProposalActionStatus.StaleProposal:
                ModelState.AddModelError(
                    string.Empty,
                    "The proposal changed after this page was loaded, so your submission was not applied. Review the current proposal and try again.");
                return await ReturnPageAsync(id, cancellationToken);

            case ProposalActionStatus.Conflict:
                ModelState.AddModelError(
                    string.Empty,
                    "Another request changed this conversation at the same time. Nothing was changed. Review the current state and try again.");
                return await ReturnPageAsync(id, cancellationToken);

            case ProposalActionStatus.ValidationFailed:
            default:
                foreach (var (field, message) in outcome.ValidationErrors ?? new Dictionary<string, string>())
                {
                    ModelState.AddModelError($"{fieldPrefix}{field}", message);
                }

                return await ReturnPageAsync(id, cancellationToken);
        }
    }

    private async Task<IActionResult> ReturnPageAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : NotFound();

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        Document = await details.GetAsync(id, cancellationToken);
        return Document is not null;
    }
}

public sealed class ProposalRevisionInput
{
    [Required(ErrorMessage = "Select the project this work belongs to.")]
    [Display(Name = "Project")]
    public Guid? ProjectId { get; set; }

    [Required(ErrorMessage = "A task title is required.")]
    [StringLength(WorkProposal.MaxTitleLength, ErrorMessage = "The task title must be {1} characters or fewer.")]
    [Display(Name = "Task title")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "A requested outcome is required.")]
    [StringLength(
        WorkProposal.MaxRequestedOutcomeLength,
        ErrorMessage = "The requested outcome must be {1} characters or fewer.")]
    [Display(Name = "Requested outcome")]
    public string RequestedOutcome { get; set; } = string.Empty;

    public Guid ConcurrencyToken { get; set; }
}
