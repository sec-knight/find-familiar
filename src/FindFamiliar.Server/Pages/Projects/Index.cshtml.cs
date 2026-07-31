using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages.Projects;

public sealed class IndexModel(FamiliarDbContext dbContext) : PageModel
{
    public IReadOnlyList<ProjectListItem> Projects { get; private set; } = Array.Empty<ProjectListItem>();

    [BindProperty]
    public NewProjectInput NewProject { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadProjectsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadProjectsAsync(cancellationToken);
            return Page();
        }

        var name = NewProject.Name.Trim();
        if (await dbContext.Projects.AnyAsync(project => project.Name == name, cancellationToken))
        {
            ModelState.AddModelError("NewProject.Name", "A project with this name already exists.");
            await LoadProjectsAsync(cancellationToken);
            return Page();
        }

        var now = DateTime.UtcNow;
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = NewProject.Purpose.Trim(),
            Status = ProjectStatus.Active,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Created project '{project.Name}'.";
        return RedirectToPage("./Details", new { id = project.Id });
    }

    private async Task LoadProjectsAsync(CancellationToken cancellationToken)
    {
        Projects = await dbContext.Projects
            .AsNoTracking()
            .OrderBy(project => project.Name)
            .Select(project => new ProjectListItem(
                project.Id,
                project.Name,
                project.Purpose,
                project.Status,
                project.ContextRevision,
                project.Tasks.Count(),
                project.ContextEntries.Count(entry => entry.State == ContextEntryState.Active)))
            .ToListAsync(cancellationToken);
    }
}

public sealed record ProjectListItem(
    Guid Id,
    string Name,
    string Purpose,
    ProjectStatus Status,
    int ContextRevision,
    int TaskCount,
    int ActiveContextEntryCount);

public sealed class NewProjectInput
{
    [Required]
    [StringLength(160)]
    [Display(Name = "Project name")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(4_000)]
    [Display(Name = "Purpose")]
    public string Purpose { get; set; } = string.Empty;
}
