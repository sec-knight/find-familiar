using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The worker heartbeat and claim routes, exercised through the real ASP.NET Core pipeline —
/// including the shared runner-bridge authentication filter, which must reject an unauthenticated
/// caller before any worker or session lookup happens.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class WorkerEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Heartbeat_registers_a_worker_and_returns_its_availability()
    {
        var workerKey = $"endpoint-worker-{Guid.NewGuid():N}";

        var response = await PostAsync("/api/runner/workers/heartbeat", new
        {
            contractVersion = 1,
            workerKey,
            displayName = "Endpoint worker",
            capabilities = new[] { "Planner" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.NotEqual(Guid.Empty, root.GetProperty("workerId").GetGuid());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("Online", root.GetProperty("availability").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.True(await dbContext.Workers.AnyAsync(worker => worker.WorkerKey == workerKey));
    }

    [Fact]
    public async Task Heartbeat_without_a_credential_is_rejected_before_registering_anything()
    {
        var workerKey = $"unauthenticated-worker-{Guid.NewGuid():N}";

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/runner/workers/heartbeat", new
        {
            contractVersion = 1,
            workerKey,
            displayName = "Should never register",
            capabilities = new[] { "Planner" }
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.False(await dbContext.Workers.AnyAsync(worker => worker.WorkerKey == workerKey));
    }

    [Fact]
    public async Task Heartbeat_with_an_unsupported_contract_version_is_rejected()
    {
        var response = await PostAsync("/api/runner/workers/heartbeat", new
        {
            contractVersion = 99,
            workerKey = $"versioned-worker-{Guid.NewGuid():N}",
            displayName = "Versioned",
            capabilities = new[] { "Planner" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_with_no_recognized_capability_is_a_validation_failure()
    {
        var response = await PostAsync("/api/runner/workers/heartbeat", new
        {
            contractVersion = 1,
            workerKey = $"capability-worker-{Guid.NewGuid():N}",
            displayName = "No capabilities",
            capabilities = Array.Empty<string>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Claim_grants_one_session_with_its_assignment_in_the_same_response()
    {
        var workerKey = $"claim-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey, "Planner");

        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey,
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(project.Id, root.GetProperty("projectId").GetGuid());
        Assert.Equal(task.Id, root.GetProperty("taskId").GetGuid());
        Assert.Equal(session.Id, root.GetProperty("sessionId").GetGuid());
        Assert.Equal("Planner", root.GetProperty("role").GetString());
        Assert.Contains("Planner", root.GetProperty("rolePrompt").GetString());
        Assert.Contains("# Find Familiar assignment", root.GetProperty("assignmentMarkdown").GetString());
        Assert.True(root.GetProperty("leaseExpiresUtc").GetDateTime() > root.GetProperty("claimedUtc").GetDateTime());
    }

    [Fact]
    public async Task Claim_with_nothing_eligible_returns_no_content()
    {
        var workerKey = $"idle-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey, "Planner");

        var response = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey,
            projectIds = new[] { Guid.NewGuid() },
            leaseSeconds = 600
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Claim_by_an_unregistered_worker_returns_not_found()
    {
        var (project, _, _) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey = $"never-registered-{Guid.NewGuid():N}",
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Claim_by_a_disabled_worker_returns_conflict_and_claims_nothing()
    {
        var workerKey = $"disabled-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey, "Planner");

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var worker = await dbContext.Workers.SingleAsync(candidate => candidate.WorkerKey == workerKey);
            worker.Enabled = false;
            await dbContext.SaveChangesAsync();
        }

        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var response = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey,
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Null((await verifyContext.AgentSessions.SingleAsync(s => s.Id == session.Id)).ClaimedByWorkerId);
    }

    [Fact]
    public async Task Claim_without_a_credential_is_rejected()
    {
        var (project, _, _) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey = "anyone",
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Claim_is_granted_to_only_one_of_two_sequential_workers()
    {
        var firstKey = $"pair-a-{Guid.NewGuid():N}";
        var secondKey = $"pair-b-{Guid.NewGuid():N}";
        await RegisterAsync(firstKey, "Planner");
        await RegisterAsync(secondKey, "Planner");

        var (project, _, _) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var first = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey = firstKey,
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });
        var second = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey = secondKey,
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Malformed_worker_body_is_rejected_without_registering()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runner/workers/heartbeat")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task RegisterAsync(string workerKey, params string[] capabilities)
    {
        var response = await PostAsync("/api/runner/workers/heartbeat", new
        {
            contractVersion = 1,
            workerKey,
            displayName = workerKey,
            capabilities
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string url, object body)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);

        return await client.SendAsync(request);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(
        AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Worker endpoint project {Guid.NewGuid():N}",
            Purpose = "Seeded for WorkerEndpointTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Worker endpoint task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for WorkerEndpointTests.",
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
