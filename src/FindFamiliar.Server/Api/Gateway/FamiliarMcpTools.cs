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
        + "Each result describes one decision point: which task it concerns, why the human is being "
        + "asked, what the finished session found, and which choices the workflow will actually accept. "
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
}
