using System.Text;

namespace FindFamiliar.Server.Services;

public static class MarkdownContextRenderer
{
    public static string Render(TaskContextDocument document)
    {
        var markdown = new StringBuilder();

        markdown.AppendLine($"# Find Familiar context: {document.Task.Title}");
        markdown.AppendLine();
        markdown.AppendLine($"Context revision: {document.Project.ContextRevision}");
        markdown.AppendLine();
        markdown.AppendLine("## Project");
        markdown.AppendLine();
        markdown.AppendLine($"- **Name:** {document.Project.Name}");
        markdown.AppendLine($"- **Status:** {document.Project.Status}");
        markdown.AppendLine($"- **Purpose:** {document.Project.Purpose}");
        markdown.AppendLine();
        markdown.AppendLine("## Current task");
        markdown.AppendLine();
        markdown.AppendLine($"- **Title:** {document.Task.Title}");
        markdown.AppendLine($"- **Status:** {document.Task.Status}");
        markdown.AppendLine($"- **Requested outcome:** {document.Task.RequestedOutcome}");

        AppendEntries(markdown, "Durable project context", document.ProjectEntries);
        AppendEntries(markdown, "Task context", document.TaskEntries);

        if (document.Sessions.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("## Session trail");
            markdown.AppendLine();

            foreach (var session in document.Sessions)
            {
                var provider = string.IsNullOrWhiteSpace(session.Provider) ? "Unspecified provider" : session.Provider;
                markdown.AppendLine($"- **{session.Role}** — {session.Status}; {provider}; read revision {session.ContextRevisionRead}; started {session.StartedUtc:u}");
            }
        }

        markdown.AppendLine();
        markdown.AppendLine("## Handoff instruction");
        markdown.AppendLine();
        markdown.AppendLine("Treat the facts above as durable project context. Record new decisions, plans, implementation results, and review findings as explicit context entries for the next isolated session.");

        return markdown.ToString();
    }

    private static void AppendEntries(
        StringBuilder markdown,
        string heading,
        IReadOnlyList<ContextEntryDocument> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        markdown.AppendLine();
        markdown.AppendLine($"## {heading}");

        foreach (var entry in entries)
        {
            markdown.AppendLine();
            markdown.AppendLine($"### {entry.Kind}: {entry.Title}");
            markdown.AppendLine();
            markdown.AppendLine(entry.Content.Trim());
        }
    }
}
