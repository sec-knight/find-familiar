using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// The Familiar: one project, read out from what is recorded.
///
/// This slice is read-only by construction. There is no <c>OnPost</c> handler on this type, so there
/// is no route by which this page can write anything at all — not a conversation row, not a message,
/// not a decision. That is asserted by a test rather than left as an intention.
///
/// The page derives nothing. Project state comes from <see cref="IProjectSnapshotService"/> and its
/// account from <see cref="FamiliarSummaryWriter"/>, so the Familiar and the Demiplane cannot develop
/// separate opinions about what a task's state is (ADR-0011). Conversation rows come from
/// <see cref="IFamiliarConversationService"/> already ordered and already project-filtered.
///
/// No reasoning provider is called from here, on any path. A <c>GET</c> of a project is a read.
/// </summary>
public sealed class FamiliarModel(
    IProjectSnapshotService snapshots,
    IFamiliarConversationService conversations) : PageModel
{
    /// <summary>The bounded project state. Null only when the project could not be read at all.</summary>
    public ProjectSnapshot? Snapshot { get; private set; }

    /// <summary>The deterministic, provider-free account of the project.</summary>
    public FamiliarProjectSummary? Summary { get; private set; }

    /// <summary>Null when nobody has spoken to this project yet — no row is created to avoid it.</summary>
    public FamiliarConversationView? Conversation { get; private set; }

    /// <summary>
    /// Set when the project exists but could not be read for an operational reason this application
    /// classifies. Always text authored here, never a database or provider message.
    /// </summary>
    public string? UnavailableDetail { get; private set; }

    /// <summary>
    /// True when the snapshot was built but is too large to send to a reasoning provider safely. The
    /// page still renders in full: the deterministic summary is complete, and it is the sending that
    /// is refused, not the reading.
    /// </summary>
    public bool SnapshotOverBudget { get; private set; }

    public bool IsArchived => Snapshot?.ProjectStatus == ProjectStatus.Archived;

    public async Task<IActionResult> OnGetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await snapshots.GetSnapshotAsync(projectId, cancellationToken);

        if (result.Outcome == ProjectSnapshotOutcome.ProjectNotFound)
        {
            return NotFound();
        }

        if (result.Snapshot is null)
        {
            // The project exists but the read failed. Saying so beats a 500, for the reason
            // ProviderCapacitySnapshot.Faulted exists: an operational hiccup is a fact to report.
            UnavailableDetail = result.Detail;
            return Page();
        }

        Snapshot = result.Snapshot;
        SnapshotOverBudget = result.Outcome == ProjectSnapshotOutcome.TooLarge;
        Summary = FamiliarSummaryWriter.Compose(result.Snapshot);
        Conversation = await conversations.GetAsync(projectId, cancellationToken);

        return Page();
    }

    /// <summary>Who a message is attributed to, in the page's own words rather than the enum's.</summary>
    public static string AuthorLabel(FamiliarMessageAuthor author) => author switch
    {
        FamiliarMessageAuthor.Human => "You",
        FamiliarMessageAuthor.Familiar => "Familiar",
        _ => "Find Familiar"
    };

    public static string AuthorCss(FamiliarMessageAuthor author) => author switch
    {
        FamiliarMessageAuthor.Human => "is-human",
        FamiliarMessageAuthor.Familiar => "is-familiar",
        _ => "is-system"
    };

    /// <summary>
    /// What a proposal would do, stated by this application. The provider's own words for it are not
    /// used here: a description of an effect is a claim about this system, and this system makes it.
    /// </summary>
    public static string ActionHeading(FamiliarActionKind kind) => kind switch
    {
        FamiliarActionKind.CreateTask => "Create a task",
        _ => "Start a Planner session"
    };

    public static string ActionEffect(FamiliarActionKind kind) => kind switch
    {
        FamiliarActionKind.CreateTask =>
            "One new task in this project, ready to be picked up. No session starts and no worker is notified.",

        _ => "One Planner session starts on the task below. An eligible worker may claim it automatically."
    };
}
