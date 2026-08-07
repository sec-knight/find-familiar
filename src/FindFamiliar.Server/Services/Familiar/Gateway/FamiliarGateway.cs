using FindFamiliar.Server.Services.Familiar.Chat.Brief;
using FindFamiliar.Server.Services.Familiar.Chat.Retrieval;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Gateway;

/// <summary>
/// The Summoning Gate: what an external body may ask of the Familiar.
///
/// Read-only, on every path, structurally.
/// </summary>
public interface IFamiliarGateway
{
    FamiliarManifest GetManifest();

    Task<FamiliarContextResult> SearchContextAsync(
        string query,
        Guid? projectId = null,
        int? maxItems = null,
        CancellationToken cancellationToken = default);

    Task<FamiliarProjectContext?> GetProjectContextAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<FamiliarProjectList> ListProjectsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The provider-neutral boundary an external AI client reaches the Familiar through.
///
/// <b>The Familiar is the persistent spirit; a frontier client is one body it can inhabit.</b> This is
/// the seam that makes that true rather than aspirational: continuity — who the Familiar is, what it
/// remembers, which projects exist, what may be shown to whom — lives here and in the services below
/// it, so swapping ChatGPT for Claude or for the native pages changes which body is speaking and
/// nothing about what it knows.
///
/// <b>Nothing about any vendor appears in this file or in the contracts beside it.</b> MCP is an
/// adapter over this type, and so is the REST surface, and a third adapter would be too. The rule
/// that keeps it that way: a transport may reshape what this returns, and may never decide what a
/// caller is allowed to see. Sensitivity, relevance, project selection and bounds are decided here
/// and below, once.
///
/// <b>It reads the Familiar's mind-map; it does not independently rediscover reality.</b> Every
/// answer comes from <see cref="IFamiliarContextRetrievalService"/> and
/// <see cref="IFamiliarStandingBriefService"/> — the same two services the native conversation is
/// built on, with the same relevance floor, the same sensitivity filtering in the query rather than
/// after it, the same exclusion of superseded entries and of raw provider prompts and output. A
/// gateway that ran its own search would be a second definition of what this system knows, and the
/// two would drift; a gateway that shelled out to git or scanned the filesystem would be a second
/// definition of what is real. Repository state reaches an external client because the snapshot is an
/// ordinary context entry that retrieval can find, not because anything here goes looking.
///
/// <b>There is no write path, and not by convention.</b> This class holds no
/// <c>IWorkflowDispatchService</c>, no plan drafting or approval service, no <c>DbContext</c> it could
/// save through — the two services it depends on are read-only, and the only way to add a mutation is
/// to add a dependency, which is a visible act in a review rather than an oversight. ADR-0016 records
/// the shape write-back must take when it comes: a candidate, a human or policy gate, and only then a
/// canonical change. Never an external model reaching a table.
/// </summary>
public sealed class FamiliarGateway(
    IFamiliarContextRetrievalService retrieval,
    IFamiliarStandingBriefService briefs,
    IOptions<FamiliarIdentityOptions> identity) : IFamiliarGateway
{
    /// <summary>
    /// The most context items one call may return, and the ceiling on what a caller may ask for.
    ///
    /// Retrieval has its own cap; this exists so the bound is stated at the boundary too, and so a
    /// caller asking for a thousand gets six rather than an error — a frontier model that guesses a
    /// parameter wrong should get a smaller answer, not a failed turn.
    /// </summary>
    public const int MaxItems = FamiliarRetrievalResult.MaxEntries;

    /// <summary>A bound on the query itself. Anything longer is a paste, not a question.</summary>
    public const int MaxQueryLength = 1_000;

    /// <summary>Projects listed in one call.</summary>
    public const int MaxProjects = FamiliarStandingBrief.MaxProjects;

    private readonly FamiliarIdentityOptions _identity = identity.Value;

    /// <summary>
    /// The capability names an external client is shown, which are the operations below and no
    /// others. Written out rather than reflected over the interface so that adding a method cannot
    /// silently advertise it.
    /// </summary>
    private static readonly string[] ReadCapabilities =
        ["search_familiar_context", "get_project_context", "list_familiar_projects"];

    public FamiliarManifest GetManifest() => new(
        _identity.ResolvedName,
        "Find Familiar",
        _identity.Description,
        _identity.ResolvedGuidance,
        ReadCapabilities,
        // Stated empty rather than omitted. Sprint 14 exposes no mutation of any kind: no task
        // creation, no session start, no proposal, no memory write-back.
        []);

    public async Task<FamiliarContextResult> SearchContextAsync(
        string query,
        Guid? projectId = null,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (query ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return Empty(trimmed, projectId, null, "No query was supplied, so no search was run.");
        }

        if (trimmed.Length > MaxQueryLength)
        {
            // Truncated rather than refused. An over-long query is a client pasting a whole
            // conversation into a search box, and the first thousand characters of it are still a
            // better search than a failed turn.
            trimmed = trimmed[..MaxQueryLength];
        }

        // Restricted, not merely weighted. A body that asked about one project and was handed another
        // project's records has been answered a question it did not ask and cannot tell.
        var found = await retrieval.RetrieveAsync(
            trimmed,
            projectId,
            cancellationToken,
            restrictToProject: projectId is not null);

        var limit = Math.Clamp(maxItems ?? MaxItems, 1, MaxItems);
        var carried = found.Entries.Take(limit).ToList();

        var projectName = projectId is { } id
            ? carried.FirstOrDefault(entry => entry.ProjectId == id)?.ProjectName
              ?? await ResolveProjectNameAsync(id, cancellationToken)
            : null;

        return new FamiliarContextResult(
            trimmed,
            projectId,
            projectName,
            carried
                .Select(entry => new FamiliarContextItem(
                    entry.EntryId,
                    entry.ProjectId,
                    entry.ProjectName,
                    entry.Kind.ToString(),
                    entry.Title,
                    entry.Excerpt,
                    entry.IsExcerpted,
                    entry.CreatedUtc))
                .ToList(),
            found.SensitiveWithheld,
            found.BelowThreshold,
            Truncated: found.Entries.Count > carried.Count,
            Describe(found, carried.Count));
    }

    public async Task<FamiliarProjectContext?> GetProjectContextAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        // The brief is asked for this project specifically, and it is the brief that decides whether
        // this project may be seen at all. A sensitive project is not in what comes back, so the
        // answer below is indistinguishable from the answer for a project that does not exist — which
        // is the correct disclosure: neither the title nor the existence of the row.
        var brief = await briefs.GetBriefAsync(projectId, cancellationToken);
        var project = brief.Projects.FirstOrDefault(candidate => candidate.ProjectId == projectId);

        if (project is null)
        {
            return null;
        }

        return new FamiliarProjectContext(
            project.ProjectId,
            project.Name,
            project.Purpose,
            project.TotalTasks,
            project.NeedsAttentionCount,
            project.RunningCount,
            project.Tasks
                .Select(task => new FamiliarProjectTask(
                    task.TaskId,
                    task.Title,
                    task.DisplayState.ToString(),
                    task.ReasonText,
                    task.NeedsHumanAttention,
                    task.ProposedRole?.ToString()))
                .ToList(),
            project.TasksOmitted,
            project.LastRecordedActivityUtc,
            brief.Limitations);
    }

