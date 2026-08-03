using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

public sealed class SessionAssignmentMarkdownRendererTests
{
    [Theory]
    [InlineData(AgentSessionRole.Planner, "Plan artifact")]
    [InlineData(AgentSessionRole.Implementer, "Implementation artifact")]
    [InlineData(AgentSessionRole.Reviewer, "Review artifact")]
    public void RenderRolePrompt_gives_role_specific_instructions(AgentSessionRole role, string _)
    {
        var document = BuildDocument();

        var prompt = SessionAssignmentMarkdownRenderer.RenderRolePrompt(role, document);

        Assert.Contains($"You are the {role}", prompt);
        Assert.Contains(document.Task.Title, prompt);
        Assert.Contains(document.Task.RequestedOutcome, prompt);

        switch (role)
        {
            case AgentSessionRole.Planner:
                Assert.Contains("Do not edit any files", prompt);
                Assert.Contains("Plan artifact", prompt);
                break;
            case AgentSessionRole.Implementer:
                Assert.Contains("implement the requested outcome", prompt);
                Assert.Contains("Implementation artifact", prompt);
                break;
            case AgentSessionRole.Reviewer:
                Assert.Contains("Do not edit any files", prompt);
                Assert.Contains("Approve or Request changes", prompt);
                Assert.Contains("Review artifact", prompt);
                break;
        }
    }

    [Fact]
    public void RenderAssignment_contains_identity_role_and_revision_values()
    {
        var session = BuildSession(AgentSessionRole.Implementer, contextRevisionRead: 3);
        var document = BuildDocument(contextRevision: 3, sessions: [session]);

        var markdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        Assert.Contains("# Find Familiar assignment", markdown);
        Assert.Contains(document.Project.Name, markdown);
        Assert.Contains(document.Project.Id.ToString(), markdown);
        Assert.Contains(document.Task.Title, markdown);
        Assert.Contains(document.Task.Id.ToString(), markdown);
        Assert.Contains(document.Task.RequestedOutcome, markdown);
        Assert.Contains(session.Id.ToString(), markdown);
        Assert.Contains("Implementer", markdown);
        Assert.Contains("read at session start:** 3", markdown);
        Assert.Contains("Current project context revision:** 3", markdown);
        Assert.Contains("## Exact role prompt", markdown);
        Assert.Contains("## Required result", markdown);
        Assert.Contains("## Canonical task context", markdown);
        Assert.DoesNotContain("STALE CONTEXT WARNING", markdown);
    }

    [Fact]
    public void RenderAssignment_shows_explicit_fallback_text_for_missing_provider_and_reference()
    {
        var session = BuildSession(AgentSessionRole.Implementer, contextRevisionRead: 0);
        var document = BuildDocument(sessions: [session]);

        var markdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        Assert.Contains("**Provider:** Unspecified provider", markdown);
        Assert.Contains("**External session reference:** None", markdown);
    }

    [Fact]
    public void RenderAssignment_shows_stale_warning_when_revisions_differ()
    {
        var session = BuildSession(AgentSessionRole.Reviewer, contextRevisionRead: 1);
        var document = BuildDocument(contextRevision: 5, sessions: [session]);

        var markdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        Assert.Contains("STALE CONTEXT WARNING", markdown);
        Assert.Contains("read revision 1", markdown);
        Assert.Contains("current revision 5", markdown);
    }

    [Fact]
    public void RenderAssignment_exact_role_prompt_matches_RenderRolePrompt_output()
    {
        var session = BuildSession(AgentSessionRole.Planner, contextRevisionRead: 0);
        var document = BuildDocument(sessions: [session]);

        var expectedPrompt = SessionAssignmentMarkdownRenderer.RenderRolePrompt(session.Role, document);
        var markdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        Assert.Contains(expectedPrompt, markdown);
    }

    [Fact]
    public void RenderAssignment_includes_canonical_context_renderer_output()
    {
        var session = BuildSession(AgentSessionRole.Planner, contextRevisionRead: 0);
        var document = BuildDocument(sessions: [session]);

        var canonicalContext = MarkdownContextRenderer.Render(document);
        var markdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        Assert.Contains(canonicalContext.Trim(), markdown);
    }

    private static AgentSessionDocument BuildSession(AgentSessionRole role, int contextRevisionRead)
    {
        return new AgentSessionDocument(
            Guid.NewGuid(),
            role,
            Provider: null,
            ExternalSessionReference: null,
            AgentSessionStatus.Started,
            contextRevisionRead,
            DateTime.UtcNow,
            CompletedUtc: null);
    }

    private static TaskContextDocument BuildDocument(
        int contextRevision = 0,
        IReadOnlyList<AgentSessionDocument>? sessions = null)
    {
        var project = new ProjectContextDocument(
            Guid.NewGuid(),
            "Find Familiar",
            "Prove assignment packet rendering.",
            ProjectStatus.Active,
            contextRevision);

        var task = new TaskContextTaskDocument(
            Guid.NewGuid(),
            "Generate session assignment packets",
            "A Started session exposes one authoritative Markdown assignment packet.",
            TaskStatus.InProgress,
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow);

        return new TaskContextDocument(project, task, [], [], sessions ?? []);
    }
}
