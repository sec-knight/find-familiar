using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages.Projects;

public sealed class DetailsModel(
    FamiliarDbContext dbContext,
    IWorkflowDispatchService workflowDispatch,
    IProjectContextRecordingService contextRecording,
    IContextProjectionService contextProjection) : PageModel
{
    public FamiliarProject? Project { get; private set; }

    public IReadOnlyList<FamiliarTask> Tasks { get; private set; } = Array.Empty<FamiliarTask>();

    public IReadOnlyList<ContextEntryDocument> ProjectEntries { get; private set; } =
        Array.Empty<ContextEntryDocument>();

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
        ModelState.Clear();

        if (!TryValidateModel(NewTask, nameof(NewTask)))
        {
            await LoadProjectAsync(id, cancellationToken);
            return Page();
        }

        var project = await dbContext.Projects.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var task = workflowDispatch.CreateReadyTask(
            project,
            NewTask.Title,
            NewTask.RequestedOutcome,
            DateTime.UtcNow);

        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Created task '{task.Title}'.";
        return RedirectToPage("../Tasks/Details", new { id = task.Id });
    }

    public async Task<IActionResult> OnPostCreateProjectContextAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();

        if (!TryValidateModel(NewProjectContext, nameof(NewProjectContext)))
        {
            await LoadProjectAsync(id, cancellationToken);
            return Page();
        }

        // Through the recording service rather than writing the rows here. The page used to own the
        // lookup, the insert and the revision bump inline, which meant those invariants existed once
        // for browsers and nowhere else — and anything that was not a browser wrote the database
        // directly. One implementation, every caller.
        var outcome = await contextRecording.RecordAsync(
            new RecordProjectContextRequest(
                id,
                NewProjectContext.Kind,
                NewProjectContext.Title,
                NewProjectContext.Content,

                // A person typed this into the Demiplane, so that is what it is. The page does not
                // offer a provenance picker: the honest value here is not one the author chooses.
                ContextProvenance.HumanReported,
                RecordedBy: "demiplane"),
            cancellationToken);

        switch (outcome.Status)
        {
            case RecordProjectContextStatus.Recorded:
                TempData["StatusMessage"] =
                    $"Recorded {NewProjectContext.Kind.ToString().ToLowerInvariant()} context.";
                return RedirectToPage(new { id });

            case RecordProjectContextStatus.ProjectNotFound:
                return NotFound();

            default:
                ModelState.AddModelError(
                    string.Empty,
                    outcome.ValidationMessage ?? "That context could not be recorded.");
                await LoadProjectAsync(id, cancellationToken);
                return Page();
        }
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

        // Through the projection rather than a query of its own. "A project's own context" is one
        // definition, and the Familiar gateway enumerates the same list — two queries that agree today
        // are two queries that can disagree tomorrow.
        ProjectEntries = await contextProjection.GetProjectEntriesAsync(id, cancellationToken);

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
