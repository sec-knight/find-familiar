using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Demiplane;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// The Demiplane: one project's command surface.
///
/// The page derives nothing. Every state, reason and summary comes from
/// <see cref="IDemiplaneProjectionService"/>, so the desktop map and the mobile trail render the same
/// facts and neither can develop its own idea of what "waiting" means.
///
/// Approval and decline reuse the Sprint 09 service unchanged. This page adds no second approval
/// path, no auto-start, and no way to begin work that a human did not ask for.
/// </summary>
public sealed class DemiplaneModel(
    IDemiplaneProjectionService projection,
    ISessionHandoffApprovalService handoffApproval) : PageModel
{
    /// <summary>
    /// How often the page asks the browser to reload while work is in flight. Bounded and only
    /// applied when something is actually running or waiting — a settled project never refreshes.
    /// </summary>
    public const int ActiveRefreshSeconds = 30;

    public DemiplaneProjection? Projection { get; private set; }

    /// <summary>The task whose detail panel is open, if any.</summary>
    public DemiplaneTask? SelectedTask { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid? TaskId { get; set; }

    [BindProperty]
    public HandoffDecisionInput Decision { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await handoffApproval.ApproveAsync(
            new SessionHandoffDecisionRequest(Decision.HandoffId, Decision.ExpectedConcurrencyToken),
            cancellationToken);

        if (outcome.Status == SessionHandoffDecisionStatus.NotFound)
        {
            return NotFound();
        }

        TempData["StatusMessage"] = Describe(outcome);
        return RedirectToPage(new { id, taskId = outcome.TaskId });
    }

    public async Task<IActionResult> OnPostDeclineAsync(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await handoffApproval.DeclineAsync(
            new SessionHandoffDecisionRequest(Decision.HandoffId, Decision.ExpectedConcurrencyToken),
            cancellationToken);

        if (outcome.Status == SessionHandoffDecisionStatus.NotFound)
        {
            return NotFound();
        }

        TempData["StatusMessage"] = Describe(outcome);
        return RedirectToPage(new { id, taskId = outcome.TaskId });
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        Projection = await projection.GetProjectionAsync(id, cancellationToken);
        if (Projection is null)
        {
            return false;
        }

        // A task id from the query string is only honoured when it belongs to this project, so a
        // guessed or stale id cannot pull another project's task into view.
        SelectedTask = TaskId is { } taskId
            ? Projection.Tasks.FirstOrDefault(task => task.TaskId == taskId)
            : null;

        return true;
    }

    /// <summary>
    /// Wording for each Sprint 09 outcome. The page never claims more than the service reported.
    /// Public so the wording itself can be asserted directly, alongside the other display helpers.
    /// </summary>
    public static string Describe(SessionHandoffDecisionOutcome outcome)
    {
        var role = outcome.Role?.ToString().ToLowerInvariant() ?? "next";

        return outcome.Status switch
        {
            SessionHandoffDecisionStatus.Approved =>
                $"Summoned a {role} session. A worker may claim it automatically.",

            SessionHandoffDecisionStatus.Declined =>
                $"Declined the {role} step. Nothing was started.",

            SessionHandoffDecisionStatus.AlreadyApproved =>
                $"That {role} step was already approved, so nothing new was started.",

            SessionHandoffDecisionStatus.AlreadyDeclined =>
                $"That {role} step was already declined.",

            SessionHandoffDecisionStatus.Superseded =>
                "That step was replaced by a newer one on this task. Review the current proposal.",

            SessionHandoffDecisionStatus.StaleHandoff =>
                "This view was out of date, so nothing was changed. Review the current proposal and try again.",

            SessionHandoffDecisionStatus.SessionAlreadyStarted =>
                "This task already has a running session, so another cannot begin. Nothing was started.",

            SessionHandoffDecisionStatus.TaskClosed =>
                "This task is complete, so no further work was started.",

            SessionHandoffDecisionStatus.ProjectInactive =>
                "This project is no longer active, so nothing was started.",

            SessionHandoffDecisionStatus.DatabaseBusy =>
                "The database was busy and the request was not applied. Nothing was changed — try again.",

            // The generic fall-through covers foreign-key violations, disk and I/O errors, and other
            // EF or SQLite faults where no competing decision exists. Claiming another change won
            // would send the user looking for an actor who was never there. Race wording above is
            // reserved for the statuses that establish a real competitor.
            _ => "This step could not be completed, and nothing was changed."
        };
    }

    /// <summary>The visual marker for a state. Never the only signal — every marker is paired with text.</summary>
    public static string MarkerFor(TaskDisplayState state) => state switch
    {
        TaskDisplayState.Succeeded => "✓",
        TaskDisplayState.Failed => "✕",
        TaskDisplayState.Running => "⟳",
        TaskDisplayState.NeedsAttention => "!",
        TaskDisplayState.Blocked => "⏸",
        TaskDisplayState.Waiting => "◌",
        TaskDisplayState.Cancelled => "⊘",
        _ => "○"
    };

    public static string LabelFor(TaskDisplayState state) => state switch
    {
        TaskDisplayState.NotStarted => "Not started",
        TaskDisplayState.NeedsAttention => "Needs attention",
        _ => state.ToString()
    };

    public static string CssFor(TaskDisplayState state) => state switch
    {
        TaskDisplayState.Succeeded => "is-succeeded",
        TaskDisplayState.Failed => "is-failed",
        TaskDisplayState.Running => "is-running",
        TaskDisplayState.NeedsAttention => "is-attention",
        TaskDisplayState.Blocked => "is-blocked",
        TaskDisplayState.Waiting => "is-waiting",
        TaskDisplayState.Cancelled => "is-cancelled",
        _ => "is-not-started"
    };

    public static string StepMarker(TaskChainStep step)
    {
        if (step.IsProposed)
        {
            return "◇";
        }

        return step.Status switch
        {
            AgentSessionStatus.Completed => "✓",
            AgentSessionStatus.Cancelled => "✕",
            AgentSessionStatus.Started => "⟳",
            _ => "○"
        };
    }

    public static string StepLabel(TaskChainStep step)
    {
        if (step.IsProposed)
        {
            return "proposed, not started";
        }

        return step.Status switch
        {
            AgentSessionStatus.Completed => "completed",
            AgentSessionStatus.Cancelled => "ended early",
            AgentSessionStatus.Started => "running",
            _ => "unknown"
        };
    }
}

/// <summary>
/// Only the handoff identifier and the token the human reviewed are bound. Every other field is
/// derived server-side, so a crafted post cannot choose a role or a state.
/// </summary>
public sealed class HandoffDecisionInput
{
    public Guid HandoffId { get; set; }

    public Guid ExpectedConcurrencyToken { get; set; }
}
