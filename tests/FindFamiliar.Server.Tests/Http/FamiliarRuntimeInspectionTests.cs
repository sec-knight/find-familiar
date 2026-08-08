using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Api.Gateway;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Frontend parity for the runtime: the Familiar must be able to find out what the Demiplane's
/// Workers page shows.
///
/// The failure this closes was concrete. A task read "Waiting for an available Planner", the human
/// asked why, and the Familiar could only repeat the sentence — it had no way to see whether a
/// Planner-capable worker was missing, disabled, offline, or busy. Four different problems, one
/// display string, and only one of them solved by waiting.
///
/// So these tests are mostly about the *explanation* being right for the right reason, not merely
/// present.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarRuntimeInspectionTests(FindFamiliarWebApplicationFactory factory)
{
    private const string RuntimeRoute = "/api/gateway/runtime";

    // ---------------------------------------------------------------- the four reasons a role cannot run

    [Fact]
    public async Task A_role_no_worker_declares_is_reported_as_blocked()
    {
        await ClearWorkersAsync();
        await SeedWorkerAsync("impl-only", [AgentSessionRole.Implementer]);

        var planner = await RoleAsync("Planner");

        Assert.True(planner.GetProperty("blocked").GetBoolean());
        Assert.Equal(0, planner.GetProperty("workersDeclaringRole").GetInt32());
        Assert.Contains("No registered worker declares", planner.GetProperty("explanation").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_whose_only_workers_are_disabled_is_reported_as_blocked()
    {
        await ClearWorkersAsync();
        await SeedWorkerAsync("planner-disabled", [AgentSessionRole.Planner], enabled: false);

        var planner = await RoleAsync("Planner");

        Assert.True(planner.GetProperty("blocked").GetBoolean());
        Assert.Equal(1, planner.GetProperty("workersDeclaringRole").GetInt32());
        Assert.Equal(0, planner.GetProperty("enabledAndOnline").GetInt32());
        Assert.Contains("disabled", planner.GetProperty("explanation").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A worker that stopped heartbeating is the case most easily mistaken for "busy". It is not:
    /// nothing will pick the work up until the process is running again.
    /// </summary>
    [Fact]
    public async Task A_role_whose_workers_are_all_offline_is_reported_as_blocked()
    {
        await ClearWorkersAsync();
        await SeedWorkerAsync("planner-offline", [AgentSessionRole.Planner], heartbeatAgo: TimeSpan.FromHours(6));

        var planner = await RoleAsync("Planner");

        Assert.True(planner.GetProperty("blocked").GetBoolean());
        Assert.Equal(1, planner.GetProperty("workersDeclaringRole").GetInt32());
        Assert.Equal(0, planner.GetProperty("enabledAndOnline").GetInt32());
        Assert.Contains("online", planner.GetProperty("explanation").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Busy is emphatically not blocked. A person told "blocked" goes looking for something to fix;
    /// a person told "busy" waits, which is correct.
    /// </summary>
    [Fact]
    public async Task A_role_whose_workers_are_all_busy_is_waiting_rather_than_blocked()
    {
        await ClearWorkersAsync();
        var seeded = await SeedWorkerAsync("planner-busy", [AgentSessionRole.Planner]);
        await SeedActiveClaimAsync(seeded);

        var planner = await RoleAsync("Planner");

        Assert.False(planner.GetProperty("blocked").GetBoolean());
        Assert.Equal(1, planner.GetProperty("enabledAndOnline").GetInt32());
        Assert.Equal(0, planner.GetProperty("idleAndReady").GetInt32());
        Assert.Contains("already running", planner.GetProperty("explanation").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_idle_online_worker_is_reported_as_ready()
    {
        await ClearWorkersAsync();
        await SeedWorkerAsync("planner-idle", [AgentSessionRole.Planner]);

        var planner = await RoleAsync("Planner");

        Assert.False(planner.GetProperty("blocked").GetBoolean());
        Assert.Equal(1, planner.GetProperty("idleAndReady").GetInt32());
        Assert.Contains("idle", planner.GetProperty("explanation").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- worker facts

    [Fact]
    public async Task Each_worker_reports_the_facts_the_demiplane_shows()
    {
        await ClearWorkersAsync();
        await SeedWorkerAsync("parity-worker", [AgentSessionRole.Planner, AgentSessionRole.Reviewer]);

        var runtime = await GetRuntimeAsync();
        var worker = runtime.GetProperty("workers").EnumerateArray()
            .Single(candidate => candidate.GetProperty("workerKey").GetString() == "parity-worker");

        Assert.True(worker.GetProperty("enabled").GetBoolean());
        Assert.Equal("Online", worker.GetProperty("availability").GetString());
        Assert.Equal(
            ["Planner", "Reviewer"],
            worker.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).Order());

        // Relative rather than absolute: "last heartbeat 02:14" means nothing to a reader with no clock.
        Assert.True(worker.GetProperty("secondsSinceHeartbeat").GetDouble() >= 0);
    }

    /// <summary>
    /// The one sensitivity boundary on this surface. A busy worker is a fact about the machine and is
    /// reported; what it is busy with belongs to a project, and a project the caller may not read is
    /// never named — here as everywhere else.
    /// </summary>
    [Fact]
    public async Task A_claim_on_a_sensitive_project_is_reported_without_naming_the_task()
    {
        await ClearWorkersAsync();
        var seeded = await SeedWorkerAsync("sensitive-claim", [AgentSessionRole.Implementer]);
        var title = await SeedActiveClaimAsync(seeded, sensitive: true);

        var runtime = await GetRuntimeAsync();
        var worker = runtime.GetProperty("workers").EnumerateArray()
            .Single(candidate => candidate.GetProperty("workerKey").GetString() == "sensitive-claim");

        var active = worker.GetProperty("activeWork");

        // The claim exists and its role is stated, so the pool count is still explicable.
        Assert.Equal("Implementer", active.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, active.GetProperty("taskTitle").ValueKind);

        Assert.DoesNotContain(title, runtime.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_claim_on_a_readable_project_names_the_task()
    {
        await ClearWorkersAsync();
        var seeded = await SeedWorkerAsync("readable-claim", [AgentSessionRole.Implementer]);
        var title = await SeedActiveClaimAsync(seeded);

        var runtime = await GetRuntimeAsync();
        var worker = runtime.GetProperty("workers").EnumerateArray()
            .Single(candidate => candidate.GetProperty("workerKey").GetString() == "readable-claim");

        Assert.Equal(title, worker.GetProperty("activeWork").GetProperty("taskTitle").GetString());
    }

    [Fact]
    public async Task An_empty_pool_says_work_is_waiting_on_a_worker_not_on_a_decision()
    {
        await ClearWorkersAsync();

        var runtime = await GetRuntimeAsync();

        Assert.Empty(runtime.GetProperty("workers").EnumerateArray());
        Assert.Contains(
            "waiting on a worker",
            runtime.GetProperty("disclosure").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(
            runtime.GetProperty("roles").EnumerateArray(),
            role => Assert.True(role.GetProperty("blocked").GetBoolean()));
    }

    // ---------------------------------------------------------------- authorization

    [Fact]
    public async Task The_static_read_token_may_inspect_the_runtime()
    {
        using var client = Authenticated();

        using var response = await client.GetAsync(RuntimeRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_may_not_inspect_the_runtime()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(RuntimeRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Reading the runtime is a read, and it must change nothing at all.</summary>
    [Fact]
    public async Task Inspecting_the_runtime_changes_no_state()
    {
        await ClearWorkersAsync();
        await SeedWorkerAsync("no-mutation", [AgentSessionRole.Planner]);

        var before = await FingerprintAsync();

        using var client = Authenticated();
        await client.GetAsync(RuntimeRoute);
        await CallMcpToolAsync(client, "inspect_familiar_runtime", new { });

        Assert.Equal(before, await FingerprintAsync());
    }

    [Fact]
    public async Task The_tool_is_advertised_read_only_and_declared_in_the_manifest()
    {
        using var client = Authenticated();

        var tools = await CallMcpAsync(client, "tools/list", new { });
        var tool = tools.GetProperty("tools").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == "inspect_familiar_runtime");

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        var manifest = await client.GetFromJsonAsync<JsonElement>("/api/gateway/manifest");

        Assert.Contains(
            "inspect_familiar_runtime",
            manifest.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(
            "inspect_familiar_runtime",
            manifest.GetProperty("writeCapabilities").EnumerateArray().Select(value => value.GetString()));
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        return client;
    }

    private async Task<JsonElement> GetRuntimeAsync()
    {
        using var client = Authenticated();

        return await client.GetFromJsonAsync<JsonElement>(RuntimeRoute);
    }

    private async Task<JsonElement> RoleAsync(string role)
    {
        var runtime = await GetRuntimeAsync();

        return runtime.GetProperty("roles").EnumerateArray()
            .Single(candidate => candidate.GetProperty("role").GetString() == role);
    }

    /// <summary>
    /// The shared fixture accumulates workers across the collection, so a test asserting "no worker
    /// declares Planner" must first establish that. Workers are removed rather than disabled: a
    /// disabled worker is a distinct state this file also tests.
    /// </summary>
    private async Task ClearWorkersAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // Release any claim first: a worker holding one cannot be deleted while the session refers to it.
        foreach (var session in await dbContext.AgentSessions
                     .Where(candidate => candidate.ClaimedByWorkerId != null)
                     .ToListAsync())
        {
            session.ClaimedByWorkerId = null;
            session.ClaimId = null;
            session.ClaimedUtc = null;
            session.ClaimExpiresUtc = null;
        }

        dbContext.Workers.RemoveRange(await dbContext.Workers.ToListAsync());
        await dbContext.SaveChangesAsync();
    }

    private async Task<Worker> SeedWorkerAsync(
        string key,
        AgentSessionRole[] capabilities,
        bool enabled = true,
        TimeSpan? heartbeatAgo = null)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            WorkerKey = key,
            DisplayName = key,
            Capabilities = string.Join(',', capabilities),
            Enabled = enabled,
            RegisteredUtc = DateTime.UtcNow.AddDays(-1),
            LastHeartbeatUtc = DateTime.UtcNow - (heartbeatAgo ?? TimeSpan.FromSeconds(5))
        };

        dbContext.Workers.Add(worker);
        await dbContext.SaveChangesAsync();

        return worker;
    }

    /// <summary>Gives the worker a live claim on a Started session, and returns the task's title.</summary>
    private async Task<string> SeedActiveClaimAsync(Worker worker, bool sensitive = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Runtime project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarRuntimeInspectionTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Claimed task {Guid.NewGuid():N}",
            RequestedOutcome = "A task a worker is currently running.",
            Status = TaskStatus.InProgress,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Implementer,
            Status = AgentSessionStatus.Started,
            StartedUtc = DateTime.UtcNow.AddMinutes(-2),
            ClaimedByWorkerId = worker.Id,
            ClaimId = Guid.NewGuid(),
            ClaimedUtc = DateTime.UtcNow.AddMinutes(-2),
            ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(20)
        };

        dbContext.Projects.Add(project);
        dbContext.Tasks.Add(task);
        dbContext.AgentSessions.Add(session);

        var tracked = await dbContext.Workers.SingleAsync(candidate => candidate.Id == worker.Id);
        tracked.LastClaimUtc = DateTime.UtcNow.AddMinutes(-2);

        await dbContext.SaveChangesAsync();

        return task.Title;
    }

    private async Task<(int Workers, int Sessions, int Tasks, int Handoffs)> FingerprintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.Workers.CountAsync(),
            await dbContext.AgentSessions.CountAsync(),
            await dbContext.Tasks.CountAsync(),
            await dbContext.SessionHandoffs.CountAsync());
    }

    private static async Task<JsonElement> CallMcpAsync(HttpClient client, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var raw = (await response.Content.ReadAsStringAsync()).Trim();
        var payload = raw.StartsWith('{')
            ? raw
            : raw.Split('\n').Select(line => line.Trim())
                .First(line => line.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();

        using var document = JsonDocument.Parse(payload);

        return document.RootElement.GetProperty("result").Clone();
    }

    private static async Task<JsonElement> CallMcpToolAsync(HttpClient client, string tool, object arguments)
    {
        var result = await CallMcpAsync(client, "tools/call", new { name = tool, arguments });

        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind is JsonValueKind.Object)
        {
            return structured.Clone();
        }

        var text = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }
}
