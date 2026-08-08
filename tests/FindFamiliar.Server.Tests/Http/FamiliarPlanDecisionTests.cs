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

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Plan proposals as a first-class decision type: discoverable beside session handoffs, and relayable
/// through the same service the chat page's buttons post to.
///
/// The property that needs the most protection is narrower than "the relay works". A plan is a list of
/// tasks somebody may edit before agreeing to it, so the danger is not that a model approves the wrong
/// plan — it is that a model approves a <em>different</em> plan from the one the person read. The
/// relay therefore carries no item decisions at all, and several tests below exist only to prove that
/// what is approved is exactly what was drafted.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarPlanDecisionTests(FindFamiliarWebApplicationFactory factory)
{
    private const string SubmitRoute = "/api/gateway/decisions/submit";
    private const string DecisionsRoute = "/api/gateway/decisions";

    // ---------------------------------------------------------------- discovery

    [Fact]
    public async Task A_pending_plan_appears_as_a_decision_with_what_it_would_create()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();

        var decision = await SingleDecisionAsync();

        Assert.Equal(seeded.PlanId, decision.GetProperty("decisionId").GetGuid());
        Assert.Equal("PlanProposal", decision.GetProperty("decisionKind").GetString());
        Assert.Equal(seeded.ProjectId, decision.GetProperty("projectId").GetGuid());
        Assert.Equal(seeded.Token, decision.GetProperty("expectedConcurrencyToken").GetGuid());
        Assert.Equal(["approve", "decline"], decision.GetProperty("legalChoices").EnumerateArray().Select(v => v.GetString()));

        // A plan has no task until it is approved, and saying otherwise would invent one.
        Assert.Equal(JsonValueKind.Null, decision.GetProperty("taskId").ValueKind);

        // What approving would create, so a person can be told before they agree.
        var items = decision.GetProperty("plannedItems").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Write the thing", items[0].GetProperty("title").GetString());
        Assert.Equal("Implementer", items[0].GetProperty("role").GetString());
        Assert.True(items[0].GetProperty("isIncluded").GetBoolean());
    }

    [Fact]
    public async Task The_reason_states_how_much_would_be_created_and_what_would_start()
    {
        await ClearPendingAsync();
        await SeedPlanAsync();

        var reason = (await SingleDecisionAsync()).GetProperty("reason").GetString()!;

        Assert.Contains("2 tasks would be created", reason, StringComparison.Ordinal);
        Assert.Contains("Implementer session would start", reason, StringComparison.Ordinal);
    }

    /// <summary>Both kinds answer one question — "what needs me" — so they are reported together.</summary>
    [Fact]
    public async Task Plans_and_handoffs_are_reported_side_by_side()
    {
        await ClearPendingAsync();
        var plan = await SeedPlanAsync();
        var handoff = await SeedHandoffAsync();

        var decisions = (await GetDecisionsAsync()).GetProperty("decisions").EnumerateArray().ToList();

        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, d => d.GetProperty("decisionId").GetGuid() == plan.PlanId
            && d.GetProperty("decisionKind").GetString() == "PlanProposal");
        Assert.Contains(decisions, d => d.GetProperty("decisionId").GetGuid() == handoff
            && d.GetProperty("decisionKind").GetString() == "SessionHandoff");
    }

    /// <summary>A plan in a project the caller may not read is absent, exactly as a handoff would be.</summary>
    [Fact]
    public async Task A_plan_in_a_sensitive_project_is_withheld()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync(sensitive: true);

        var result = await GetDecisionsAsync();
        var body = result.ToString();

        Assert.Empty(result.GetProperty("decisions").EnumerateArray());
        Assert.DoesNotContain(seeded.PlanId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write the thing", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the relay

    [Fact]
    public async Task Approving_a_plan_creates_its_tasks_and_starts_exactly_one_session()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.PlanId, seeded.Token, "approve");

        Assert.Equal("Approved", result.GetProperty("outcome").GetString());
        Assert.Contains("2 tasks created", result.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var plan = await dbContext.FamiliarPlanProposals.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.PlanId);
        Assert.Equal(FamiliarPlanStatus.Approved, plan.Status);

        var tasks = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.ProjectId == seeded.ProjectId).ToListAsync();
        Assert.Equal(2, tasks.Count);

        // ADR-0014: every included task, and exactly one session.
        var sessions = await dbContext.AgentSessions.AsNoTracking()
            .Where(session => tasks.Select(t => t.Id).Contains(session.TaskId)).ToListAsync();
        Assert.Single(sessions);
        Assert.Equal(AgentSessionRole.Implementer, sessions[0].Role);
    }

    [Fact]
    public async Task Declining_a_plan_creates_nothing()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.PlanId, seeded.Token, "decline");

        Assert.Equal("Declined", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            FamiliarPlanStatus.Declined,
            (await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId)).Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == seeded.ProjectId).ToListAsync());
    }

    /// <summary>
    /// The property this slice most needs. An item the human excluded stays excluded, and its task is
    /// never created — the relay carries a yes, not a different plan.
    /// </summary>
    [Fact]
    public async Task Approving_relays_the_plan_exactly_as_drafted_including_exclusions()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync(excludeSecondItem: true);
        using var client = await DecideClientAsync();

        await SubmitAsync(client, seeded.PlanId, seeded.Token, "approve");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var tasks = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.ProjectId == seeded.ProjectId).ToListAsync();

        var task = Assert.Single(tasks);
        Assert.Equal("Write the thing", task.Title);

        // The excluded item was not created, and its wording was not altered on the way through.
        Assert.DoesNotContain(tasks, candidate => candidate.Title == "Review the thing");
    }

    /// <summary>The submission contract carries no item field at all, so there is nothing to smuggle.</summary>
    [Fact]
    public async Task The_submission_contract_accepts_no_item_level_input()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.PlanId,
            expectedConcurrencyToken = seeded.Token,
            choice = "approve",

            // Ignored: the record has no such members, so a client cannot rewrite the plan here.
            items = new[] { new { title = "Something the human never read", isIncluded = true } },
            plannedItems = new[] { new { title = "Nor this" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var titles = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.ProjectId == seeded.ProjectId).Select(task => task.Title).ToListAsync();

        Assert.DoesNotContain("Something the human never read", titles);
        Assert.DoesNotContain("Nor this", titles);
        Assert.Equal(["Review the thing", "Write the thing"], titles.Order());
    }

    // ---------------------------------------------------------------- fencing and replay

    [Fact]
    public async Task A_stale_token_changes_nothing()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.PlanId, Guid.NewGuid(), "approve");

        Assert.Equal("StaleDecision", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            FamiliarPlanStatus.Pending,
            (await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId)).Status);
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == seeded.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task Replaying_an_approval_creates_no_second_set_of_tasks()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        await SubmitAsync(client, seeded.PlanId, seeded.Token, "approve");
        var afterFirst = await TaskCountAsync(seeded.ProjectId);

        var replay = await SubmitAsync(client, seeded.PlanId, seeded.Token, "approve");

        Assert.NotEqual("Approved", replay.GetProperty("outcome").GetString());
        Assert.Equal(afterFirst, await TaskCountAsync(seeded.ProjectId));
    }

    [Fact]
    public async Task A_declined_plan_cannot_be_approved_afterwards()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        await SubmitAsync(client, seeded.PlanId, seeded.Token, "decline");
        var after = await SubmitAsync(client, seeded.PlanId, seeded.Token, "approve");

        Assert.NotEqual("Approved", after.GetProperty("outcome").GetString());
        Assert.Equal(0, await TaskCountAsync(seeded.ProjectId));
    }

    /// <summary>A decided plan leaves the list, because it no longer needs the human.</summary>
    [Fact]
    public async Task A_decided_plan_stops_being_reported()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();
        using var client = await DecideClientAsync();

        await SubmitAsync(client, seeded.PlanId, seeded.Token, "decline");

        var decisions = (await GetDecisionsAsync()).GetProperty("decisions").EnumerateArray().ToList();

        Assert.DoesNotContain(decisions, d => d.GetProperty("decisionId").GetGuid() == seeded.PlanId);
    }

    // ---------------------------------------------------------------- scope and isolation

    [Fact]
    public async Task A_read_only_token_cannot_decide_a_plan()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + await ObtainAccessTokenAsync(FamiliarGatewayOptions.ReadScope));

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.PlanId,
            expectedConcurrencyToken = seeded.Token,
            choice = "approve"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await TaskCountAsync(seeded.ProjectId));
    }

    [Fact]
    public async Task The_static_gateway_token_cannot_decide_a_plan()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.PlanId,
            expectedConcurrencyToken = seeded.Token,
            choice = "approve"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await TaskCountAsync(seeded.ProjectId));
    }

    /// <summary>A plan the caller cannot see cannot be decided, even with the right scope and ids.</summary>
    [Fact]
    public async Task A_plan_in_a_sensitive_project_cannot_be_decided()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync(sensitive: true);
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.PlanId, seeded.Token, "approve");

        Assert.Equal("NotFound", result.GetProperty("outcome").GetString());
        Assert.Equal(0, await TaskCountAsync(seeded.ProjectId));
    }

    /// <summary>End to end over MCP, on a token that went through consent.</summary>
    [Fact]
    public async Task The_full_loop_runs_over_mcp()
    {
        await ClearPendingAsync();
        var seeded = await SeedPlanAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "Bearer " + await ObtainAccessTokenAsync($"{FamiliarGatewayOptions.ReadScope} {FamiliarGatewayOptions.DecideScope}"));

        var open = await CallMcpToolAsync(client, "open_decisions", new { });
        var decision = open.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("decisionId").GetGuid() == seeded.PlanId);

        Assert.Equal("PlanProposal", decision.GetProperty("decisionKind").GetString());

        var result = await CallMcpToolAsync(client, "submit_familiar_decision", new
        {
            decisionId = decision.GetProperty("decisionId").GetGuid(),
            expectedConcurrencyToken = decision.GetProperty("expectedConcurrencyToken").GetGuid(),
            choice = "approve"
        });

        Assert.Equal("Approved", result.GetProperty("outcome").GetString());
        Assert.Equal(2, await TaskCountAsync(seeded.ProjectId));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<HttpClient> DecideClientAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "Bearer " + await ObtainAccessTokenAsync($"{FamiliarGatewayOptions.ReadScope} {FamiliarGatewayOptions.DecideScope}"));

        return client;
    }

    private HttpClient Reader()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        return client;
    }

    private async Task<JsonElement> GetDecisionsAsync()
    {
        using var client = Reader();

        return await client.GetFromJsonAsync<JsonElement>(DecisionsRoute);
    }

    private async Task<JsonElement> SingleDecisionAsync() =>
        (await GetDecisionsAsync()).GetProperty("decisions").EnumerateArray().Single();

    private static async Task<JsonElement> SubmitAsync(HttpClient client, Guid id, Guid token, string choice)
    {
        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = id,
            expectedConcurrencyToken = token,
            choice
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> TaskCountAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.Tasks.CountAsync(task => task.ProjectId == projectId);
    }

    /// <summary>The shared fixture accumulates decisions, so a test asserting "one" must establish it.</summary>
    private async Task ClearPendingAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        foreach (var handoff in await dbContext.SessionHandoffs
                     .Where(h => h.Status == SessionHandoffStatus.Pending).ToListAsync())
        {
            handoff.Status = SessionHandoffStatus.Superseded;
        }

        foreach (var plan in await dbContext.FamiliarPlanProposals
                     .Where(p => p.Status == FamiliarPlanStatus.Pending).ToListAsync())
        {
            plan.Status = FamiliarPlanStatus.Declined;
        }

        await dbContext.SaveChangesAsync();
    }

    private sealed record SeededPlan(Guid PlanId, Guid Token, Guid ProjectId, Guid ChatId);

    /// <summary>A two-item plan in its own project, drafted from a real chat turn.</summary>
    private async Task<SeededPlan> SeedPlanAsync(bool sensitive = false, bool excludeSecondItem = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Plan project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarPlanDecisionTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Planning chat",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var turn = new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Sequence = 1,
            State = FamiliarChatTurnState.Completed,
            UserText = "Plan this.",
            Output = "Here is a plan.",
            CreatedUtc = DateTime.UtcNow,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };

        var plan = new FamiliarPlanProposal
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            TurnId = turn.Id,
            ProjectId = project.Id,
            Status = FamiliarPlanStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = project.ContextRevision,
            Summary = "Write it, then review it.",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Items =
            [
                new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = 0,
                    Title = "Write the thing",
                    RequestedOutcome = "The thing exists.",
                    Role = AgentSessionRole.Implementer,
                    IsIncluded = true
                },
                new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = 1,
                    Title = "Review the thing",
                    RequestedOutcome = "The thing is reviewed.",
                    Role = AgentSessionRole.Reviewer,
                    IsIncluded = !excludeSecondItem
                }
            ]
        };

        dbContext.Projects.Add(project);
        dbContext.FamiliarChats.Add(chat);
        dbContext.FamiliarChatTurns.Add(turn);
        dbContext.FamiliarPlanProposals.Add(plan);
        await dbContext.SaveChangesAsync();

        return new SeededPlan(plan.Id, plan.ConcurrencyToken, project.Id, chat.Id);
    }

    private async Task<Guid> SeedHandoffAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Handoff project {Guid.NewGuid():N}",
            Purpose = "Seeded beside a plan.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Handoff task",
            RequestedOutcome = "Awaiting a step.",
            Status = FindFamiliar.Server.Domain.TaskStatus.InProgress,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

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

        dbContext.Projects.Add(project);
        dbContext.Tasks.Add(task);
        dbContext.AgentSessions.Add(session);
        dbContext.SessionHandoffs.Add(handoff);
        await dbContext.SaveChangesAsync();

        return handoff.Id;
    }

    private async Task<string> ObtainAccessTokenAsync(string scope)
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        const string redirectUri = "https://chatgpt.com/connector/oauth/plan-tests";

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

    private static async Task<JsonElement> CallMcpToolAsync(HttpClient client, string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new { name = tool, arguments }
                }),
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
        var result = document.RootElement.GetProperty("result");

        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind is JsonValueKind.Object)
        {
            return structured.Clone();
        }

        var text = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var inner = JsonDocument.Parse(text);

        return inner.RootElement.Clone();
    }
}
