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
    IContextProjectionService contextProjection) : PageModel
{
    public TaskContextDocument? Document { get; private set; }

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

        var task = await dbContext.Tasks
            .Include(candidate => candidate.Project)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (task is null)
        {
            return NotFound();
        }

        var hasStartedSession = await dbContext.AgentSessions.AnyAsync(
            candidate => candidate.TaskId == id && candidate.Status == AgentSessionStatus.Started,
            cancellationToken);

        if (hasStartedSession)
        {
            ModelState.AddModelError(
                "NewSession.Role",
                "This task already has a Started session. Capture its result or cancel it before starting another.");
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var startedUtc = DateTime.UtcNow;

        task.Project.IncrementContextRevision();

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = NewSession.Role,
            Provider = NullIfWhiteSpace(NewSession.Provider),
            ExternalSessionReference = NullIfWhiteSpace(NewSession.ExternalSessionReference),
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = task.Project.ContextRevision,
            StartedUtc = startedUtc
        };

        dbContext.AgentSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Started a {session.Role.ToString().ToLowerInvariant()} session. Copy the generated Markdown into a new AI conversation.";
        return RedirectToPage(new { id, sessionId = session.Id });
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

        var session = await dbContext.AgentSessions
            .Include(candidate => candidate.Task)
            .ThenInclude(task => task.Project)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == SessionResult.SessionId && candidate.TaskId == id,
                cancellationToken);

        if (session is null)
        {
            return NotFound();
        }

        if (session.Status != AgentSessionStatus.Started)
        {
            ModelState.AddModelError(
                "SessionResult.SessionId",
                "That session is no longer Started. A result can only be captured once for a Started session.");
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var artifactKind = session.Role switch
        {
            AgentSessionRole.Planner => ContextEntryKind.Plan,
            AgentSessionRole.Implementer => ContextEntryKind.Implementation,
            AgentSessionRole.Reviewer => ContextEntryKind.Review,
            _ => throw new InvalidOperationException($"Unmapped agent session role '{session.Role}'.")
        };

        var roleLabel = session.Role.ToString().ToLowerInvariant();
        var capturedUtc = DateTime.UtcNow;

        dbContext.ContextEntries.AddRange(
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.Prompt,
                Title = $"{session.Role} session prompt",
                Content = SessionResult.Prompt.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.RawOutput,
                Title = $"{session.Role} raw output",
                Content = SessionResult.RawOutput.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.Summary,
                Title = $"{session.Role} summary",
                Content = SessionResult.Summary.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = session.Task.ProjectId,
                TaskId = session.TaskId,
                SourceSessionId = session.Id,
                Kind = artifactKind,
                Title = SessionResult.ArtifactTitle.Trim(),
                Content = SessionResult.ArtifactContent.Trim(),
                State = ContextEntryState.Active,
                CreatedUtc = capturedUtc
            });

        session.Status = AgentSessionStatus.Completed;
        session.CompletedUtc = capturedUtc;
        session.Task.UpdatedUtc = capturedUtc;
        session.Task.Project.IncrementContextRevision();

        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Captured the {roleLabel} result and completed the session.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(SessionCancellation, nameof(SessionCancellation)))
        {
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var session = await dbContext.AgentSessions
            .Include(candidate => candidate.Task)
            .ThenInclude(task => task.Project)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == SessionCancellation.SessionId && candidate.TaskId == id,
                cancellationToken);

        if (session is null)
        {
            return NotFound();
        }

        if (session.Status != AgentSessionStatus.Started)
        {
            ModelState.AddModelError(
                "SessionCancellation.SessionId",
                "That session is no longer Started. Only a Started session can be cancelled.");
            await LoadContextAsync(id, cancellationToken);
            return Page();
        }

        var cancelledUtc = DateTime.UtcNow;
        var role = session.Role;

        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = session.Task.ProjectId,
            TaskId = session.TaskId,
            SourceSessionId = session.Id,
            Kind = ContextEntryKind.Handoff,
            Title = $"{role} session cancelled",
            Content = SessionCancellation.Reason.Trim(),
            State = ContextEntryState.Active,
            CreatedUtc = cancelledUtc
        });

        session.Status = AgentSessionStatus.Cancelled;
        session.CompletedUtc = cancelledUtc;
        session.Task.UpdatedUtc = cancelledUtc;
        session.Task.Project.IncrementContextRevision();

        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Cancelled the {role.ToString().ToLowerInvariant()} session.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadContextAsync(Guid id, CancellationToken cancellationToken)
    {
        Document = await contextProjection.GetTaskContextAsync(id, cancellationToken);
        return Document is not null;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
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
