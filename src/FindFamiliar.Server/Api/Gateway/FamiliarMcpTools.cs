using System.ComponentModel;
using FindFamiliar.Server.Services.Familiar.Gateway;
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
public sealed class FamiliarMcpTools(IFamiliarGateway gateway)
{
    [McpServerTool(Name = "familiar_manifest", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Identify which Familiar you are speaking for: its name, what it is, and which read "
        + "capabilities it offers. Call this once at the start of a conversation where the user "
        + "refers to their Familiar by name or asks what you can remember for them. It returns no "
        + "project data, so it is never a substitute for searching context.")]
    public FamiliarManifest FamiliarManifest() => gateway.GetManifest();

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
        CancellationToken cancellationToken = default) =>
        gateway.SearchContextAsync(query, projectId, maxItems, cancellationToken);

    [McpServerTool(Name = "list_familiar_projects", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "List the projects this Familiar holds, with their ids, purpose, and how much is waiting on "
        + "the user. Call this when you need a project id for another tool, or when the user asks "
        + "what they are working on across everything. Projects the user has marked sensitive are "
        + "absent and counted, never named.")]
    public Task<FamiliarProjectList> ListFamiliarProjects(CancellationToken cancellationToken = default) =>
        gateway.ListProjectsAsync(cancellationToken);

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
        var project = await gateway.GetProjectContextAsync(projectId, cancellationToken);

        // A project that is sensitive and a project that does not exist answer identically. Telling
        // them apart here would disclose the existence of a record the user chose to withhold, which
        // is precisely what the sensitivity rule protects.
        return project ?? (object)new FamiliarGatewayError("No readable project has that id.");
    }
}
