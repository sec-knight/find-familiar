using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Services.Familiar.Chat.Brief;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
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

    /// <summary>
    /// The worker pool and the providers behind it: who can run which role, what they are running,
    /// and why a role that work is waiting on cannot currently start.
    /// </summary>
    Task<FamiliarRuntimeState> InspectRuntimeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One task in full: its state and why, the sessions that ran on it, the records they produced,
    /// and the decision it is waiting on if any. Null when no readable task has that id.
    /// </summary>
    Task<FamiliarTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<FamiliarSessionHandoffPlan?> GetSessionHandoffPlanAsync(
        Guid handoffId,
        int? offset = null,
        int? maxCharacters = null,
        CancellationToken cancellationToken = default);
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
    IWorkerOverviewService workers,
    IProviderCapacityService providers,
    IContextProjectionService taskContext,
    IPendingPlanReader pendingPlans,
    IOptions<FamiliarIdentityOptions> identity,
    IFamiliarSessionHandoffPlanReader? handoffPlans = null) : IFamiliarGateway
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
    /// The capability names an external client is shown, which are the operations it may actually
    /// reach and no others.
    ///
    /// Written out rather than reflected over the registered tools, and deliberately so. Reflection
    /// would mean that adding any method to this boundary silently makes the Familiar claim a new
    /// public capability — an announcement nobody reviewed. An allowlist costs one line when a
    /// capability is genuinely added, and the cost of forgetting that line is a manifest that
    /// understates what exists, which is the safe direction to be wrong in.
    ///
    /// It has been wrong in that direction: this list was written in Sprint 14 and went two slices
    /// stale, so a connected client was told the gateway offered three capabilities and no writes
    /// while five reads and one write were live. Hence the test that compares this declaration
    /// against the MCP tool surface.
    /// </summary>
    private static readonly string[] ReadCapabilities =
    [
        "search_familiar_context",
        "get_project_context",
        "list_familiar_projects",
        "open_decisions",
        "inspect_familiar_runtime",
        "get_task_detail",
        "get_session_handoff_plan"
    ];

    /// <summary>
    /// The one operation an external client can use to change anything, and what it is bounded to.
    ///
    /// <c>submit_familiar_decision</c> relays a decision the human has already made to a gate Find
    /// Familiar itself raised. It requires the separate <c>familiar.decide</c> scope, it accepts no
    /// free text, and legality is re-decided inside the approval transaction. It is not a general
    /// write capability: nothing here creates a task, starts arbitrary work, edits a record, or
    /// writes a memory.
    /// </summary>
    private static readonly string[] WriteCapabilities =
    [
        "submit_familiar_decision",
        "create_familiar_project",
        "create_familiar_task",
        "set_familiar_task_status",
        "record_familiar_context",
        "start_familiar_session",
        "cancel_familiar_session"
    ];

    public FamiliarManifest GetManifest() => new(
        _identity.ResolvedName,
        "Find Familiar",
        _identity.Description,
        _identity.ResolvedGuidance,
        ReadCapabilities,
        WriteCapabilities);

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

        var (records, withheld) = await ProjectRecordsAsync(project.ProjectId, cancellationToken);

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
            brief.Limitations,
            records,
            withheld);
    }

    /// <summary>
    /// A project's own recorded context, enumerated rather than searched.
    ///
    /// <b>Why enumeration is the point.</b> Retrieval applies a relevance floor, which is correct for
    /// a question and wrong for an inventory — a constraint the user recorded once and never phrased
    /// again would be invisible, and a client cannot ask about a record it has no way to learn exists.
    /// The project page lists these; so must this.
    ///
    /// The same two subtractions the task surface makes, for the same reason and stated the same way:
    /// sensitive entries and raw provider input and output are removed, and what was removed is
    /// counted. The project itself has already been established as readable by the caller before this
    /// runs, so project-level sensitivity is settled higher up.
    /// </summary>
    private async Task<(IReadOnlyList<FamiliarTaskRecord> Records, int Withheld)> ProjectRecordsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var entries = await taskContext.GetProjectEntriesAsync(projectId, cancellationToken);

        var visible = entries
            .Where(entry => !entry.IsSensitive && !ExcludedRecordKinds.Contains(entry.Kind))
            .ToList();

        var records = visible
            .OrderByDescending(entry => entry.CreatedUtc)
            .Take(FamiliarProjectContext.MaxRecords)
            .Select(entry => new FamiliarTaskRecord(
                entry.Id,
                entry.Kind.ToString(),
                entry.Title,
                Excerpt(entry.Content),
                entry.CreatedUtc,
                entry.SourceSessionId))
            .ToList();

        return (records, entries.Count - records.Count);
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

        // Plans awaiting a human, in the projects this caller may read. Reported beside handoffs
        // because from the person's side "what needs me" is one question, and a client that had to
        // know to ask twice would eventually ask once.
        var readable = brief.Projects.ToDictionary(project => project.ProjectId, project => project.Name);

        foreach (var plan in await pendingPlans.ListPendingAsync(cancellationToken))
        {
            if (!readable.TryGetValue(plan.ProjectId, out var projectName))
            {
                // The plan's project is sensitive or otherwise not this caller's to see. Absent, and
                // not counted separately: the sensitive-project count already says a project was
                // withheld, and a second count would say how much was in it.
                continue;
            }

            if (decisions.Count >= FamiliarOpenDecisionList.MaxDecisions)
            {
                omitted++;
                continue;
            }

            var included = plan.Items.Where(item => item.IsIncluded).ToList();

            decisions.Add(new FamiliarOpenDecision(
                plan.PlanId,
                "PlanProposal",
                plan.ProjectId,
                projectName,
                TaskId: null,
                TaskTitle: null,
                Reason: $"A plan is waiting for your approval: {included.Count} task"
                    + $"{(included.Count == 1 ? "" : "s")} would be created"
                    + (included.FirstOrDefault(item => item.Role is not null) is { } starting
                        ? $", and a {starting.Role} session would start on \"{starting.Title}\"."
                        : ", and no session would start."),
                ProposedRole: included.FirstOrDefault(item => item.Role is not null)?.Role?.ToString(),
                ProposedKind: null,
                PriorOutcome: null,
                Evidence: Bound(plan.Summary),
                LegalChoices: ["approve", "decline"],
                plan.ConcurrencyToken,
                plan.UpdatedUtc,
                plan.Items
                    .Select(item => new FamiliarPlannedItem(
                        item.Title, item.RequestedOutcome, item.Role?.ToString(), item.IsIncluded))
                    .ToList()));
        }

        return new FamiliarOpenDecisionList(
            decisions,
            brief.SensitiveProjectsWithheld,
            omitted,
            Disclose(decisions.Count, brief.SensitiveProjectsWithheld, omitted));
    }

    /// <summary>
    /// One task, in the detail the Demiplane's task page shows.
    ///
    /// <b>Visibility first, and by the same route as everywhere else.</b> The context projection this
    /// leans on serves assignment packets, where the reader is a worker on the owner's own machine, so
    /// it applies no sensitivity rule at all. That is correct for its purpose and wrong for this one.
    /// The task is therefore resolved through the standing brief — the same filter that decides which
    /// projects may be listed — and a task in a project this caller may not read answers exactly as a
    /// task that does not exist. Naming which of the two applied would be the disclosure the
    /// sensitivity rule exists to withhold.
    ///
    /// <b>Two record filters, matching retrieval exactly.</b> Entries marked sensitive are removed,
    /// and <c>Prompt</c> and <c>RawOutput</c> are removed — raw provider input and output are excluded
    /// from every external answer this system gives, including the native conversation's, so this is
    /// not an asymmetry between frontends but a rule both sides of the boundary share (ADR-0019).
    /// What was removed is counted rather than hidden.
    /// </summary>
    public async Task<FamiliarTaskDetail?> GetTaskDetailAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var brief = await briefs.GetBriefAsync(null, cancellationToken);

        var owningProject = brief.Projects
            .FirstOrDefault(project => project.Tasks.Any(task => task.TaskId == taskId));

        if (owningProject is null)
        {
            return null;
        }

        var document = await taskContext.GetTaskContextAsync(taskId, cancellationToken);
        var projection = await projections.GetProjectionAsync(owningProject.ProjectId, cancellationToken);
        var task = projection?.Tasks.SingleOrDefault(candidate => candidate.TaskId == taskId);

        if (document is null || task is null)
        {
            // The brief listed it a moment ago and it is gone now, or the two projections disagree.
            // Answering null is honest; inventing a partial task from whichever half survived is not.
            return null;
        }

        var visible = document.TaskEntries
            .Where(entry => !entry.IsSensitive && !ExcludedRecordKinds.Contains(entry.Kind))
            .ToList();

        var records = visible
            .OrderByDescending(entry => entry.CreatedUtc)
            .Take(FamiliarTaskDetail.MaxRecords)
            .Select(entry => new FamiliarTaskRecord(
                entry.Id,
                entry.Kind.ToString(),
                entry.Title,
                Excerpt(entry.Content),
                entry.CreatedUtc,
                entry.SourceSessionId))
            .ToList();

        var awaiting = task.PendingHandoffId is { } handoffId
            && task.PendingHandoffToken is { } token
            && task.ProposedRole is { } proposedRole
            && task.ProposedKind is { } proposedKind
                ? new FamiliarOpenDecision(
                    handoffId,
                    "SessionHandoff",
                    owningProject.ProjectId,
                    owningProject.Name,
                    task.TaskId,
                    task.Title,
                    task.ReasonText,
                    proposedRole.ToString(),
                    proposedKind.ToString(),
                    task.Summary.WhatHappened,
                    Bound(task.Summary.OutcomeDetail ?? task.Summary.NeedsAttention),
                    ["approve", "decline"],
                    token,
                    task.UpdatedUtc)
                : null;

        return new FamiliarTaskDetail(
            document.Task.Id,
            document.Task.Title,
            document.Task.RequestedOutcome,
            document.Task.Status.ToString(),
            task.DisplayState.ToString(),
            task.ReasonText,
            task.NeedsHumanAttention,
            owningProject.ProjectId,
            owningProject.Name,
            document.Sessions
                .OrderBy(session => session.StartedUtc)
                .Select(session => new FamiliarTaskSession(
                    session.Id,
                    session.Role.ToString(),
                    session.Status.ToString(),
                    session.Provider,
                    session.StartedUtc,
                    session.CompletedUtc,
                    session.FailureCategory is null
                        ? null
                        : new FamiliarSessionFailure(
                            session.FailureCategory,
                            session.FailureAdapterExitCode,
                            session.FailureProviderLaunched,
                            session.FailureProviderExitCode,
                            session.FailureMessage ?? "The session failed without a diagnostic message.")))
                .ToList(),
            records,
            document.TaskEntries.Count - records.Count,
            awaiting,
            document.Task.CreatedUtc,
            document.Task.UpdatedUtc,
            DiscloseTask(records.Count, document.TaskEntries.Count - records.Count, awaiting is not null));
    }

    public async Task<FamiliarSessionHandoffPlan?> GetSessionHandoffPlanAsync(
        Guid handoffId,
        int? offset = null,
        int? maxCharacters = null,
        CancellationToken cancellationToken = default)
    {
        if (handoffPlans is null)
        {
            return null;
        }

        var source = await handoffPlans.ReadAsync(handoffId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        var brief = await briefs.GetBriefAsync(source.ProjectId, cancellationToken);
        var project = brief.Projects.SingleOrDefault(candidate => candidate.ProjectId == source.ProjectId);
        if (project is null)
        {
            return null;
        }

        var document = await taskContext.GetTaskContextAsync(source.TaskId, cancellationToken);
        var artifact = document?.TaskEntries
            .Where(entry => !entry.IsSensitive
                && entry.Kind == (source.SourceRole == AgentSessionRole.Planner
                    ? ContextEntryKind.Plan
                    : source.SourceRole == AgentSessionRole.Implementer
                        ? ContextEntryKind.Implementation
                        : ContextEntryKind.Review)
                && entry.SourceSessionId == source.SourceSessionId)
            .OrderByDescending(entry => entry.CreatedUtc)
            .FirstOrDefault();

        if (document is null || artifact is null)
        {
            return null;
        }

        var completeContent = artifact.Content;
        var start = Math.Clamp(offset ?? 0, 0, completeContent.Length);
        var length = Math.Clamp(maxCharacters ?? FamiliarSessionHandoffPlanDefaults.DefaultPageLength, 1, FamiliarSessionHandoffPlanDefaults.MaxPageLength);
        var page = completeContent.Substring(start, Math.Min(length, completeContent.Length - start));

        return new FamiliarSessionHandoffPlan(
            source.HandoffId,
            source.TaskId,
            source.ProjectId,
            project.Name,
            document.Task.Title,
            $"Complete the task: {document.Task.Title}",
            document.Task.RequestedOutcome,
            source.SourceRole.ToString(),
            source.ProposedRole.ToString(),
            source.Kind.ToString(),
            source.Status.ToString(),
            artifact.Title,
            page,
            start,
            completeContent.Length,
            start + page.Length < completeContent.Length,
            $"Showing a complete bounded Planner artifact page ({start + 1}-{start + page.Length} of {completeContent.Length} characters). "
                + "Use the returned offset and page length to inspect the remainder. Raw provider input and output are never returned.");
    }

    /// <summary>
    /// Raw provider prompts and output, excluded from every external answer. The same two kinds the
    /// retrieval path excludes, for the same reason: they are the model's working material rather than
    /// the user's recorded history, and they are where an unreviewed instruction would hide.
    /// </summary>
    private static readonly ContextEntryKind[] ExcludedRecordKinds =
        [ContextEntryKind.Prompt, ContextEntryKind.RawOutput];

    private static string DiscloseTask(int shown, int withheld, bool awaitingDecision)
    {
        var disclosure = shown == 0
            ? "Nothing has been recorded about this task yet."
            : $"Showing the {shown} most recent record{(shown == 1 ? "" : "s")} about this task.";

        if (withheld > 0)
        {
            disclosure += $" {withheld} further record{(withheld == 1 ? " was" : "s were")} not shown — "
                + "older, marked sensitive, or raw provider input and output, which is never returned.";
        }

        disclosure += awaitingDecision
            ? " This task is waiting on a decision from you."
            : " Nothing on this task is waiting on a decision from you.";

        return disclosure;
    }

    private static string Excerpt(string content)
    {
        var trimmed = (content ?? string.Empty).Trim();

        return trimmed.Length <= FamiliarTaskDetail.MaxExcerptLength
            ? trimmed
            : trimmed[..FamiliarTaskDetail.MaxExcerptLength] + "… (truncated)";
    }

    /// <summary>
    /// The runtime the work runs on, assembled from the same two services the Demiplane's own pages
    /// render: the worker overview and provider capacity.
    ///
    /// <b>Why this exists.</b> A task could say "Waiting for an available Planner" on the Demiplane
    /// while the Familiar could only repeat that sentence back — it had no way to see whether a
    /// Planner-capable worker was missing, disabled, offline, or merely busy. Those are four
    /// different problems with four different fixes, and the distinction is exactly what a person
    /// asking "why?" wants. Peer frontends over one authoritative system should not differ in what
    /// they can find out (ADR-0019).
    ///
    /// <b>The one sensitivity boundary here.</b> Workers are machine state and belong to no project,
    /// so the pool itself is not withheld. What a worker is <em>busy with</em> can belong to a
    /// project this caller may not read, so the claimed task is named only when it appears in a
    /// project the standing brief was willing to show. The claim is still reported — a busy worker is
    /// a fact about the machine, and hiding it would misexplain why a role is unavailable.
    /// </summary>
    public async Task<FamiliarRuntimeState> InspectRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var pool = await workers.GetWorkersAsync(cancellationToken);
        var capacity = await providers.GetAllAsync(cancellationToken);
        var readableTasks = await ReadableTaskTitlesAsync(cancellationToken);

        var reported = pool
            .Select(worker => new FamiliarWorker(
                worker.WorkerKey,
                worker.DisplayName,
                worker.Enabled,
                worker.Capabilities.Select(role => role.ToString()).ToList(),
                worker.Availability.ToString(),
                Math.Round((DateTime.UtcNow - worker.LastHeartbeatUtc).TotalSeconds, 1),
                worker.ActiveClaim is { } claim
                    ? new FamiliarWorkerActiveWork(
                        claim.Role.ToString(),
                        readableTasks.TryGetValue(claim.TaskId, out var title) ? title : null,
                        claim.LeaseExpired)
                    : null))
            .ToList();

        var roles = Enum.GetValues<AgentSessionRole>()
            .Select(role => DescribeRole(role, pool))
            .ToList();

        return new FamiliarRuntimeState(
            reported,
            roles,
            capacity
                .Select(snapshot => new FamiliarProviderCapacity(
                    snapshot.Provider,
                    snapshot.Status.ToString(),
                    snapshot.Confidence.ToString(),
                    snapshot.Detail))
                .ToList(),
            reported.Count(worker => worker.ActiveWork is not null),
            DiscloseRuntime(reported, roles));
    }

    /// <summary>
    /// Whether a role can start now, and which of the several reasons applies when it cannot.
    ///
    /// The ordering matters: "nobody declares this role" is a different message from "the only worker
    /// that does is offline", and a reader given the second when the first is true will wait for
    /// something that is never coming.
    /// </summary>
    private static FamiliarRoleReadiness DescribeRole(AgentSessionRole role, IReadOnlyList<WorkerOverviewItem> pool)
    {
        var declaring = pool.Where(worker => worker.Capabilities.Contains(role)).ToList();
        var enabledOnline = declaring
            .Where(worker => worker.Enabled && worker.Availability == WorkerAvailability.Online)
            .ToList();
        var idle = enabledOnline.Where(worker => worker.ActiveClaim is null).ToList();

        var (blocked, explanation) = declaring.Count switch
        {
            0 => (true, $"No registered worker declares the {role} role, so {role} work cannot start "
                + "until one is registered or an existing worker is configured to run it."),

            _ when declaring.All(worker => !worker.Enabled) => (true,
                $"Every worker that can run {role} is disabled, so {role} work cannot start until one "
                + "is enabled."),

            _ when enabledOnline.Count == 0 => (true,
                $"No enabled worker that can run {role} is currently online — the most recent heartbeat "
                + "is stale or absent, which usually means the worker process is not running."),

            _ when idle.Count == 0 => (false,
                $"Every online {role} worker is already running something, so {role} work will start "
                + "when one of them finishes."),

            _ => (false, $"{idle.Count} online {role} worker(s) are idle and able to pick work up now.")
        };

        return new FamiliarRoleReadiness(
            role.ToString(), declaring.Count, enabledOnline.Count, idle.Count, blocked, explanation);
    }

    /// <summary>
    /// The titles of tasks in projects this caller may read, so a worker's current claim can be named
    /// where that is permitted and left unnamed where it is not.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ReadableTaskTitlesAsync(CancellationToken cancellationToken)
    {
        var brief = await briefs.GetBriefAsync(null, cancellationToken);

        return brief.Projects
            .SelectMany(project => project.Tasks)
            .GroupBy(task => task.TaskId)
            .ToDictionary(group => group.Key, group => group.First().Title);
    }

    private static string DiscloseRuntime(
        IReadOnlyList<FamiliarWorker> reported,
        IReadOnlyList<FamiliarRoleReadiness> roles)
    {
        if (reported.Count == 0)
        {
            return "No workers are registered, so no automated work can run at all. Nothing here is "
                + "waiting on a decision — it is waiting on a worker.";
        }

        var blocked = roles.Where(role => role.Blocked).Select(role => role.Role).ToList();

        return blocked.Count == 0
            ? "Every role has at least one enabled, online worker."
            : $"These roles cannot start work at all right now: {string.Join(", ", blocked)}. The "
              + "per-role explanation says which of registration, enablement or liveness is missing.";
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
