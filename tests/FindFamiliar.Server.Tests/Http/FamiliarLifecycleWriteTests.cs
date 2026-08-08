using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Api.Gateway;
using FindFamiliar.Server.Api.Gateway.OAuth;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Slice 1 of the lifecycle writes: creating and maintaining ordinary project work.
///
/// Two things are being defended here and they pull in opposite directions. The capability has to be
/// genuinely useful — a person should be able to say "make a task for that" and have it happen — while
/// remaining unable to do the things that are somebody else's to decide. So the tests come in pairs:
/// the operation works, and the operation stops exactly where it should. Nothing here may start work,
/// answer a decision, delete anything, or reach a project the caller cannot see.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarLifecycleWriteTests(FindFamiliarWebApplicationFactory factory)
{
    private const string Read = FamiliarGatewayOptions.ReadScope;
    private const string Write = FamiliarGatewayOptions.ProjectWriteScope;
    private const string Decide = FamiliarGatewayOptions.DecideScope;

    // ---------------------------------------------------------------- the operations work

    [Fact]
    public async Task A_project_can_be_created_and_is_visible_to_both_frontends()
    {
        using var client = await WriterAsync();
        var name = $"Conversation project {Guid.NewGuid():N}";

        var result = await PostAsync(client, "/api/gateway/lifecycle/projects", new { name, purpose = "Made by asking." });

        Assert.Equal("Done", result.GetProperty("outcome").GetString());
        var projectId = result.GetProperty("projectId").GetGuid();

        // The Demiplane's own projection sees it: one authoritative state, two frontends.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var project = await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);

        Assert.Equal(name, project.Name);
        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    public async Task A_duplicate_project_name_is_refused_and_creates_nothing()
    {
        using var client = await WriterAsync();
        var name = $"Duplicate {Guid.NewGuid():N}";

        await PostAsync(client, "/api/gateway/lifecycle/projects", new { name, purpose = "First." });
        var second = await PostAsync(client, "/api/gateway/lifecycle/projects", new { name, purpose = "Second." });

        Assert.Equal("NameTaken", second.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(1, await dbContext.Projects.CountAsync(p => p.Name == name));
    }

    /// <summary>
    /// The property that keeps this scope separate from the workflow ones: a created task is Ready and
    /// idle. Creating work and spending model time on it are different decisions.
    /// </summary>
    [Fact]
    public async Task A_created_task_is_ready_and_nothing_runs_on_it()
    {
        var project = await SeedProjectAsync();
        using var client = await WriterAsync();

        var result = await PostAsync(client, "/api/gateway/lifecycle/tasks", new
        {
            projectId = project,
            title = "Fix the mobile navigation",
            requestedOutcome = "Selecting a task on a phone lands on the task detail."
        });

        Assert.Equal("Done", result.GetProperty("outcome").GetString());
        var taskId = result.GetProperty("taskId").GetGuid();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var task = await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);

        Assert.Equal(TaskStatus.Ready, task.Status);
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().Where(s => s.TaskId == taskId).ToListAsync());

        // And the tool says so, so a client does not report that work has begun.
        Assert.Contains("nothing is running", result.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Blocked")]
    [InlineData("blocked")]
    [InlineData("Ready")]
    [InlineData("InReview")]
    public async Task A_task_status_can_be_set_and_case_does_not_matter(string status)
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();

        var result = await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status });

        Assert.Equal("Done", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var task = await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.TaskId);

        Assert.Equal(Enum.Parse<TaskStatus>(status, ignoreCase: true), task.Status);
    }

    /// <summary>
    /// Completing a task retires the step that was waiting on it. Without this the handoff stays
    /// Pending forever: unapprovable, because the approval service refuses a closed task, and still
    /// reported in every "waiting for you" list.
    /// </summary>
    [Fact]
    public async Task Completing_a_task_retires_the_decision_that_was_waiting_on_it()
    {
        var seeded = await SeedTaskAsync(withPendingHandoff: true);
        using var client = await WriterAsync();

        var result = await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status = "Completed" });

        Assert.Equal("Done", result.GetProperty("outcome").GetString());
        Assert.Contains("no longer waiting", result.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            SessionHandoffStatus.Superseded,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId)).Status);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    public async Task Context_can_be_recorded_against_a_project_or_a_task(string target)
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();

        var body = target == "task"
            ? (object)new { category = "Decision", title = "A decision", content = "We decided this.", taskId = seeded.TaskId }
            : new { category = "Constraint", title = "A constraint", content = "This always applies.", projectId = seeded.ProjectId };

        var result = await PostAsync(client, "/api/gateway/lifecycle/context", body);

        Assert.Equal("Done", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entry = await dbContext.ContextEntries.AsNoTracking()
            .SingleAsync(e => e.Id == result.GetProperty("contextEntryId").GetGuid());

        Assert.Equal(seeded.ProjectId, entry.ProjectId);
        Assert.Equal(target == "task" ? seeded.TaskId : null, entry.TaskId);

        // Reported, not verified: the Familiar wrote down what a person said.
        Assert.Equal(ContextProvenance.HumanReported, entry.Provenance);
        Assert.Equal("familiar-gateway", entry.RecordedBy);
    }

    [Fact]
    public async Task Context_must_name_exactly_one_of_project_or_task()
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();

        foreach (var body in new object[]
                 {
                     new { category = "Decision", title = "t", content = "c" },
                     new { category = "Decision", title = "t", content = "c", projectId = seeded.ProjectId, taskId = seeded.TaskId }
                 })
        {
            using var response = await client.PostAsJsonAsync("/api/gateway/lifecycle/context", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    // ---------------------------------------------------------------- it stops where it should

    /// <summary>
    /// The scope separation, made consequential. A project-write token may create a task and may not
    /// answer the decision waiting beside it.
    /// </summary>
    [Fact]
    public async Task A_project_write_token_cannot_decide_anything()
    {
        var seeded = await SeedTaskAsync(withPendingHandoff: true);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync($"{Read} {Write}"));

        using var response = await client.PostAsJsonAsync("/api/gateway/decisions/submit", new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.HandoffToken,
            choice = "approve"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId)).Status);
    }

    /// <summary>And the converse: a decide token cannot create work.</summary>
    [Fact]
    public async Task A_decide_token_cannot_create_project_work()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync($"{Read} {Decide}"));

        using var response = await client.PostAsJsonAsync("/api/gateway/lifecycle/tasks", new
        {
            projectId = project,
            title = "Should not exist",
            requestedOutcome = "Nothing."
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await TaskCountAsync(project));
    }

    [Fact]
    public async Task A_read_only_token_cannot_write_anything()
    {
        var seeded = await SeedTaskAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync(Read));

        foreach (var (route, body) in new (string, object)[]
                 {
                     ("/api/gateway/lifecycle/projects", new { name = $"Nope {Guid.NewGuid():N}", purpose = "x" }),
                     ("/api/gateway/lifecycle/tasks", new { projectId = seeded.ProjectId, title = "Nope", requestedOutcome = "x" }),
                     ($"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status = "Blocked" }),
                     ("/api/gateway/lifecycle/context", new { category = "Decision", title = "Nope", content = "x", taskId = seeded.TaskId })
                 })
        {
            using var response = await client.PostAsJsonAsync(route, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Nothing moved.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(TaskStatus.Ready, (await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.TaskId)).Status);
        Assert.Equal(1, await TaskCountAsync(seeded.ProjectId));
    }

    [Fact]
    public async Task The_static_gateway_token_cannot_write_anything()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var response = await client.PostAsJsonAsync("/api/gateway/lifecycle/tasks", new
        {
            projectId = project,
            title = "Nope",
            requestedOutcome = "x"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await TaskCountAsync(project));
    }

    /// <summary>A project the caller cannot read answers as one that does not exist, and stays untouched.</summary>
    [Fact]
    public async Task Work_cannot_be_created_in_a_sensitive_project()
    {
        var project = await SeedProjectAsync(sensitive: true);
        using var client = await WriterAsync();

        var result = await PostAsync(client, "/api/gateway/lifecycle/tasks", new
        {
            projectId = project,
            title = "Should not exist",
            requestedOutcome = "Nothing."
        });

        Assert.Equal("NotFound", result.GetProperty("outcome").GetString());
        Assert.DoesNotContain("sensitive", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await TaskCountAsync(project));
    }

    [Fact]
    public async Task A_task_in_a_sensitive_project_cannot_be_changed()
    {
        var seeded = await SeedTaskAsync(sensitive: true);
        using var client = await WriterAsync();

        var result = await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status = "Completed" });

        Assert.Equal("NotFound", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(TaskStatus.Ready, (await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.TaskId)).Status);
    }

    [Fact]
    public async Task An_unknown_id_answers_the_same_as_an_unreadable_one()
    {
        using var client = await WriterAsync();

        var result = await PostAsync(client, "/api/gateway/lifecycle/tasks", new
        {
            projectId = Guid.NewGuid(),
            title = "Nowhere",
            requestedOutcome = "x"
        });

        Assert.Equal("NotFound", result.GetProperty("outcome").GetString());
    }

    // ---------------------------------------------------------------- typed inputs

    [Theory]
    [InlineData("Finished")]
    [InlineData("done")]
    [InlineData("")]
    [InlineData("Completed; DROP TABLE Tasks")]
    public async Task An_unknown_status_is_refused_and_changes_nothing(string status)
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();

        var result = await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status });

        Assert.Equal("Rejected", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(TaskStatus.Ready, (await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.TaskId)).Status);
    }

    [Theory]
    [InlineData("Musing")]
    [InlineData("")]
    public async Task An_unknown_context_category_is_refused(string category)
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();

        var result = await PostAsync(client, "/api/gateway/lifecycle/context", new
        {
            category,
            title = "t",
            content = "c",
            taskId = seeded.TaskId
        });

        Assert.Equal("Rejected", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Missing_or_oversized_fields_are_refused_without_partial_state()
    {
        var project = await SeedProjectAsync();
        using var client = await WriterAsync();

        foreach (var body in new object[]
                 {
                     new { projectId = project, title = "", requestedOutcome = "x" },
                     new { projectId = project, title = new string('t', 201), requestedOutcome = "x" },
                     new { projectId = project, title = "ok", requestedOutcome = "" }
                 })
        {
            var result = await PostAsync(client, "/api/gateway/lifecycle/tasks", body);
            Assert.Equal("Rejected", result.GetProperty("outcome").GetString());
        }

        Assert.Equal(0, await TaskCountAsync(project));
    }

    // ---------------------------------------------------------------- idempotency shape

    /// <summary>
    /// Status changes are idempotent by nature: setting the same status twice lands in the same place.
    /// Creation is not, and is not pretended to be — two "create a task" instructions from a person are
    /// two tasks, which is what they asked for.
    /// </summary>
    [Fact]
    public async Task Setting_the_same_status_twice_is_stable_and_creating_twice_is_not_deduplicated()
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();

        await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status = "Blocked" });
        var second = await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status = "Blocked" });

        Assert.Equal("Done", second.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(TaskStatus.Blocked, (await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.TaskId)).Status);

        await PostAsync(client, "/api/gateway/lifecycle/tasks", new { projectId = seeded.ProjectId, title = "Twice", requestedOutcome = "x" });
        await PostAsync(client, "/api/gateway/lifecycle/tasks", new { projectId = seeded.ProjectId, title = "Twice", requestedOutcome = "x" });

        Assert.Equal(2, await dbContext.Tasks.AsNoTracking().CountAsync(t => t.ProjectId == seeded.ProjectId && t.Title == "Twice"));
    }

    // ---------------------------------------------------------------- the tool surface

    [Fact]
    public async Task The_write_tools_are_advertised_as_writes_and_declared_in_the_manifest()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        var tools = await CallMcpAsync(client, "tools/list");
        var mutating = tools.GetProperty("tools").EnumerateArray()
            .Where(tool => !tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean())
            .Select(tool => tool.GetProperty("name").GetString()!)
            .Order()
            .ToList();

        Assert.Equal(
            ["create_familiar_project", "create_familiar_task", "record_familiar_context",
             "set_familiar_task_status", "submit_familiar_decision"],
            mutating);

        // Nothing destructive, on the whole surface.
        Assert.All(
            tools.GetProperty("tools").EnumerateArray(),
            tool => Assert.False(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()));

        var manifest = await client.GetFromJsonAsync<JsonElement>("/api/gateway/manifest");
        Assert.Equal(
            mutating,
            manifest.GetProperty("writeCapabilities").EnumerateArray().Select(v => v.GetString()!).Order());
    }

    /// <summary>Creating a task must not become a way to start one.</summary>
    [Fact]
    public async Task No_lifecycle_write_starts_a_session()
    {
        var seeded = await SeedTaskAsync();
        using var client = await WriterAsync();
        var before = await SessionCountAsync();

        await PostAsync(client, "/api/gateway/lifecycle/tasks", new { projectId = seeded.ProjectId, title = "A", requestedOutcome = "x" });
        await PostAsync(client, $"/api/gateway/lifecycle/tasks/{seeded.TaskId}/status", new { status = "InProgress" });
        await PostAsync(client, "/api/gateway/lifecycle/context", new { category = "Plan", title = "Start it now", content = "Run the implementer.", taskId = seeded.TaskId });

        Assert.Equal(before, await SessionCountAsync());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<HttpClient> WriterAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync($"{Read} {Write}"));

        return client;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string route, object body)
    {
        using var response = await client.PostAsJsonAsync(route, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> TaskCountAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.Tasks.CountAsync(task => task.ProjectId == projectId);
    }

    private async Task<int> SessionCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.AgentSessions.CountAsync();
    }

    private async Task<Guid> SeedProjectAsync(bool sensitive = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Lifecycle project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarLifecycleWriteTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        return project.Id;
    }

    private sealed record SeededTask(Guid ProjectId, Guid TaskId, Guid HandoffId, Guid HandoffToken);

    private async Task<SeededTask> SeedTaskAsync(bool sensitive = false, bool withPendingHandoff = false)
    {
        var projectId = await SeedProjectAsync(sensitive);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = $"Lifecycle task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);

        var handoffId = Guid.Empty;
        var handoffToken = Guid.Empty;

        if (withPendingHandoff)
        {
            var session = new AgentSession
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Role = AgentSessionRole.Implementer,
                Status = AgentSessionStatus.Completed,
                StartedUtc = DateTime.UtcNow.AddMinutes(-5),
                CompletedUtc = DateTime.UtcNow.AddMinutes(-1)
            };

            var handoff = new SessionHandoff
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SourceSessionId = session.Id,
                SourceOutcome = AgentSessionStatus.Completed,
                ProposedRole = AgentSessionRole.Reviewer,
                Kind = SessionHandoffKind.NextRole,
                Status = SessionHandoffStatus.Pending,
                ConcurrencyToken = Guid.NewGuid(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            dbContext.AgentSessions.Add(session);
            dbContext.SessionHandoffs.Add(handoff);
            handoffId = handoff.Id;
            handoffToken = handoff.ConcurrencyToken;
        }

        await dbContext.SaveChangesAsync();

        return new SeededTask(projectId, task.Id, handoffId, handoffToken);
    }

    private async Task<string> TokenAsync(string scope)
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        const string redirectUri = "https://chatgpt.com/connector/oauth/lifecycle-tests";

        using var registration = await client.PostAsJsonAsync(
            "/oauth/register", new { redirect_uris = new[] { redirectUri }, client_name = "ChatGPT" });
        registration.EnsureSuccessStatusCode();

        var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("client_id").GetString()!;

        var verifier = FamiliarOAuthArtifacts.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = FamiliarOAuthArtifacts.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var authorize = await client.GetAsync(
            "/oauth/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&code_challenge=" + challenge
            + "&code_challenge_method=S256"
            + "&scope=" + Uri.EscapeDataString(scope));

        authorize.EnsureSuccessStatusCode();

        var page = await authorize.Content.ReadAsStringAsync();
        const string marker = "name=\"request\" value=\"";
        var start = page.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var pending = WebUtility.HtmlDecode(page[start..page.IndexOf('"', start)]);

        using var consented = await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["request"] = pending,
                ["owner_token"] = FindFamiliarWebApplicationFactory.GatewayTestToken
            }));

        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(consented.Headers.Location!.Query)["code"]!;

        using var token = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier
            }));

        token.EnsureSuccessStatusCode();

        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> CallMcpAsync(HttpClient client, string method)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = new { } }),
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
}
