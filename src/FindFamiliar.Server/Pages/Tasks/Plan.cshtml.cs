using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Gateway;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages.Tasks;

/// <summary>
/// The whole plan behind one handoff, read a page at a time.
///
/// <b>Why the task page could not simply print it.</b> That page shows a bounded excerpt of every
/// record, which is right for a page that lists a dozen of them and wrong for the one artifact a human
/// is being asked to approve. Rather than make that page unbounded — which would make every task with a
/// long history expensive to open — the excerpt stays where it is and links here, and this page is
/// bounded per request instead of in total (ADR-0020).
///
/// <b>Read-only, structurally.</b> It holds no capture, dispatch or approval service and no handler but
/// <c>OnGetAsync</c>, so the page that exists to inform a decision cannot make one. Approving still
/// happens on the task page, against the concurrency token that page carries.
/// </summary>
public sealed class PlanModel(
    FamiliarDbContext dbContext,
    IContextProjectionService contextProjection,
    IFamiliarSessionHandoffPlanReader planReader) : PageModel
{
    /// <summary>
    /// How much of the artifact one request renders. Larger than the gateway's page because the
    /// constraint differs: a browser is bounded by what a person will scroll, a tool response by what a
    /// model's context can hold.
    /// </summary>
    public const int PageLength = 20_000;

    public SessionHandoff Handoff { get; private set; } = null!;

    public string TaskTitle { get; private set; } = string.Empty;

    public Guid TaskId { get; private set; }

    public string ArtifactTitle { get; private set; } = string.Empty;

    /// <summary>The slice of the artifact this request renders.</summary>
    public string PlanText { get; private set; } = string.Empty;

    public int Offset { get; private set; }

    /// <summary>How much of the artifact was retained and is therefore reachable by paging.</summary>
    public int RetainedLength { get; private set; }

    /// <summary>The artifact's length before any retention bound; above <see cref="RetainedLength"/> only when text was lost.</summary>
    public int OriginalLength { get; private set; }

    public FamiliarPlanCompleteness Completeness { get; private set; }

    public bool HasMore => Offset + PlanText.Length < RetainedLength;

    public int NextOffset => Offset + PlanText.Length;

    public int PreviousOffset => Math.Max(0, Offset - PageLength);

    public async Task<IActionResult> OnGetAsync(Guid id, int? offset, CancellationToken cancellationToken)
    {
        var handoff = await dbContext.SessionHandoffs
            .AsNoTracking()
            .Include(candidate => candidate.Task)
            .Include(candidate => candidate.SourceSession)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (handoff is null)
        {
            return NotFound();
        }

        var document = await contextProjection.GetTaskContextAsync(handoff.TaskId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var artifactKind = handoff.SourceSession.Role switch
        {
            AgentSessionRole.Planner => ContextEntryKind.Plan,
            AgentSessionRole.Implementer => ContextEntryKind.Implementation,
            _ => ContextEntryKind.Review
        };

        // The same selection the gateway makes — the newest artifact of the source session's own kind,
        // produced by that session. Anchoring on SourceSessionId is what keeps a task's third Planner
        // run from showing the first run's plan under the third run's approval.
        var artifact = document.TaskEntries
            .Where(entry => entry.Kind == artifactKind && entry.SourceSessionId == handoff.SourceSessionId)
            .OrderByDescending(entry => entry.CreatedUtc)
            .FirstOrDefault();

        if (artifact is null)
        {
            return NotFound();
        }

        var complete = await planReader.ReadCompleteArtifactAsync(artifact.Id, cancellationToken);
        var text = complete?.Content ?? artifact.Content;

        Handoff = handoff;
        TaskId = handoff.TaskId;
        TaskTitle = document.Task.Title;
        ArtifactTitle = artifact.Title;
        RetainedLength = text.Length;
        OriginalLength = complete?.OriginalLength ?? text.Length;
        Offset = Math.Clamp(offset ?? 0, 0, Math.Max(0, text.Length - 1));
        PlanText = text.Substring(Offset, Math.Min(PageLength, text.Length - Offset));

        Completeness = complete is null
            ? FamiliarPlanCompleteness.Excerpt
            : complete.OriginalLength > complete.Content.Length
                ? FamiliarPlanCompleteness.PartiallyRetained
                : HasMore || Offset > 0
                    ? FamiliarPlanCompleteness.Page
                    : FamiliarPlanCompleteness.Complete;

        return Page();
    }
}
