using System.Net;
using System.Text.RegularExpressions;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class SessionAssignmentEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Started_session_returns_utf8_markdown_with_correct_identity_and_context()
    {
        var (project, task, session) = await SeedSessionAsync(AgentSessionRole.Implementer, AgentSessionStatus.Started);

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        var markdown = await response.Content.ReadAsStringAsync();

        Assert.Contains("# Find Familiar assignment", markdown);
        Assert.Contains(project.Name, markdown);
        Assert.Contains(project.Id.ToString(), markdown);
        Assert.Contains(task.Title, markdown);
        Assert.Contains(task.Id.ToString(), markdown);
        Assert.Contains(task.RequestedOutcome, markdown);
        Assert.Contains(session.Id.ToString(), markdown);
        Assert.Contains("Implementer", markdown);
        Assert.Contains("## Canonical task context", markdown);
        Assert.Contains("## Exact role prompt", markdown);
    }

    [Theory]
    [InlineData(AgentSessionRole.Planner)]
    [InlineData(AgentSessionRole.Implementer)]
    [InlineData(AgentSessionRole.Reviewer)]
    public async Task Packet_exact_role_prompt_matches_task_page_prefilled_prompt(AgentSessionRole role)
    {
        var (_, task, session) = await SeedSessionAsync(role, AgentSessionStatus.Started);

        using var client = factory.CreateClient();

        var packetResponse = await client.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");
        var packet = await packetResponse.Content.ReadAsStringAsync();

        var pageResponse = await client.GetAsync($"/Tasks/Details/{task.Id}?sessionId={session.Id}");
        var html = await pageResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);

        var match = Regex.Match(
            html,
            "<textarea[^>]*id=\"SessionResult_Prompt\"[^>]*>(.*?)</textarea>",
            RegexOptions.Singleline);
        Assert.True(match.Success, "Expected to find the prefilled SessionResult.Prompt textarea.");
        var prefilledPrompt = WebUtility.HtmlDecode(match.Groups[1].Value);

        Assert.Contains(prefilledPrompt.Trim(), packet);
        Assert.Contains($"You are the {role} for the Find Familiar task \"{task.Title}\"", prefilledPrompt);
    }

    [Fact]
    public async Task Unknown_task_returns_not_found()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_session_returns_not_found()
    {
        var (_, task, _) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started);

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/tasks/{task.Id}/sessions/{Guid.NewGuid()}/assignment.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Session_belonging_to_sibling_task_returns_not_found()
    {
        var (project, _, session) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started);
        var siblingTask = await SeedTaskAsync(project.Id);

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/tasks/{siblingTask.Id}/sessions/{session.Id}/assignment.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_session_and_cross_task_session_return_byte_identical_not_found_bodies()
    {
        var (project, task, session) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started);
        var siblingTask = await SeedTaskAsync(project.Id);

        using var client = factory.CreateClient();

        var unknownSessionResponse = await client.GetAsync($"/tasks/{task.Id}/sessions/{Guid.NewGuid()}/assignment.md");
        var crossTaskResponse = await client.GetAsync($"/tasks/{siblingTask.Id}/sessions/{session.Id}/assignment.md");

        Assert.Equal(HttpStatusCode.NotFound, unknownSessionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTaskResponse.StatusCode);

        var unknownSessionBytes = await unknownSessionResponse.Content.ReadAsByteArrayAsync();
        var crossTaskBytes = await crossTaskResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(unknownSessionBytes, crossTaskBytes);
    }

    [Fact]
    public async Task Completed_session_returns_conflict_and_no_packet()
    {
        var (_, task, session) = await SeedSessionAsync(AgentSessionRole.Reviewer, AgentSessionStatus.Completed);

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Revision_mismatch_renders_stale_context_warning()
    {
        var (project, task, session) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started, contextRevisionRead: 0);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var trackedProject = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
            trackedProject.IncrementContextRevision();
            await dbContext.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");
        var markdown = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("STALE CONTEXT WARNING", markdown);
    }

    [Fact]
    public async Task Repeated_assignment_gets_perform_no_writes()
    {
        var (project, task, session) = await SeedSessionAsync(AgentSessionRole.Implementer, AgentSessionStatus.Started);

        int revisionBefore;
        DateTime taskUpdatedBefore;
        int entryCountBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            revisionBefore = await dbContext.Projects.Where(p => p.Id == project.Id).Select(p => p.ContextRevision).SingleAsync();
            taskUpdatedBefore = await dbContext.Tasks.Where(t => t.Id == task.Id).Select(t => t.UpdatedUtc).SingleAsync();
            entryCountBefore = await dbContext.ContextEntries.CountAsync(e => e.TaskId == task.Id);
        }

        using var client = factory.CreateClient();
        await client.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");
        await client.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        var refreshedTask = await verifyDbContext.Tasks.SingleAsync(candidate => candidate.Id == task.Id);
        var refreshedSession = await verifyDbContext.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id);
        var entryCountAfter = await verifyDbContext.ContextEntries.CountAsync(e => e.TaskId == task.Id);

        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);
        Assert.Equal(taskUpdatedBefore, refreshedTask.UpdatedUtc);
        Assert.Equal(AgentSessionStatus.Started, refreshedSession.Status);
        Assert.Equal(entryCountBefore, entryCountAfter);
    }

    [Fact]
    public async Task Task_page_offers_assignment_link_only_for_started_sessions()
    {
        var (_, task, startedSession) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var completedSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Reviewer,
            Status = AgentSessionStatus.Completed,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow.AddHours(-1),
            CompletedUtc = DateTime.UtcNow
        };
        dbContext.AgentSessions.Add(completedSession);
        await dbContext.SaveChangesAsync();

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/Tasks/Details/{task.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"/tasks/{task.Id}/sessions/{startedSession.Id}/assignment.md", html);
        Assert.DoesNotContain($"/tasks/{task.Id}/sessions/{completedSession.Id}/assignment.md", html);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedSessionAsync(
        AgentSessionRole role,
        AgentSessionStatus status,
        int contextRevisionRead = 0)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionAssignmentEndpointTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionAssignmentEndpointTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = status,
            ContextRevisionRead = contextRevisionRead,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = status == AgentSessionStatus.Completed ? DateTime.UtcNow : null
        };

        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }

    private async Task<FamiliarTask> SeedTaskAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = $"Sibling task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionAssignmentEndpointTests cross-task rejection.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }
}
