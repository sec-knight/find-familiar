using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Pages.Tasks;

public sealed class DetailsModel(
    FamiliarDbContext dbContext,
    IContextProjectionService contextProjection,
    ISessionResultCaptureService resultCapture,
    ISessionCancellationService cancellation,
    IWorkflowDispatchService workflowDispatch,
    ISessionHandoffApprovalService handoffApproval) : PageModel
{
    public TaskContextDocument? Document { get; private set; }

    /// <summary>
    /// The task's current proposed next step, if a human has not decided on it yet. Display state
    /// only: nothing about execution consults it.
    /// </summary>
    public SessionHandoff? PendingHandoff { get; private set; }

    public IReadOnlyList<AgentSessionDocument> StartedSessions =>
        Document?.Sessions.Where(session => session.Status == AgentSessionStatus.Started).ToList()
            ?? new List<AgentSessionDocument>();

    public IReadOnlyList<AgentSessionRole> AgentSessionRoles { get; } = Enum.GetValues<AgentSessionRole>();

    public IReadOnlyList<ContextEntryKind> ContextEntryKinds { get; } = Enum.GetValues<ContextEntryKind>();

    public IReadOnlyList<TaskStatus> TaskStatuses { get; } = Enum.GetValues<TaskStatus>();

    [BindProperty]
    public NewAgentSessionInput NewSession { get; set; } = new();

    [BindProperty]
    public NewTaskContextEntryInput NewContextEntry { get; set; } = new();

    [BindProperty]
    public SessionResultInput SessionResult { get; set; } = new();

    [BindProperty]
    public SessionCancellationInput SessionCancellation { get; set; } = new();

    [BindProperty]
    public TaskStatus NewTaskStatus { get; set; }

    [BindProperty]
    public SessionHandoffDecisionInput HandoffDecision { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, Guid? sessionId, CancellationToken cancellationToken)
    {
        if (!await LoadContextAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (sessionId.HasValue && Document!.Sessions.Any(session => session.Id == sessionId.Value))
        {
            NewContextEntry.SourceSessionId = sessionId;

            var startedSession = Document.Sessions.FirstOrDefault(
                session => session.Id == sessionId.Value && session.Status == AgentSessionStatus.Started);
            if (startedSession is not null)
            {
                SessionResult.SessionId = startedSession.Id;
                SessionResult.Prompt = SessionAssignmentMarkdownRenderer.RenderRolePrompt(startedSession.Role, Document);
            }
        }

        NewTaskStatus = Document!.Task.Status;
        return Page();
    }

    public async Task<IActionResult> OnPostStartSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(NewSession, nameof(NewSession)))
        {
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var outcome = await workflowDispatch.StartSessionForTaskAsync(
            id,
            NewSession.Role,
            NewSession.Provider,
            NewSession.ExternalSessionReference,
            DateTime.UtcNow,
            cancellationToken);

        switch (outcome.Status)
        {
            case StartSessionStatus.Started:
                var session = outcome.Session!;
                TempData["StatusMessage"] = $"Started a {session.Role.ToString().ToLowerInvariant()} session. Copy the generated Markdown into a new AI conversation.";
                return RedirectToPage(new { id, sessionId = session.Id });

            case StartSessionStatus.NotFound:
                return NotFound();

            case StartSessionStatus.AlreadyStarted:
            default:
                ModelState.AddModelError(
                    "NewSession.Role",
                    "This task already has a Started session. Capture its result or cancel it before starting another.");
                await LoadContextAsync(id, cancellationToken);
                return Page();
        }
    }

    public async Task<IActionResult> OnPostApproveHandoffAsync(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await handoffApproval.ApproveAsync(
            new SessionHandoffDecisionRequest(HandoffDecision.HandoffId, HandoffDecision.ExpectedConcurrencyToken),
            cancellationToken);

        if (outcome.Status == SessionHandoffDecisionStatus.NotFound)
        {
            return NotFound();
        }

        TempData["StatusMessage"] = DescribeDecision(outcome, approving: true);

        return outcome.Status == SessionHandoffDecisionStatus.Approved
            ? RedirectToPage(new { id, sessionId = outcome.SessionId })
            : RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeclineHandoffAsync(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await handoffApproval.DeclineAsync(
            new SessionHandoffDecisionRequest(HandoffDecision.HandoffId, HandoffDecision.ExpectedConcurrencyToken),
            cancellationToken);

        if (outcome.Status == SessionHandoffDecisionStatus.NotFound)
        {
            return NotFound();
        }

        TempData["StatusMessage"] = DescribeDecision(outcome, approving: false);
        return RedirectToPage(new { id });
    }

    private static string DescribeDecision(SessionHandoffDecisionOutcome outcome, bool approving)
    {
        var role = outcome.Role?.ToString().ToLowerInvariant() ?? "next";

        return outcome.Status switch
        {
            SessionHandoffDecisionStatus.Approved =>
                $"Started a {role} session. A worker may claim it automatically.",

            SessionHandoffDecisionStatus.Declined =>
                $"Declined the {role} step. Nothing was started.",

            SessionHandoffDecisionStatus.AlreadyApproved =>
                $"That {role} step was already approved, so nothing new was started.",

            SessionHandoffDecisionStatus.AlreadyDeclined =>
                $"That {role} step was already declined.",

            SessionHandoffDecisionStatus.Superseded =>
                "That step was replaced by a newer one on this task. Review the current proposal.",

            SessionHandoffDecisionStatus.StaleHandoff =>
                "This page was out of date, so nothing was changed. Review the current proposal and try again.",

            SessionHandoffDecisionStatus.SessionAlreadyStarted =>
                "This task already has a Started session, so another cannot begin. Capture or cancel it first.",

            SessionHandoffDecisionStatus.TaskClosed =>
                "This task is Completed, so no further work was started.",

            SessionHandoffDecisionStatus.ProjectInactive =>
                "This project is no longer active, so nothing was started.",

            SessionHandoffDecisionStatus.DatabaseBusy =>
                "The database was busy and this decision was not applied. Nothing changed — try again.",

            _ => approving
                ? "Another change reached this step first, so nothing was started."
                : "Another change reached this step first, so nothing was declined."
        };
    }

    public async Task<IActionResult> OnPostCreateContextEntryAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(NewContextEntry, nameof(NewContextEntry)))
        {
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var task = await dbContext.Tasks
            .Include(candidate => candidate.Project)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (task is null)
        {
            return NotFound();
        }

        if (NewContextEntry.SourceSessionId.HasValue)
        {
            var belongsToTask = await dbContext.AgentSessions.AnyAsync(
                session => session.Id == NewContextEntry.SourceSessionId.Value && session.TaskId == id,
                cancellationToken);

            if (!belongsToTask)
            {
                ModelState.AddModelError("NewContextEntry.SourceSessionId", "The selected session does not belong to this task.");
                await LoadContextAsync(id, cancellationToken);
                return Page();
            }
        }

        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = task.ProjectId,
            TaskId = task.Id,
            SourceSessionId = NewContextEntry.SourceSessionId,
            Kind = NewContextEntry.Kind,
            Title = NewContextEntry.Title.Trim(),
            Content = NewContextEntry.Content.Trim(),
            State = ContextEntryState.Active,
            CreatedUtc = DateTime.UtcNow
        };

        task.Project.IncrementContextRevision();
        dbContext.ContextEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Recorded {entry.Kind.ToString().ToLowerInvariant()} context for the next session.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks
            .Include(candidate => candidate.Project)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (task is null)
        {
            return NotFound();
        }

        task.Status = NewTaskStatus;
        task.UpdatedUtc = DateTime.UtcNow;
        task.Project.IncrementContextRevision();
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Task status changed to {task.Status}.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCaptureSessionResultAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(SessionResult, nameof(SessionResult)))
        {
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var outcome = await resultCapture.CaptureAsync(
            new SessionResultCaptureRequest(
                id,
                SessionResult.SessionId ?? Guid.Empty,
                SessionResult.Prompt,
                SessionResult.RawOutput,
                SessionResult.Summary,
                SessionResult.ArtifactTitle,
                SessionResult.ArtifactContent),
            cancellationToken);

        switch (outcome.Status)
        {
            case SessionResultCaptureStatus.Success:
                var roleLabel = outcome.Role!.Value.ToString().ToLowerInvariant();
                TempData["StatusMessage"] = $"Captured the {roleLabel} result and completed the session.";
                return RedirectToPage(new { id });

            case SessionResultCaptureStatus.NotFound:
                return NotFound();

            case SessionResultCaptureStatus.NotStarted:
                ModelState.AddModelError(
                    "SessionResult.SessionId",
                    "That session is no longer Started. A result can only be captured once for a Started session.");
                await LoadContextAsync(id, cancellationToken);
                return Page();

            case SessionResultCaptureStatus.ValidationFailed:
            default:
                foreach (var (field, message) in outcome.ValidationErrors ?? new Dictionary<string, string>())
                {
                    ModelState.AddModelError($"SessionResult.{field}", message);
                }

                await LoadContextAsync(id, cancellationToken);
                return Page();
        }
    }

    public async Task<IActionResult> OnPostCancelSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(SessionCancellation, nameof(SessionCancellation)))
        {
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var outcome = await cancellation.CancelAsync(
            new SessionCancellationRequest(id, SessionCancellation.SessionId ?? Guid.Empty, SessionCancellation.Reason),
            cancellationToken);

        switch (outcome.Status)
        {
            case SessionCancellationStatus.Success:
                TempData["StatusMessage"] = $"Cancelled the {outcome.Role!.Value.ToString().ToLowerInvariant()} session.";
                return RedirectToPage(new { id });

            case SessionCancellationStatus.NotFound:
                return NotFound();

            case SessionCancellationStatus.NotStarted:
                ModelState.AddModelError(
                    "SessionCancellation.SessionId",
                    "That session is no longer Started. Only a Started session can be cancelled.");
                await LoadContextAsync(id, cancellationToken);
                return Page();

            case SessionCancellationStatus.ValidationFailed:
            default:
                foreach (var (field, message) in outcome.ValidationErrors ?? new Dictionary<string, string>())
                {
                    ModelState.AddModelError($"SessionCancellation.{field}", message);
                }

                await LoadContextAsync(id, cancellationToken);
                return Page();
        }
    }

    private async Task<bool> LoadContextAsync(Guid id, CancellationToken cancellationToken)
    {
        Document = await contextProjection.GetTaskContextAsync(id, cancellationToken);
        if (Document is null)
        {
            return false;
        }

        PendingHandoff = await dbContext.SessionHandoffs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                handoff => handoff.TaskId == id && handoff.Status == SessionHandoffStatus.Pending,
                cancellationToken);

        return true;
    }
}

