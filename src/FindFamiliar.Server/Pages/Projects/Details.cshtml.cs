using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Pages.Projects;

public sealed class DetailsModel(FamiliarDbContext dbContext) : PageModel
{
    public FamiliarProject? Project { get; private set; }

    public IReadOnlyList<FamiliarTask> Tasks { get; private set; } = Array.Empty<FamiliarTask>();

    public IReadOnlyList<ContextEntry> ProjectEntries { get; private set; } = Array.Empty<ContextEntry>();

    public IReadOnlyList<ContextEntryKind> ContextEntryKinds { get; } = Enum.GetValues<ContextEntryKind>();

    [BindProperty]
    public NewTaskInput NewTask { get; set; } = new();

    [BindProperty]
    public NewProjectContextEntryInput NewProjectContext { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await LoadProjectAsync(id, cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostCreateTaskAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadProjectAsync(id, cancellationToken);
            return Page();
        }

        var project = await dbContext.Projects.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = NewTask.Title.Trim(),
            RequestedOutcome = NewTask.RequestedOutcome.Trim(),
            Status = TaskStatus.Ready,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        project.IncrementContextRevision();
        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Created task '{task.Title}'.";
        return RedirectToPage("../Tasks/Details", new { id = task.Id });
    }

    public async Task<IActionResult> OnPostCreateProjectContextAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadProjectAsync(id, cancellationToken);
            return Page();
        }

        var project = await dbContext.Projects.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = NewProjectContext.Kind,
            Title = NewProjectContext.Title.Trim(),
            Content = NewProjectContext.Content.Trim(),
            State = ContextEntryState.Active,
            CreatedUtc = DateTime.UtcNow
        };

        project.IncrementContextRevision();
        dbContext.ContextEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Recorded {entry.Kind.ToString().ToLowerInvariant()} context.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadProjectAsync(Guid id, CancellationToken cancellationToken)
    {
        Project = await dbContext.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (Project is null)
        {
            return false;
        }

        Tasks = await dbContext.Tasks
            .AsNoTracking()
            .Where(task => task.ProjectId == id)
            .OrderByDescending(task => task.UpdatedUtc)
            .ToListAsync(cancellationToken);

        ProjectEntries = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == id && entry.TaskId == null && entry.State == ContextEntryState.Active)
            .OrderBy(entry => entry.CreatedUtc)
            .ToListAsync(cancellationToken);

        return true;
    }
}

public sealed class NewTaskInput
{
    [Required]
    [StringLength(200)]
    [Display(Name = "Task title")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(4_000)]
    [Display(Name = "Requested outcome")]
    public string RequestedOutcome { get; set; } = string.Empty;
}

public sealed class NewProjectContextEntryInput
{
    [Display(Name = "Kind")]
    public ContextEntryKind Kind { get; set; } = ContextEntryKind.Goal;

    [Required]
    [StringLength(200)]
    [Display(Name = "Title")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(12_000)]
    [Display(Name = "Content")]
    public string Content { get; set; } = string.Empty;
}
