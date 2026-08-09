using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class RunnerCancelEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Valid_cancellation_records_one_handoff_and_marks_cancelled()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Implementer);

        var response = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":1,"reason":"Adapter timed out before submission."}""");

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(ContextEntryKind.Handoff, entries[0].Kind);
        Assert.Equal("Adapter timed out before submission.", entries[0].Content);

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Cancelled, refreshedSession.Status);

        var refreshedProject = await dbContext.Projects.SingleAsync(p => p.Id == project.Id);
        Assert.Equal(1, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Adapter_failure_diagnostic_is_persisted_without_raw_provider_text()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Implementer);

        var response = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":1,"reason":"Runner cancelled: adapter-non-zero-exit.","diagnostic":{"category":"WorktreeNotClean","adapterExitCode":5,"providerLaunched":false,"providerExitCode":null,"message":"adapter: edit mode requires a clean git worktree (Dirty)."}}""");

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var refreshed = await dbContext.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id);
        Assert.Equal("WorktreeNotClean", refreshed.FailureCategory);
        Assert.Equal(5, refreshed.FailureAdapterExitCode);
        Assert.False(refreshed.FailureProviderLaunched);
        Assert.Equal(
            "Implementer could not start: WorktreeNotClean (adapter exit 5). Provider was not launched.",
            await dbContext.ContextEntries
                .Where(entry => entry.SourceSessionId == session.Id && entry.Kind == ContextEntryKind.Handoff)
                .Select(entry => entry.Content)
                .SingleAsync());
        Assert.DoesNotContain("clean git worktree", refreshed.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_does_not_accept_provenance_fields()
    {
        // The cancel contract has only ContractVersion and Reason — there is no project/task/
        // source-session field for a caller to supply, so provenance can only come from the
        // route-resolved session, matching the UI's cancellation behavior.
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":1,"reason":"Reason."}""");

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_task_or_session_returns_not_found()
    {
        var response = await PostAsync(
            $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/cancel",
            """{"contractVersion":1,"reason":"Reason."}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Replaying_cancellation_after_success_returns_conflict_and_writes_no_duplicate()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var first = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":1,"reason":"First."}""");
        Assert.True(first.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        var replay = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":1,"reason":"Replay."}""");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Single(await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task Missing_reason_returns_bad_request_and_writes_nothing()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":1,"reason":""}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Contract_version_mismatch_returns_bad_request()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync(
            $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/cancel",
            """{"contractVersion":2,"reason":"Reason."}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string url, string jsonBody)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for RunnerCancelEndpointTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for RunnerCancelEndpointTests.",
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
}
