using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Operator visibility and enable/disable control for registered workers. GET is read-only, and
/// the page must never expose anything machine-specific because the server holds no repository paths.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class WorkersPageTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Workers_page_lists_a_registered_worker_with_its_availability()
    {
        var workerKey = $"page-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Workers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(workerKey, html);
        Assert.Contains("Online", html);
        Assert.Contains("Planner", html);
        Assert.Contains("Idle", html);
    }

    [Fact]
    public async Task Workers_page_shows_the_active_claim_and_its_lease()
    {
        var workerKey = $"claim-page-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey);

        var (project, task, _) = await SeedStartedSessionAsync();

        var claim = await PostAsync("/api/runner/workers/claim", new
        {
            contractVersion = 1,
            workerKey,
            projectIds = new[] { project.Id },
            leaseSeconds = 600
        });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Workers");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(workerKey, html);
        Assert.Contains(task.Title, html);
        Assert.Contains("Until", html);
    }

    [Fact]
    public async Task Workers_page_get_performs_no_writes()
    {
        var workerKey = $"readonly-page-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey);

        DateTime heartbeatBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            heartbeatBefore = await dbContext.Workers
                .Where(worker => worker.WorkerKey == workerKey)
                .Select(worker => worker.LastHeartbeatUtc)
                .SingleAsync();
        }

        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/Workers");
        using var second = await client.GetAsync("/Workers");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var worker = await verifyContext.Workers.SingleAsync(candidate => candidate.WorkerKey == workerKey);

        Assert.Equal(heartbeatBefore, worker.LastHeartbeatUtc);
        Assert.Null(worker.LastClaimUtc);
    }

    [Fact]
    public async Task Workers_page_can_disable_and_reenable_a_worker()
    {
        var workerKey = $"toggle-page-worker-{Guid.NewGuid():N}";
        await RegisterAsync(workerKey);

        Guid workerId;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            workerId = await dbContext.Workers
                .Where(worker => worker.WorkerKey == workerKey)
                .Select(worker => worker.Id)
                .SingleAsync();
        }

        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var afClient = new AntiforgeryHttpClient(client);
        var (_, pageHtml) = await afClient.GetPageAsync("/Workers");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(pageHtml);
        Assert.Contains("value=\"false\"", pageHtml);

        using (var disable = await afClient.PostFormAsync(
                   "/Workers?handler=SetEnabled",
                   token,
                   new Dictionary<string, string>
                   {
                       ["id"] = workerId.ToString(),
                       ["enabled"] = bool.FalseString
                   }))
        {
            Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
        }

        await RegisterAsync(workerKey);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            Assert.False(await dbContext.Workers
                .Where(worker => worker.Id == workerId)
                .Select(worker => worker.Enabled)
                .SingleAsync());
        }

        var (_, refreshedHtml) = await afClient.GetPageAsync("/Workers");
        var refreshedToken = AntiforgeryHttpClient.ExtractAntiforgeryToken(refreshedHtml);
        Assert.Contains("value=\"true\"", refreshedHtml);

        using (var enable = await afClient.PostFormAsync(
                   "/Workers?handler=SetEnabled",
                   refreshedToken,
                   new Dictionary<string, string>
                   {
                       ["id"] = workerId.ToString(),
                       ["enabled"] = bool.TrueString
                   }))
        {
            Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.True(await verifyContext.Workers
            .Where(worker => worker.Id == workerId)
            .Select(worker => worker.Enabled)
            .SingleAsync());
    }

    private async Task RegisterAsync(string workerKey)
    {
        var response = await PostAsync("/api/runner/workers/heartbeat", new
        {
            contractVersion = 1,
            workerKey,
            displayName = workerKey,
            capabilities = new[] { "Planner" }
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

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Workers page project {Guid.NewGuid():N}",
            Purpose = "Seeded for WorkersPageTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Workers page task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for WorkersPageTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        };

        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }
}
