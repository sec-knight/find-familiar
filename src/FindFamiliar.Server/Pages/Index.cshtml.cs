using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// The dashboard: what this system currently holds, and what the Familiar has been doing.
///
/// Every number here is counted from persisted rows on this render. The page previously carried four
/// hardcoded zeros and a note promising live data later; placing a real usage panel beside them would
/// have left an application that shows a true figure and a false one in the same grid, styled
/// identically. So they are counted now.
///
/// <c>GET</c> writes nothing on any branch.
/// </summary>
public sealed class IndexModel(FamiliarDbContext dbContext, IFamiliarChatUsageService usage) : PageModel
{
    public int Projects { get; private set; }

    public int Tasks { get; private set; }

    public int Workers { get; private set; }

    public int ContextEntries { get; private set; }

    /// <summary>
    /// Projects withheld from every pack sent to a provider. Shown because a person operating this
    /// should be able to see that the boundary exists and is holding, without opening a database.
    /// </summary>
    public int SensitiveProjects { get; private set; }

    public FamiliarChatUsage Usage { get; private set; } = new(0, 0, 0, 0, []);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Projects = await dbContext.Projects.AsNoTracking().CountAsync(cancellationToken);
        Tasks = await dbContext.Tasks.AsNoTracking().CountAsync(cancellationToken);
        Workers = await dbContext.Workers.AsNoTracking().CountAsync(cancellationToken);

        // Active entries only. A superseded entry is history rather than current context, and counting
        // it here would make the workspace look larger than what any session would actually be given.
        ContextEntries = await dbContext.ContextEntries
            .AsNoTracking()
            .CountAsync(entry => entry.State == ContextEntryState.Active, cancellationToken);

        SensitiveProjects = await dbContext.Projects
            .AsNoTracking()
            .CountAsync(project => project.IsSensitive, cancellationToken);

        Usage = await usage.GetUsageAsync(cancellationToken);
    }

    /// <summary>A percentage, or the honest word when the provider never reported one.</summary>
    public static string CacheHitRateLabel(double? rate) =>
        rate is { } value ? $"{value:P0}" : "not reported";
}
