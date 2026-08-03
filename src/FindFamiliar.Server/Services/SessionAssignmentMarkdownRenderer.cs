using System.Text;
using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services;

public static class SessionAssignmentMarkdownRenderer
{
    public static string RenderRolePrompt(AgentSessionRole role, TaskContextDocument document)
    {
        var roleBody = role switch
        {
            AgentSessionRole.Planner => PlannerBody,
            AgentSessionRole.Implementer => ImplementerBody,
            AgentSessionRole.Reviewer => ReviewerBody,
            _ => throw new InvalidOperationException($"Unmapped agent session role '{role}'.")
        };

        return
            $"""
            You are the {role} for the Find Familiar task "{document.Task.Title}" (`{document.Task.Id}`).

            Requested outcome: {document.Task.RequestedOutcome}

            You have only the context above this prompt and this prompt itself. Do not ask for additional information; work from what is given.

            {roleBody}

            Return your response as:
            - a concise **Summary** of what you found or did;
            - an **artifact title**;
            - the **artifact content** itself.
            """;
    }

    public static string RenderAssignment(TaskContextDocument document, AgentSessionDocument session)
    {
        var markdown = new StringBuilder();

        markdown.AppendLine("# Find Familiar assignment");
        markdown.AppendLine();
        markdown.AppendLine("## Execution contract");
        markdown.AppendLine();
        markdown.AppendLine($"- **Project:** {document.Project.Name} (`{document.Project.Id}`)");
        markdown.AppendLine($"- **Task:** {document.Task.Title} (`{document.Task.Id}`)");
        markdown.AppendLine($"- **Requested outcome:** {document.Task.RequestedOutcome}");
        markdown.AppendLine($"- **Session:** `{session.Id}`");
        markdown.AppendLine($"- **Role:** {session.Role}");
        markdown.AppendLine($"- **Provider:** {(string.IsNullOrWhiteSpace(session.Provider) ? "Unspecified provider" : session.Provider)}");
        markdown.AppendLine($"- **External session reference:** {(string.IsNullOrWhiteSpace(session.ExternalSessionReference) ? "None" : session.ExternalSessionReference)}");
        markdown.AppendLine($"- **Session status:** {session.Status}");
        markdown.AppendLine($"- **Context revision read at session start:** {session.ContextRevisionRead}");
        markdown.AppendLine($"- **Current project context revision:** {document.Project.ContextRevision}");

        if (session.ContextRevisionRead != document.Project.ContextRevision)
        {
            markdown.AppendLine();
            markdown.AppendLine("> **STALE CONTEXT WARNING:** The project context revision has changed since this session started " +
                $"(read revision {session.ContextRevisionRead}, current revision {document.Project.ContextRevision}). " +
                "Stop and start a fresh session before doing any work, so the worker reads current context.");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Exact role prompt");
        markdown.AppendLine();
        markdown.AppendLine(RenderRolePrompt(session.Role, document));

        markdown.AppendLine();
        markdown.AppendLine("## Required result");
        markdown.AppendLine();
        markdown.AppendLine("Give the worker's complete visible response back to Find Familiar through the session result form. It will be stored " +
            "as bounded raw output, together with the Summary and the role artifact described above. Do not request or store hidden reasoning, " +
            "tool chatter, credentials, or a full conversation transcript — only the response needed to reproduce the worker's conclusions.");

        markdown.AppendLine();
        markdown.AppendLine("## Canonical task context");
        markdown.AppendLine();
        markdown.AppendLine(MarkdownContextRenderer.Render(document));

        return markdown.ToString();
    }

    private const string PlannerBody =
        """
        Inspect the repository and the context above. Do not edit any files. Design the smallest change that
        achieves the requested outcome, respecting every constraint and decision already recorded as durable
        context. Return a concrete Plan artifact.
        """;

    private const string ImplementerBody =
        """
        Inspect the repository and the context above, then implement the requested outcome within the constraints
        already recorded as durable context. Verify your work (build, tests, or other checks appropriate to the
        change). Return an Implementation artifact describing what you changed and how you verified it.
        """;

    private const string ReviewerBody =
        """
        Independently inspect the repository and the context above. Do not edit any files. Verify that the work
        described in durable context actually satisfies the requested outcome and its constraints. Return a
        Review artifact with an explicit Approve or Request changes verdict and your supporting findings.
        """;
}
