using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FindFamiliar.Server.Api.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class RunnerResultEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Theory]
    [InlineData(AgentSessionRole.Planner, ContextEntryKind.Plan)]
    [InlineData(AgentSessionRole.Implementer, ContextEntryKind.Implementation)]
    [InlineData(AgentSessionRole.Reviewer, ContextEntryKind.Review)]
    public async Task Valid_result_captures_four_entries_completes_session_and_maps_role(
        AgentSessionRole role, ContextEntryKind expectedKind)
    {
        var (project, task, session) = await SeedStartedSessionAsync(role);

        var response = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result",
            ValidResultBody());

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, response.StatusCode.ToString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync();

        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, e => e.Kind == expectedKind && e.Title == "Artifact title");

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Completed, refreshedSession.Status);

        var refreshedProject = await dbContext.Projects.SingleAsync(p => p.Id == project.Id);
        Assert.Equal(1, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Route_ids_are_authoritative_even_if_body_carried_ids_would_differ()
    {
        // The result contract has no ID fields at all, so there is nothing to spoof — this test
        // documents that the shape itself enforces route authority.
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", ValidResultBody());

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_task_or_session_returns_not_found_and_writes_nothing()
    {
        var response = await PostAsync(
            $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/result", ValidResultBody());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cross_task_session_returns_not_found_and_writes_nothing()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var siblingTask = await SeedTaskAsync(project.Id);

        var response = await PostAsync(
            $"/api/runner/tasks/{siblingTask.Id}/sessions/{session.Id}/result", ValidResultBody());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Non_started_session_returns_conflict_and_writes_nothing()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var first = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", ValidResultBody());
        Assert.True(first.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        var replay = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", ValidResultBody());
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(4, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Missing_field_returns_bad_request_and_writes_nothing()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var body = """{"contractVersion":1,"prompt":"P","rawOutput":"R","summary":"S","artifactTitle":"T"}""";
        var response = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Oversized_field_returns_bad_request_and_writes_nothing()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var oversized = new string('x', 12_001);
        var body = $$"""{"contractVersion":1,"prompt":"P","rawOutput":"{{oversized}}","summary":"S","artifactTitle":"T","artifactContent":"C"}""";
        var response = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Malformed_json_returns_bad_request()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Contract_version_mismatch_returns_bad_request()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var body = """{"contractVersion":99,"prompt":"P","rawOutput":"R","summary":"S","artifactTitle":"T","artifactContent":"C"}""";
        var response = await PostAsync($"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Oversized_request_body_is_rejected()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/result");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);
        // Sized from the contract rather than a literal, so this keeps testing oversized handling if
        // the bound moves again — it rose to 1 MB when the complete artifact began travelling with the
        // result, and a fixture pinned to the old 64 KB limit would have quietly become a test that a
        // large valid body is accepted.
        request.Content = new StringContent(
            new string('x', RunnerContracts.MaxRequestBodyBytes + 1024),
            Encoding.UTF8,
            "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertNoWritesAsync(session.Id);
    }

    private static string ValidResultBody() =>
        """{"contractVersion":1,"prompt":"The exact prompt.","rawOutput":"A bounded raw output excerpt.","summary":"A concise summary.","artifactTitle":"Artifact title","artifactContent":"Artifact content."}""";

    private async Task<HttpResponseMessage> PostAsync(string url, string jsonBody)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private async Task AssertNoWritesAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == sessionId));
        var session = await dbContext.AgentSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for RunnerResultEndpointTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for RunnerResultEndpointTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
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
            RequestedOutcome = "Seeded for RunnerResultEndpointTests cross-task rejection.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }
}
