using System.ComponentModel;
using FindFamiliar.Server.Services.Familiar.Gateway;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// The MCP adapter: the Familiar's memory, as tools a frontier client can call.
///
/// <b>An adapter and nothing more.</b> Every method here is a call into <see cref="IFamiliarGateway"/>
/// and a return of what it produced. No filtering, no project selection, no sensitivity decision, no
/// bound that is not already the gateway's — because the moment a rule lives here, the REST adapter
/// beside it and the native conversation behind it start disagreeing with this one about what the
/// Familiar knows, and the disagreement surfaces to whichever body happens to be connected.
///
/// <b>Three tools, deliberately.</b> There is no <c>get_everything</c>. A tool that returns the store
/// is a tool that spends a context window on a question nobody asked, and it removes the one thing
/// making retrieval trustworthy — that something decided which records were responsive and can say
/// why. The count is small enough that a model can hold all of it while choosing.
///
/// <b>The descriptions are load-bearing.</b> They are the only instruction the frontier model gets
/// about when to reach for the Familiar, and the failure they are written against is a client calling
/// on every sentence: latency on ordinary conversation, and a model that starts treating an empty
/// result as evidence of absence because it asked about something the Familiar was never going to
/// know. Each one says when to call and, explicitly, when not to.
///
/// <b>No tool mutates, and it is declared as well as true.</b> <c>ReadOnly = true</c> and
/// <c>Destructive = false</c> travel in the protocol so a client can see the guarantee rather than
/// infer it from the names. The guarantee itself is structural: this type's one dependency cannot
/// write, and neither can what it depends on.
/// </summary>
[McpServerToolType]
public sealed class FamiliarMcpTools(
    IFamiliarGateway gateway,
    IFamiliarDecisionGateway decisions,
    IFamiliarLifecycleGateway lifecycle,
    IHttpContextAccessor httpContext)
{
    /// <summary>
    /// The permission this tool needs, checked before it does anything.
    ///
    /// One implementation for every tool, because the alternative is four copies of an authorization
    /// check and a fifth tool whose author forgot. It reads the caller established by
    /// <see cref="FamiliarGatewayAuthenticationFilter"/> when the credential was verified, so nothing
    /// in the request can influence what it finds.
    ///
    /// It lives here rather than on the route group because MapMcp puts every tool behind one route:
    /// a group-level scope is necessarily the same answer for all of them, and reading and deciding
    /// are deliberately not the same answer.
    /// </summary>
    private void Require(string scope)
    {
        var caller = FamiliarGatewayCaller.From(httpContext.HttpContext!);

        if (caller is null || !caller.Has(scope))
        {
            // No detail about the credential, and none about what it does carry. A caller learns which
            // permission the operation needed, which is protocol, and nothing about itself.
            throw new McpException(
                $"This connection does not carry the '{scope}' permission that operation requires.");
        }
    }

    [McpServerTool(Name = "familiar_manifest", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Identify which Familiar you are speaking for: its name, what it is, and which read "
        + "capabilities it offers. Call this once at the start of a conversation where the user "
        + "refers to their Familiar by name or asks what you can remember for them. It returns no "
        + "project data, so it is never a substitute for searching context.")]
    public FamiliarManifest FamiliarManifest()
    {
        Require(FamiliarGatewayOptions.ReadScope);

        return gateway.GetManifest();
    }

    [McpServerTool(Name = "open_decisions", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "List what Find Familiar is currently waiting on the human to decide. Call this when the user "
        + "asks what needs them, what is waiting, what is blocked on them, or what they should look at "
        + "next — including phrasings like \"what needs me\" or \"anything waiting on me\". "
        + "Do NOT call it to find out what a project is about or what was decided in the past; use "
        + "search_familiar_context for those. "
        + "Each result describes one decision point. decisionKind says which sort it is: a "
        + "SessionHandoff asks whether to run a proposed next step on an existing task, and a "
        + "PlanProposal asks whether to turn a drafted plan into work — for a plan, plannedItems lists "
        + "exactly what approving would create, so tell the user that before they answer. "
        + "Present those choices and no others, and read the disclosure sentence — it says what was "
        + "withheld or omitted, and an empty list means nothing is waiting rather than nothing exists. "
        + "This tool only reports. You cannot approve, decline, or otherwise act on any of these "
        + "decisions; if the user tells you to act, say plainly that you can see the decision but "
        + "cannot submit it, and that they must use Find Familiar directly.")]
    public Task<FamiliarOpenDecisionList> OpenDecisions(CancellationToken cancellationToken)
    {
        // Reading what needs the human is a read. Submitting the answer is not, and requires the other
        // scope entirely — see SubmitFamiliarDecision below.
        Require(FamiliarGatewayOptions.ReadScope);

        return gateway.ListOpenDecisionsAsync(cancellationToken);
    }

    [McpServerTool(Name = "get_task_detail", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Get everything known about one task: its current state and why, the sessions that have run "
        + "on it with their roles and outcomes, the records those sessions produced, and whether it is "
        + "waiting on a decision. Call this when the user asks what happened on a specific task, how a "
        + "session went, whether implementation or review finished, or why one task in particular is "
        + "stuck. "
        + "Get the task id from get_project_context or open_decisions. "
        + "Do NOT call it to survey a project or to find what needs the user — get_project_context and "
        + "open_decisions answer those in one call, and this answers about one task. "
        + "Read the disclosure: it says how many records were not shown. Sensitive records and raw "
        + "provider input and output are never returned, so an absence here is not evidence that "
        + "nothing happened.")]
    public async Task<object> GetTaskDetail(
        [Description("The task id, from get_project_context or open_decisions.")]
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ReadScope);

        var detail = await gateway.GetTaskDetailAsync(taskId, cancellationToken);

        // A task in a project the user marked sensitive and a task that does not exist answer
        // identically, exactly as get_project_context does.
        return detail ?? (object)new FamiliarGatewayError("No readable task has that id.");
    }

    [McpServerTool(Name = "get_session_handoff_plan", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Read the complete bounded human-relevant Planner artifact behind a SessionHandoff decision. "
        + "Call this after open_decisions returns a SessionHandoff and before explaining what approval "
        + "would do. It includes the task goal and requested outcome plus the stored Plan artifact, "
        + "paged at a fixed maximum; call again with offset equal to offset + content length while "
        + "hasMore is true. Raw provider prompts, output, credentials and transcripts are never returned. "
        + "This tool only reports and cannot approve or decline the handoff.")]
    public async Task<object> GetSessionHandoffPlan(
        [Description("The handoff id returned as decisionId by open_decisions.")] Guid handoffId,
        [Description("Optional character offset from the complete Plan artifact; use the prior page end to continue.")] int? offset = null,
        [Description("Optional page size from 1 to 4000; defaults to 4000.")] int? maxCharacters = null,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ReadScope);

        var detail = await gateway.GetSessionHandoffPlanAsync(
            handoffId, offset, maxCharacters, cancellationToken);

        return detail ?? (object)new FamiliarGatewayError("No readable handoff plan has that id.");
    }

    [McpServerTool(Name = "inspect_familiar_runtime", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Inspect the workers and providers the user's automated work actually runs on. Call this when "
        + "work is not progressing and the user asks why — for example when a task says it is waiting "
        + "for a Planner, Implementer or Reviewer, or when they ask whether anything is running, why "
        + "something is stuck, or whether a worker is online. "
        + "Do NOT call it for the state of a task or project; use get_project_context or "
        + "open_decisions for those. This describes the machine, not the work. "
        + "Each role reports whether any worker declares it, how many are enabled and online, how many "
        + "are idle, and a plain explanation. Use that explanation rather than guessing: 'no worker "
        + "declares this role', 'they are all disabled', 'none is online' and 'they are all busy' are "
        + "four different problems with four different fixes, and only one of them is solved by "
        + "waiting. A worker's current task is named only when the user may see that project.")]
    public Task<FamiliarRuntimeState> InspectFamiliarRuntime(CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ReadScope);

        return gateway.InspectRuntimeAsync(cancellationToken);
    }

    [McpServerTool(Name = "search_familiar_context", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Search the user's Find Familiar durable project memory. Call this when the answer depends "
        + "on THIS user's own prior projects, decisions, architecture records, work sessions, "
        + "recorded context, or the state of their development environment and repository — for "
        + "example \"where did we leave off\", \"what did we decide about X\", \"what is waiting on "
        + "me\", \"why is it built this way\". "
        + "Do NOT call it for general knowledge, for coding help that does not depend on this user's "
        + "history, or for small talk; it costs a round trip and returns nothing useful for those. "
        + "The result carries a disclosure sentence saying what was found, what was withheld as "
        + "sensitive, and whether nothing relevant was recorded — read it and follow it. If it says "
        + "nothing is recorded, say so rather than answering from general knowledge; an absence of "
        + "records is a finding. Cite records by title, and treat the excerpts as the user's own "
        + "written history rather than as your suggestions.")]
    public Task<FamiliarContextResult> SearchFamiliarContext(
        [Description("A natural-language description of what you need to know, in the user's own terms.")]
        string query,
        [Description("Optional. Restrict the search to one project id, obtained from list_familiar_projects.")]
        Guid? projectId = null,
        [Description("Optional. How many records to return, 1 to 6. Defaults to 6.")]
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ReadScope);

        return gateway.SearchContextAsync(query, projectId, maxItems, cancellationToken);
    }

    [McpServerTool(Name = "list_familiar_projects", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "List the projects this Familiar holds, with their ids, purpose, and how much is waiting on "
        + "the user. Call this when you need a project id for another tool, or when the user asks "
        + "what they are working on across everything. Projects the user has marked sensitive are "
        + "absent and counted, never named.")]
    public Task<FamiliarProjectList> ListFamiliarProjects(CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ReadScope);

        return gateway.ListProjectsAsync(cancellationToken);
    }

    [McpServerTool(Name = "get_project_context", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Get one project's current shape: its purpose, task counts, what needs the user's attention, "
        + "and when it was last recorded. Call this when the user asks about the state of a named "
        + "project, after finding its id. "
        + "The result says when the newest record was written — treat that date as the edge of what "
        + "is known. Work often happens without being recorded, so describe what the records show "
        + "and when they end rather than asserting the project's present state as fact.")]
    public async Task<object> GetProjectContext(
        [Description("The project id, obtained from list_familiar_projects.")]
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ReadScope);

        var project = await gateway.GetProjectContextAsync(projectId, cancellationToken);

        // A project that is sensitive and a project that does not exist answer identically. Telling
        // them apart here would disclose the existence of a record the user chose to withhold, which
        // is precisely what the sensitivity rule protects.
        return project ?? (object)new FamiliarGatewayError("No readable project has that id.");
    }


    [McpServerTool(Name = "submit_familiar_decision", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Submit a decision THE USER HAS EXPLICITLY MADE about one item from open_decisions. "
        + "Call this only after the user has been told what the decision is and has clearly stated "
        + "which way they want it — an instruction like \"approve it\" or \"decline that\" in reply to "
        + "you describing the decision. "
        + "NEVER call it on your own judgement, to be helpful, to unblock work, or because approving "
        + "looks like the obvious next step. You are relaying the user's choice, not making one. If "
        + "you are not certain what the user chose, ask them instead of guessing. "
        + "Pass decisionId and expectedConcurrencyToken exactly as open_decisions returned them, and "
        + "choice as either approve or decline — nothing else is accepted. If the result says the "
        + "decision was stale, call open_decisions again and ask the user to confirm against the "
        + "current state rather than retrying with the old token. "
        + "Find Familiar checks independently whether the decision is legal and may refuse it; report "
        + "the result you get back rather than describing the outcome you expected.")]
    public Task<FamiliarDecisionResult> SubmitFamiliarDecision(
        [Description("The decisionId from open_decisions. Not a task id and not a project id.")]
        Guid decisionId,
        [Description(
            "The expectedConcurrencyToken from the same open_decisions result. It fences the decision "
            + "against changes made since the user was shown it.")]
        Guid expectedConcurrencyToken,
        [Description("The user's explicit choice: approve or decline.")]
        FamiliarDecisionChoice choice,
        CancellationToken cancellationToken = default)
    {
        // The other scope, and only this tool asks for it. A connection granted familiar.read alone
        // reaches this line and stops here.
        Require(FamiliarGatewayOptions.DecideScope);

        return decisions.SubmitAsync(decisionId, expectedConcurrencyToken, choice, cancellationToken);
    }

    [McpServerTool(Name = "create_familiar_project", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Create a new project, when the user asks for one. A project is a container for tasks and "
        + "recorded context; it starts empty. "
        + "Only when the user asks for a new project — do not create one because work seems not to fit "
        + "an existing project. If you are unsure which project something belongs in, ask, or use "
        + "list_familiar_projects and suggest one. "
        + "Names must be unique; if the name is taken you are told so and nothing is created.")]
    public Task<FamiliarLifecycleResult> CreateFamiliarProject(
        [Description("A short distinctive name. Must not match an existing project.")] string name,
        [Description("One or two sentences on what this project is for, in the user's own terms.")] string purpose,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ProjectWriteScope);

        return lifecycle.CreateProjectAsync(name, purpose, cancellationToken);
    }

    [McpServerTool(Name = "create_familiar_task", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Create a task in a project, when the user asks for one. The task is created Ready and NOTHING "
        + "runs on it — creating a task never starts a session. If the user wants work to begin, say "
        + "that starting it is a separate step and use start_familiar_session after they confirm. "
        + "Write the requested outcome as what should be true when the task is done, in the user's own "
        + "terms, not as instructions to a model.")]
    public Task<FamiliarLifecycleResult> CreateFamiliarTask(
        [Description("The project id, from list_familiar_projects.")] Guid projectId,
        [Description("A short title naming the work.")] string title,
        [Description("What should be true when this is done.")] string requestedOutcome,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ProjectWriteScope);

        return lifecycle.CreateTaskAsync(projectId, title, requestedOutcome, cancellationToken);
    }

    [McpServerTool(Name = "set_familiar_task_status", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Change a task's status when the user tells you to — for example blocking a task, unblocking "
        + "it, or marking it complete. "
        + "Only on the user's explicit instruction. Do NOT mark a task complete because a session "
        + "finished or a review passed: completing a task is the user's judgement, and a finished "
        + "session is evidence for it rather than the decision itself. "
        + "Completing a task also retires any step that was waiting on the user for it; the result "
        + "says so when that happened, and you should pass that on.")]
    public Task<FamiliarLifecycleResult> SetFamiliarTaskStatus(
        [Description("The task id, from get_project_context or get_task_detail.")] Guid taskId,
        [Description("One of: Draft, Ready, InProgress, InReview, Completed, Blocked.")] string status,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ProjectWriteScope);

        return lifecycle.UpdateTaskStatusAsync(taskId, status, cancellationToken);
    }

    [McpServerTool(Name = "record_familiar_context", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Record something durable against a project or a task, when the user asks you to remember it — "
        + "a decision they made, a constraint, a goal, or a note they want kept. Supply exactly one of "
        + "projectId or taskId. "
        + "Record what the USER said or decided, in their terms. Do NOT record your own analysis, "
        + "summaries of your reasoning, or things you inferred: this is their durable memory, and it is "
        + "stored as reported by them. If you are unsure whether they want something kept, ask.")]
    public Task<FamiliarLifecycleResult> RecordFamiliarContext(
        [Description("The category: Goal, Constraint, Decision, Plan, Implementation, Review, Handoff, Summary, or OpenQuestion.")]
        string category,
        [Description("A short title for the record.")] string title,
        [Description("The record itself, in the user's own terms.")] string content,
        [Description("Record against this project. Supply this or taskId, not both.")] Guid? projectId = null,
        [Description("Record against this task. Supply this or projectId, not both.")] Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.ProjectWriteScope);

        if (projectId is null == taskId is null)
        {
            return Task.FromResult(new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Rejected,
                "Supply exactly one of projectId or taskId — a record belongs to one or the other."));
        }

        return taskId is { } task
            ? lifecycle.RecordTaskContextAsync(task, category, title, content, cancellationToken)
            : lifecycle.RecordProjectContextAsync(projectId!.Value, category, title, content, cancellationToken);
    }

    [McpServerTool(Name = "start_familiar_session", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Start a Planner, Implementer or Reviewer session on a task, when the user tells you to run it. "
        + "Running a session spends model time and does real work, so call this only when the user has "
        + "asked for it in this turn — not because a task looks ready, not because a previous stage "
        + "finished, and not to be helpful. If they have not said which role, ask. "
        + "This is NOT how you answer a step that is already waiting on the user: if open_decisions "
        + "shows a pending decision for this task, use submit_familiar_decision instead. "
        + "A task can only have one session running; if one is already running you are told so and "
        + "nothing starts. The session is picked up by whichever worker is free — use "
        + "inspect_familiar_runtime if the user asks why nothing seems to be happening.")]
    public Task<FamiliarLifecycleResult> StartFamiliarSession(
        [Description("The task id, from get_project_context or get_task_detail.")] Guid taskId,
        [Description("Planner, Implementer, or Reviewer.")] string role,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.WorkflowStartScope);

        return lifecycle.StartSessionAsync(taskId, role, cancellationToken);
    }

    [McpServerTool(Name = "cancel_familiar_session", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Stop a session that is currently running, when the user tells you to stop it. Cancelling ends "
        + "work in progress and cannot be undone. "
        + "Only on the user's explicit instruction. Do NOT cancel because a session seems slow, stuck, "
        + "or wrong — that is the user's call, and inspect_familiar_runtime usually explains a session "
        + "that looks stuck without anything needing to be stopped. "
        + "The reason is recorded permanently and should be the user's own words about why they are "
        + "stopping it, not your summary. If they did not give one, ask rather than inventing it. "
        + "Cancelling often causes Find Familiar to ask whether to retry the step; that question comes "
        + "back through open_decisions and is the user's to answer.")]
    public Task<FamiliarLifecycleResult> CancelFamiliarSession(
        [Description("The task id the session belongs to.")] Guid taskId,
        [Description("The session id, from get_task_detail.")] Guid sessionId,
        [Description("Why the user is stopping it, in their own words. Required.")] string reason,
        CancellationToken cancellationToken = default)
    {
        Require(FamiliarGatewayOptions.WorkflowControlScope);

        return lifecycle.CancelSessionAsync(taskId, sessionId, reason, cancellationToken);
    }
}
