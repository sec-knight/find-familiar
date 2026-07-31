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

    public IReadOnlyList<AgentSessionRole> AgentSessionRoles { get; } = Enum.GetValues<AgentSessionRole>();

    public IReadOnlyList<ContextEntryKind> ContextEntryKinds { get; } = Enum.GetValues<ContextEntryKind>();

    public IReadOnlyList<TaskStatus> TaskStatuses { get; } = Enum.GetValues<TaskStatus>();

    [BindProperty]
    public NewAgentSessionInput NewSession { get; set; } = new();

    [BindProperty]
    public NewTaskContextEntryInput NewContextEntry { get; set; } = new();

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
        }

        NewTaskStatus = Document!.Task.Status;
        return Page();
    }

    public async Task<IActionResult> OnPostStartSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
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

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = NewSession.Role,
            Provider = NullIfWhiteSpace(NewSession.Provider),
            ExternalSessionReference = NullIfWhiteSpace(NewSession.ExternalSessionReference),
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = task.Project.ContextRevision,
            StartedUtc = DateTime.UtcNow
        };

        task.Project.IncrementContextRevision();
        dbContext.AgentSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Started a {session.Role.ToString().ToLowerInvariant()} session. Copy the generated Markdown into a new AI conversation.";
        return RedirectToPage(new { id, sessionId = session.Id });
    }

    public async Task<IActionResult> OnPostCreateContextEntryAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
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

    public async Task<IActionResult> OnPostCompleteSessionAsync(Guid id, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.AgentSessions
            .Include(candidate => candidate.Task)
            .ThenInclude(task => task.Project)
            .SingleOrDefaultAsync(candidate => candidate.Id == sessionId && candidate.TaskId == id, cancellationToken);

        if (session is null)
        {
            return NotFound();
        }

        if (session.Status == AgentSessionStatus.Started)
        {
            session.Status = AgentSessionStatus.Completed;
            session.CompletedUtc = DateTime.UtcNow;
            session.Task.Project.IncrementContextRevision();
            await dbContext.SaveChangesAsync(cancellationToken);
            TempData["StatusMessage"] = $"Completed the {session.Role.ToString().ToLowerInvariant()} session.";
        }

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
