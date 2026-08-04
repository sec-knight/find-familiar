using FindFamiliar.Server.Data;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages;

public sealed class WorkersModel(
    IWorkerOverviewService workerOverviewService,
    FamiliarDbContext dbContext) : PageModel
{
    public IReadOnlyList<WorkerOverviewItem> Items { get; private set; } = Array.Empty<WorkerOverviewItem>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Items = await workerOverviewService.GetWorkersAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSetEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var worker = await dbContext.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);

        if (worker is null)
        {
            return NotFound();
        }

        worker.Enabled = enabled;
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = enabled
            ? $"Enabled worker '{worker.DisplayName}'."
            : $"Disabled worker '{worker.DisplayName}'.";

        return RedirectToPage();
    }
}
