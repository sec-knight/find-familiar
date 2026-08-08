using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages.Projects;

public sealed class IndexModel(
    FamiliarDbContext dbContext,
    IProjectLifecycleService lifecycle) : PageModel
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

        // Through the shared lifecycle service. Name uniqueness is a rule, and a rule written here and
        // again in the Familiar's write boundary is a rule that eventually differs between them.
        var outcome = await lifecycle.CreateProjectAsync(
            new CreateProjectRequest(NewProject.Name, NewProject.Purpose), cancellationToken);

        if (outcome.Status == ProjectLifecycleStatus.NameTaken)
        {
            ModelState.AddModelError("NewProject.Name", "A project with this name already exists.");
            await LoadProjectsAsync(cancellationToken);
            return Page();
        }

        if (outcome.Status != ProjectLifecycleStatus.Succeeded)
        {
            ModelState.AddModelError(string.Empty, outcome.ValidationMessage ?? "That project could not be created.");
            await LoadProjectsAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage("Details", new { id = outcome.ProjectId });
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
