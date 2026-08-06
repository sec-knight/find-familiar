using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat.Brief;

/// <summary>
/// Builds the system-wide brief. Read-only on every path.
/// </summary>
public interface IFamiliarStandingBriefService
{
    Task<FamiliarStandingBrief> GetBriefAsync(
        Guid? focusProjectId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Demiplane's per-project projection, rolled up across every project a provider is allowed to
/// be told about.
///
/// Built by asking <see cref="IDemiplaneProjectionService"/> for each project rather than by
/// re-deriving task state here. That is N queries where one clever one would do, and it is the right
/// trade: ADR-0011 made the Demiplane the single owner of what a task's state is, and the moment this
/// file classified a task itself the Familiar could start contradicting the page the human is looking
/// at. Project counts are small and this runs once per turn.
///
/// <b>Sensitive projects are excluded entirely, and the exclusion is counted.</b> A brief that dropped
/// them silently would let the Familiar answer "nothing is blocked" about a world it was only shown
/// part of. The count travels into <see cref="FamiliarStandingBrief.Limitations"/> so the model is
/// told the edge of its own knowledge, without being told anything about what is behind it.
///
/// <see cref="GetBriefAsync"/>'s focus argument affects ordering only. Focus is a lean, never a
/// filter — a focused conversation must still be able to answer about everything else, because
/// cross-project questions are the point of a system-wide Familiar.
/// </summary>
public sealed class FamiliarStandingBriefService(
    FamiliarDbContext dbContext,
    IDemiplaneProjectionService projections,
    TimeProvider timeProvider) : IFamiliarStandingBriefService
{
    public async Task<FamiliarStandingBrief> GetBriefAsync(
        Guid? focusProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var limitations = new List<string>();

        // Sensitive projects never leave the database. Filtered in the query rather than after it, so
        // there is no moment at which flagged rows are held in memory alongside a pack being built.
        var candidates = await dbContext.Projects
            .AsNoTracking()
            .Where(project => !project.IsSensitive)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.Status,
                project.UpdatedUtc
            })
            .ToListAsync(cancellationToken);

        var totalProjects = await dbContext.Projects.AsNoTracking().CountAsync(cancellationToken);
        var withheld = totalProjects - candidates.Count;

        // Active first, then most recently touched — and the focused project ahead of its peers, which
        // is the whole of what focus does here. Archived projects come last rather than being dropped:
        // "what happened to X?" is a fair question about an archived project.
        var ordered = candidates
            .OrderByDescending(project => project.Id == focusProjectId)
            .ThenByDescending(project => project.Status == ProjectStatus.Active)
            .ThenByDescending(project => project.UpdatedUtc)
            .ToList();

        var carried = ordered.Take(FamiliarStandingBrief.MaxProjects).ToList();
        var projectsOmitted = ordered.Count - carried.Count;

        var projects = new List<BriefProject>(carried.Count);

        foreach (var candidate in carried)
        {
            if (await projections.GetProjectionAsync(candidate.Id, cancellationToken) is not { } projection)
            {
                // Deleted between the two reads, or unreadable. Skipped rather than guessed at; the
                // count below still reports it as missing from the brief.
                continue;
            }

            projects.Add(Compose(projection));
        }

        if (projectsOmitted > 0)
        {
            limitations.Add(
                $"{projectsOmitted} further project(s) exist and are not described here. "
                + "Ask about one by name and it can be looked up.");
        }

        if (withheld > 0)
        {
            // What, not which. The count is the honest disclosure; the names are the thing being
            // protected.
            limitations.Add(
                $"{withheld} project(s) are marked sensitive and are withheld from this brief entirely. "
                + "Nothing about them — names, tasks, or state — is available to you.");
        }

        limitations.Add(
            "This brief is a summary. Task lists are capped per project, so a task's absence here is "
            + "not evidence that it does not exist.");

        limitations.Add(
            "You cannot see repository contents, file changes, commits, session transcripts, or "
            + "anything outside these records.");

        return new FamiliarStandingBrief(
            projects,
            totalProjects,
            projectsOmitted,
            withheld,
            limitations,
            timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Reduces one Demiplane projection to the brief's shape.
    ///
    /// Counts are taken across every task in the projection; only the carried list is capped. A
    /// truncated list must never make a project look smaller or healthier than it is, which is the
    /// same rule <c>SnapshotHealth</c> holds for the per-project snapshot.
    /// </summary>
    private static BriefProject Compose(DemiplaneProjection projection)
    {
        // Most useful first: anything asking for a human, then anything running, then the rest by
        // recency. A cap that kept the ten oldest untouched tasks would answer "what's blocked?" with
        // the least relevant half of the project.
        var ordered = projection.Tasks
            .OrderByDescending(task => task.NeedsHumanAttention)
            .ThenByDescending(task => task.DisplayState == TaskDisplayState.Running)
            .ThenByDescending(task => task.UpdatedUtc)
            .ToList();

        var carried = ordered
            .Take(FamiliarStandingBrief.MaxTasksPerProject)
            .Select(task => new BriefTask(
                task.TaskId,
                task.Title,
                task.DisplayState,
                task.ReasonText,
                task.NeedsHumanAttention))
            .ToList();

        return new BriefProject(
            projection.ProjectId,
            projection.ProjectName,
            projection.ProjectPurpose,
            projection.Tasks.Count,
            projection.NeedsAttention.Count,
            projection.Running.Count,
            carried,
            ordered.Count - carried.Count);
    }
}
