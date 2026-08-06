using System.Text;
using FindFamiliar.Server.Services.Demiplane;

namespace FindFamiliar.Server.Services.Familiar.Chat.Brief;

/// <summary>
/// The brief as text a model reads.
///
/// Plain lines rather than JSON. JSON would cost roughly a third more tokens for the same facts in
/// braces and quotes, and a model asked to answer in prose reads prose more readily than it reads a
/// serialised object. The one thing JSON would buy — unambiguous structure — is bought here instead
/// by fixed prefixes and a stable line shape.
///
/// Deterministic for identical state, and that matters more than it looks: the brief sits in the
/// prompt's stable head, and a writer that reordered equal elements or stamped a time into every line
/// would break the provider's prefix cache on every turn and cost roughly six times more per turn for
/// no benefit. <see cref="FamiliarStandingBrief.ObservedAt"/> is deliberately not written for exactly
/// that reason.
///
/// Ids are written because slice 5 needs them for citations, and because "the task called Foo" is
/// ambiguous across projects while an id never is.
/// </summary>
public static class FamiliarStandingBriefWriter
{
    public static string Write(FamiliarStandingBrief brief)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<standing_brief>");
        builder.AppendLine(
            "What follows is everything you can see. It is generated from this system's own records.");

        if (brief.NewestRecordedActivityUtc is { } newest)
        {
            // A date, not a timestamp: day granularity is all an answer needs, and it keeps the brief
            // identical across a day's worth of turns so the prefix cache still covers it.
            builder
                .Append("The newest record in this system is dated ")
                .Append(newest.ToString("yyyy-MM-dd"))
                .AppendLine(". Nothing here is evidence about anything after that date.");
        }

        builder.AppendLine();

        if (brief.Projects.Count == 0)
        {
            builder.AppendLine(brief.SensitiveProjectsWithheld > 0
                ? "No project is visible to you."
                : "There are no projects yet.");
        }

        foreach (var project in brief.Projects)
        {
            WriteProject(builder, project);
        }

        builder.AppendLine("<limits>");
        builder.AppendLine("These are the edges of what you know. Do not answer past them.");

        foreach (var limitation in brief.Limitations)
        {
            builder.Append("- ").AppendLine(limitation);
        }

        builder.AppendLine("</limits>");
        builder.AppendLine("</standing_brief>");

        var text = builder.ToString();

        // Trimmed to the bound rather than trusted to fit. Truncation is announced, because a brief
        // that stopped mid-project without saying so would read to a model as a complete picture of a
        // smaller system.
        return text.Length <= FamiliarStandingBrief.MaxCharacters
            ? text
            : text[..FamiliarStandingBrief.MaxCharacters]
              + "\n[This brief was truncated. Assume there is more you have not been shown.]\n</standing_brief>\n";
    }

    private static void WriteProject(StringBuilder builder, BriefProject project)
    {
        builder.Append("<project id=\"").Append(project.ProjectId).AppendLine("\">");
        builder.Append("name: ").AppendLine(project.Name);

        if (!string.IsNullOrWhiteSpace(project.Purpose))
        {
            builder.Append("purpose: ").AppendLine(Collapse(project.Purpose));
        }

        builder
            .Append("tasks: ").Append(project.TotalTasks)
            .Append(" total, ").Append(project.RunningCount).Append(" running, ")
            .Append(project.NeedsAttentionCount).AppendLine(" needing a human decision");

        if (project.LastRecordedActivityUtc is { } lastActivity)
        {
            builder
                .Append("newest record for this project: ")
                .AppendLine(lastActivity.ToString("yyyy-MM-dd"));
        }

        foreach (var task in project.Tasks)
        {
            builder
                .Append("- task ").Append(task.TaskId)
                .Append(" [").Append(Describe(task.DisplayState)).Append("] ")
                .Append(Collapse(task.Title));

            if (!string.IsNullOrWhiteSpace(task.ReasonText))
            {
                builder.Append(" — ").Append(Collapse(task.ReasonText));
            }

            builder.AppendLine();
        }

        if (project.TasksOmitted > 0)
        {
            // Stated per project, not only in the global limits: a model reading one project's block
            // must know that block is partial without having to remember a caveat from elsewhere.
            builder
                .Append("- (")
                .Append(project.TasksOmitted)
                .AppendLine(" more task(s) in this project are not listed here)");
        }

        builder.AppendLine("</project>");
        builder.AppendLine();
    }

    /// <summary>
    /// The display state in words a model will read the same way a person does, taken from the same
    /// vocabulary the Demiplane uses on screen.
    /// </summary>
    private static string Describe(TaskDisplayState state) => state switch
    {
        TaskDisplayState.NotStarted => "not started",
        TaskDisplayState.Waiting => "waiting",
        TaskDisplayState.Running => "running now",
        TaskDisplayState.NeedsAttention => "NEEDS A HUMAN DECISION",
        TaskDisplayState.Blocked => "blocked",
        TaskDisplayState.Succeeded => "done",
        _ => "failed"
    };

    /// <summary>
    /// One line of single-spaced text. A title or reason containing a newline would otherwise break
    /// the line shape this format's unambiguity rests on.
    /// </summary>
    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
