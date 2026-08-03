using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

public sealed class MarkdownContextRendererTests
{
    [Fact]
    public void Render_includes_project_and_task_identity()
    {
        var document = BuildDocument();

        var markdown = MarkdownContextRenderer.Render(document);

        Assert.Contains($"# Find Familiar context: {document.Task.Title}", markdown);
        Assert.Contains($"- **Name:** {document.Project.Name}", markdown);
        Assert.Contains($"- **Title:** {document.Task.Title}", markdown);
    }

    [Fact]
    public void Render_includes_session_lifecycle_fields()
    {
        var startedUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var completedUtc = new DateTime(2026, 8, 1, 13, 30, 0, DateTimeKind.Utc);
        var sessionId = Guid.NewGuid();

        var session = new AgentSessionDocument(
            sessionId,
            AgentSessionRole.Reviewer,
            "Claude Code",
            null,
            AgentSessionStatus.Completed,
            ContextRevisionRead: 4,
            startedUtc,
            completedUtc);

        var document = BuildDocument(sessions: [session]);

        var markdown = MarkdownContextRenderer.Render(document);

        Assert.Contains($"**Reviewer** (`{sessionId}`)", markdown);
        Assert.Contains("Completed", markdown);
        Assert.Contains("Claude Code", markdown);
        Assert.Contains("read revision 4", markdown);
        Assert.Contains($"started {startedUtc:u}", markdown);
        Assert.Contains($"completed {completedUtc:u}", markdown);
    }

    [Fact]
    public void Render_falls_back_to_unspecified_provider_when_provider_is_null()
    {
        var session = new AgentSessionDocument(
            Guid.NewGuid(),
            AgentSessionRole.Planner,
            null,
            null,
            AgentSessionStatus.Started,
            ContextRevisionRead: 0,
            DateTime.UtcNow,
            null);

        var document = BuildDocument(sessions: [session]);

        var markdown = MarkdownContextRenderer.Render(document);

        Assert.Contains("Unspecified provider", markdown);
        Assert.DoesNotContain("completed", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_marks_a_linked_entry_with_its_source_session_id()
    {
        var sourceSessionId = Guid.NewGuid();
        var createdUtc = new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc);
        var entry = new ContextEntryDocument(
            Guid.NewGuid(),
            ContextEntryKind.Implementation,
            "Linked entry",
            "Bounded raw output content.",
            createdUtc,
            sourceSessionId);

        var document = BuildDocument(taskEntries: [entry]);

        var markdown = MarkdownContextRenderer.Render(document);

        Assert.Contains($"source session `{sourceSessionId}`", markdown);
        Assert.Contains($"Created {createdUtc:u}", markdown);
        Assert.Contains("Bounded raw output content.", markdown);
    }

    [Fact]
    public void Render_marks_an_entry_without_a_source_session_as_unlinked_human()
    {
        var entry = new ContextEntryDocument(
            Guid.NewGuid(),
            ContextEntryKind.Goal,
            "Human entry",
            "Written directly by a person, not an AI session.",
            DateTime.UtcNow,
            SourceSessionId: null);

        var document = BuildDocument(projectEntries: [entry]);

        var markdown = MarkdownContextRenderer.Render(document);

        Assert.Contains("Unlinked/human", markdown);
    }

    private static TaskContextDocument BuildDocument(
        IReadOnlyList<ContextEntryDocument>? projectEntries = null,
        IReadOnlyList<ContextEntryDocument>? taskEntries = null,
        IReadOnlyList<AgentSessionDocument>? sessions = null)
    {
        var project = new ProjectContextDocument(
            Guid.NewGuid(),
            "Find Familiar",
            "Prove Markdown provenance rendering.",
            ProjectStatus.Active,
            ContextRevision: 3);

        var task = new TaskContextTaskDocument(
            Guid.NewGuid(),
            "Add regression coverage for the durable workflow",
            "Protect the durable workflow with automated regression coverage.",
            TaskStatus.InProgress,
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow);

        return new TaskContextDocument(
            project,
            task,
            projectEntries ?? [],
            taskEntries ?? [],
            sessions ?? []);
    }
}