/// <summary>
/// Only the identifier and the reviewed token are bound. Status, kind, proposed role, created session
/// and timestamps are all derived server-side, so a crafted post cannot set them.
/// </summary>
public sealed class SessionHandoffDecisionInput
{
    public Guid HandoffId { get; set; }

    public Guid ExpectedConcurrencyToken { get; set; }
}

public sealed class NewAgentSessionInput
{
    [Display(Name = "Role")]
    public AgentSessionRole Role { get; set; } = AgentSessionRole.Planner;

    [StringLength(120)]
    [Display(Name = "Provider (optional)")]
    public string? Provider { get; set; }

    [StringLength(500)]
    [Display(Name = "External session reference (optional)")]
    public string? ExternalSessionReference { get; set; }
}

public sealed class NewTaskContextEntryInput
{
    [Display(Name = "Kind")]
    public ContextEntryKind Kind { get; set; } = ContextEntryKind.Handoff;

    [Required]
    [StringLength(200)]
    [Display(Name = "Title")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(12_000)]
    [Display(Name = "Content")]
    public string Content { get; set; } = string.Empty;

    [Display(Name = "Source session")]
    public Guid? SourceSessionId { get; set; }
}

public sealed class SessionResultInput
{
    [Required(ErrorMessage = "Select the Started session this result belongs to.")]
    [Display(Name = "Started session")]
    public Guid? SessionId { get; set; }

    [Required]
    [StringLength(12_000)]
    [Display(Name = "Exact prompt used")]
    public string Prompt { get; set; } = string.Empty;

    [Required]
    [StringLength(12_000)]
    [Display(Name = "Raw output (a bounded excerpt of the response, not the full transcript)")]
    public string RawOutput { get; set; } = string.Empty;

    [Required]
    [StringLength(4_000)]
    [Display(Name = "Summary")]
    public string Summary { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    [Display(Name = "Result title")]
    public string ArtifactTitle { get; set; } = string.Empty;

    [Required]
    [StringLength(12_000)]
    [Display(Name = "Result content")]
    public string ArtifactContent { get; set; } = string.Empty;
}

public sealed class SessionCancellationInput
{
    [Required(ErrorMessage = "Select the Started session to cancel.")]
    [Display(Name = "Started session")]
    public Guid? SessionId { get; set; }

    [Required(ErrorMessage = "A cancellation reason is required.")]
    [StringLength(2_000)]
    [Display(Name = "Cancellation reason")]
    public string Reason { get; set; } = string.Empty;
}