    public async Task<FamiliarProjectList> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var brief = await briefs.GetBriefAsync(null, cancellationToken);

        return new FamiliarProjectList(
            brief.Projects
                .Take(MaxProjects)
                .Select(project => new FamiliarProjectSummary(
                    project.ProjectId,
                    project.Name,
                    project.Purpose,
                    project.NeedsAttentionCount,
                    project.LastRecordedActivityUtc))
                .ToList(),
            brief.SensitiveProjectsWithheld);
    }

    /// <summary>
    /// The project's name for the header of a scoped search, when the search itself returned nothing
    /// to take it from. Goes through the brief rather than the table, so a sensitive project stays
    /// unnamed here exactly as it is everywhere else.
    /// </summary>
    private async Task<string?> ResolveProjectNameAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var brief = await briefs.GetBriefAsync(projectId, cancellationToken);

        return brief.Projects.FirstOrDefault(project => project.ProjectId == projectId)?.Name;
    }

    private static FamiliarContextResult Empty(
        string query,
        Guid? projectId,
        string? projectName,
        string disclosure) =>
        new(query, projectId, projectName, [], 0, 0, false, disclosure);

    /// <summary>
    /// What happened, in a sentence the external model reads.
    ///
    /// The two empty cases are told apart deliberately. "Nothing is recorded about this" and "things
    /// were close and none was responsive" license different answers, and a body handed a bare empty
    /// list will fill the silence from general knowledge in the same confident register it uses for
    /// things it actually knows.
    /// </summary>
    private static string Describe(FamiliarRetrievalResult found, int carried)
    {
        if (carried > 0)
        {
            var note = $"{carried} recorded item(s) matched this query.";

            return found.SensitiveWithheld > 0
                ? note + $" {found.SensitiveWithheld} further match(es) are withheld as sensitive; "
                       + "you may say that something is withheld and nothing about what it contains."
                : note;
        }

        if (found.NoMatchAboveFloor)
        {
            return $"Nothing relevant was found. {found.BelowThreshold} record(s) mentioned some of "
                   + "these words but none was close enough to be responsive. Say that nothing is "
                   + "recorded about this rather than answering from general knowledge.";
        }

        return found.SensitiveWithheld > 0
            ? $"Nothing readable was found. {found.SensitiveWithheld} match(es) are withheld as "
              + "sensitive; you may say that something is withheld and nothing about what it contains."
            : "Nothing is recorded about this. Say so rather than answering from general knowledge — "
              + "an absence of records is a finding, not a gap to fill.";
    }
}
