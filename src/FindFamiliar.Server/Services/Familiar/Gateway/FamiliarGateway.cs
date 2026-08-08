using FindFamiliar.Server.Services.Demiplane;
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

    /// <summary>
    /// Everything currently waiting on a human decision, across every project this caller may read.
    ///
    /// Read-only, like everything else here. It reports decision points; it cannot decide one.
    /// </summary>
    Task<FamiliarOpenDecisionList> ListOpenDecisionsAsync(CancellationToken cancellationToken = default);
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
    IDemiplaneProjectionService projections,
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
    /// What is waiting on the human, assembled from the projection the Demiplane itself renders.
    ///
    /// <b>Two services, and neither is a new opinion.</b> The standing brief decides which projects
    /// this caller may see — so sensitivity is filtered by the same rule as everywhere else, in the
    /// query rather than after it — and the Demiplane projection decides what each task's state is and
    /// whether a human is being asked. ADR-0011 settled that the Demiplane owns what a task's state
    /// means; composing a second answer here is precisely how the Familiar would come to contradict
    /// the page about the same task.
    ///
    /// <b>A decision exists only where a Pending handoff row does.</b> The projection surfaces the row
    /// id and its concurrency token, and this reports them. Nothing here reads model prose, and there
    /// is no path by which a decision could be invented for a task nobody proposed anything on.
    /// </summary>
    public async Task<FamiliarOpenDecisionList> ListOpenDecisionsAsync(CancellationToken cancellationToken = default)
    {
        var brief = await briefs.GetBriefAsync(null, cancellationToken);
        var decisions = new List<FamiliarOpenDecision>();
        var omitted = 0;

        foreach (var briefProject in brief.Projects)
        {
            var projection = await projections.GetProjectionAsync(briefProject.ProjectId, cancellationToken);

            if (projection is null)
            {
                continue;
            }

            foreach (var task in projection.Tasks)
            {
                // The row is the authority. A task can need attention for reasons that are not a
                // decision anybody can take — a failed session, a declined step — and offering those
                // as choices would be inventing an action the workflow does not support.
                if (task.PendingHandoffId is not { } handoffId
                    || task.PendingHandoffToken is not { } token
                    || task.ProposedRole is not { } proposedRole
                    || task.ProposedKind is not { } proposedKind)
                {
                    continue;
                }

                if (decisions.Count >= FamiliarOpenDecisionList.MaxDecisions)
                {
                    omitted++;
                    continue;
                }

                decisions.Add(new FamiliarOpenDecision(
                    handoffId,

                    // Named rather than numbered, and one kind for now: this is the only decision the
                    // workflow currently asks an external client about.
                    DecisionKind: "SessionHandoff",
                    projection.ProjectId,
                    projection.ProjectName,
                    task.TaskId,
                    task.Title,

                    // The Demiplane's own sentence, not a paraphrase of it.
                    task.ReasonText,
                    proposedRole.ToString(),
                    proposedKind.ToString(),
                    task.Summary.WhatHappened,

                    // What the finished session found, where the projection has it: the outcome detail
                    // first, then its account of why the human is needed.
                    //
                    // Null when neither exists, deliberately. CurrentState would always produce a
                    // string — "Waiting for you." — and a field that is never empty is a field a
                    // client will read as evidence when there is none. An absent evidence field is
                    // honest; a filled one that says nothing is worse than silence.
                    Evidence: Bound(task.Summary.OutcomeDetail ?? task.Summary.NeedsAttention),

                    // Exactly what a Pending handoff accepts. Not a menu this layer chose.
                    LegalChoices: ["approve", "decline"],
                    token,
                    task.UpdatedUtc));
            }
        }

        return new FamiliarOpenDecisionList(
            decisions,
            brief.SensitiveProjectsWithheld,
            omitted,
            Disclose(decisions.Count, brief.SensitiveProjectsWithheld, omitted));
    }

    /// <summary>
    /// What the list could not show. An empty list with no explanation is the one answer a client will
    /// confidently misreport as "nothing needs you".
    /// </summary>
    private static string Disclose(int shown, int sensitiveWithheld, int omitted)
    {
        var disclosure = shown == 0
            ? "Nothing is currently waiting on a human decision in the projects I can read."
            : $"{shown} decision{(shown == 1 ? " is" : "s are")} waiting on a human.";

        if (omitted > 0)
        {
            disclosure += $" {omitted} more were not listed to keep this answer bounded.";
        }

        if (sensitiveWithheld > 0)
        {
            disclosure += $" {sensitiveWithheld} project{(sensitiveWithheld == 1 ? " is" : "s are")} "
                + "marked sensitive and was not examined.";
        }

        disclosure += " Reading this changes nothing; only the human can decide, and this client cannot "
            + "submit that decision.";

        return disclosure;
    }

    /// <summary>Evidence is an excerpt, and a truncated one says so — an unmarked cut reads as the whole.</summary>
    private static string? Bound(string? evidence)
    {
        const int maximum = 1_200;

        if (string.IsNullOrWhiteSpace(evidence))
        {
            return null;
        }

        var trimmed = evidence.Trim();

        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum] + "… (truncated)";
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
