using System.Text;
using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services;

/// <summary>
/// Builds the plain, templated text Familiar shows in a conversation.
///
/// These are templates, not generated prose: no model produces them, and they carry only what the
/// user already supplied plus the structured proposal. Razor encodes the result on output, so the
/// composer never needs to escape and must never be given raw HTML to emit.
/// </summary>
public static class ProposalMessageComposer
{
    public const string NothingStartedNotice = "Nothing has been created or started yet.";

    private const string UnresolvedProject = "not selected yet — choose a project before approving";

    public static string InitialResponse(string? projectName, string title, string requestedOutcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine("I turned your request into a proposal for review.");
        builder.AppendLine();
        AppendProposal(builder, projectName, title, requestedOutcome);
        builder.AppendLine();
        builder.AppendLine(NothingStartedNotice);
        builder.Append("Revise it, approve it, or reject it. Work begins only when you approve.");
        return Bound(builder.ToString());
    }

    public static string RevisionSummary(int revision, string? projectName, string title, string requestedOutcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Proposal revised (revision {revision}).");
        builder.AppendLine();
        AppendProposal(builder, projectName, title, requestedOutcome);
        builder.AppendLine();
        builder.Append(NothingStartedNotice);
        return Bound(builder.ToString());
    }

    public static string ContextRefreshed(string projectName, int previousRevision, int currentRevision)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"Project context for '{projectName}' changed from revision {previousRevision} to revision {currentRevision}.");
        builder.AppendLine();
        builder.AppendLine("The proposal now observes the current context. Review it again before approving.");
        builder.Append(NothingStartedNotice);
        return Bound(builder.ToString());
    }

    public static string Approved(string projectName, string title, int contextRevisionRead)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Approved.");
        builder.AppendLine();
        builder.AppendLine($"Created one Ready task '{title}' in project '{projectName}'.");
        builder.AppendLine(
            $"Started one Planner session against project context revision {contextRevisionRead}.");
        builder.Append("An eligible worker will discover and run it automatically. No later role starts on its own.");
        return Bound(builder.ToString());
    }

    public static string Rejected()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Rejected.");
        builder.AppendLine();
        builder.Append("No task and no session were created. This conversation is closed.");
        return Bound(builder.ToString());
    }

    private static void AppendProposal(
        StringBuilder builder,
        string? projectName,
        string title,
        string requestedOutcome)
    {
        builder.AppendLine($"Project: {(string.IsNullOrWhiteSpace(projectName) ? UnresolvedProject : projectName)}");
        builder.AppendLine($"Task title: {title}");
        builder.AppendLine($"Session role: {AgentSessionRole.Planner}");
        builder.AppendLine("Requested outcome:");
        builder.AppendLine(requestedOutcome);
    }

    private static string Bound(string value) =>
        DeterministicProposalGenerator.TruncateOnTextElementBoundary(
            value,
            ConversationMessage.MaxContentLength);
}
