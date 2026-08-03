using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

public sealed class WorkModel(IWorkQueueService workQueueService) : PageModel
{
    public IReadOnlyList<WorkQueueItem> Items { get; private set; } = Array.Empty<WorkQueueItem>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Items = await workQueueService.GetActiveQueueAsync(cancellationToken);
    }
}
