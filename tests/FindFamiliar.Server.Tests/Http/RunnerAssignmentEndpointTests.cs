using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class RunnerAssignmentEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Started_session_returns_versioned_assignment_contract()
    {
        var (project, task, session) = await SeedSessionAsync(AgentSessionRole.Implementer, AgentSessionStatus.Started);

        var response = await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(task.Id, root.GetProperty("taskId").GetGuid());
        Assert.Equal(session.Id, root.GetProperty("sessionId").GetGuid());
        Assert.Equal("Implementer", root.GetProperty("role").GetString());
        Assert.Equal(0, root.GetProperty("contextRevisionRead").GetInt32());
        Assert.Contains("Implementer", root.GetProperty("rolePrompt").GetString());
        Assert.Contains("# Find Familiar assignment", root.GetProperty("assignmentMarkdown").GetString());
        Assert.Contains(project.Name, root.GetProperty("assignmentMarkdown").GetString());
    }

    [Fact]
    public async Task Assignment_endpoint_never_writes()
    {
        var (project, task, session) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started);

        int revisionBefore;
        DateTime taskUpdatedBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            revisionBefore = await dbContext.Projects.Where(p => p.Id == project.Id).Select(p => p.ContextRevision).SingleAsync();
            taskUpdatedBefore = await dbContext.Tasks.Where(t => t.Id == task.Id).Select(t => t.UpdatedUtc).SingleAsync();
        }

        await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");
        await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var refreshedProject = await verifyDbContext.Projects.SingleAsync(p => p.Id == project.Id);
        var refreshedTask = await verifyDbContext.Tasks.SingleAsync(t => t.Id == task.Id);
        var refreshedSession = await verifyDbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);

        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);
        Assert.Equal(taskUpdatedBefore, refreshedTask.UpdatedUtc);
        Assert.Equal(AgentSessionStatus.Started, refreshedSession.Status);
    }

    [Fact]
    public async Task Unknown_task_returns_not_found()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Session_belonging_to_sibling_task_returns_not_found()
    {
        var (project, _, session) = await SeedSessionAsync(AgentSessionRole.Planner, AgentSessionStatus.Started);
        var siblingTask = await SeedTaskAsync(project.Id);

        var response = await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{siblingTask.Id}/sessions/{session.Id}/assignment");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Completed_session_returns_conflict()
    {
        var (_, task, session) = await SeedSessionAsync(AgentSessionRole.Reviewer, AgentSessionStatus.Completed);
        var response = await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Cancelled_session_returns_conflict()
    {
        var (_, task, session) = await SeedSessionAsync(AgentSessionRole.Reviewer, AgentSessionStatus.Cancelled);
        var response = await SendAsync(HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);
        return await client.SendAsync(request);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedSessionAsync(
        AgentSessionRole role, AgentSessionStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for RunnerAssignmentEndpointTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for RunnerAssignmentEndpointTests.",
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
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = status == AgentSessionStatus.Started ? null : DateTime.UtcNow
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
            RequestedOutcome = "Seeded for RunnerAssignmentEndpointTests cross-task rejection.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }
}
