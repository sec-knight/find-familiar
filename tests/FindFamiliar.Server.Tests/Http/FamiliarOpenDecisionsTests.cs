using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Api.Gateway;
using FindFamiliar.Server.Api.Gateway.OAuth;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Gateway;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Slice 2: what a conversational client is told when the human asks "what needs me?".
///
/// The property under test is narrower than "the tool returns data". It is that the answer is the
/// same one the Demiplane would give — same rows, same classification, same identifiers — because the
/// next slice will let a client carry a person's reply back, and a decision assembled from a second
/// interpretation would be an invitation to approve something that was never asked.
///
/// The other half is what the tool must still be unable to do. Reading a decision, its handoff id and
/// its concurrency token grants nothing, and the tests at the end of this file are what say so.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarOpenDecisionsTests(FindFamiliarWebApplicationFactory factory)
{
    private const string DecisionsRoute = "/api/gateway/decisions";

    // ---------------------------------------------------------------- the shape of an answer

    /// <summary>
    /// Nothing waiting is a real answer, and it must say so. An empty list with no disclosure is the
    /// one result a client will confidently report as "nothing needs you" when it means "I could not
    /// look".
    /// </summary>
    [Fact]
    public async Task With_nothing_awaiting_the_result_is_empty_and_says_why()
    {
        await ClearPendingHandoffsAsync();

        var result = await GetDecisionsAsync();

        Assert.Empty(result.GetProperty("decisions").EnumerateArray());

        var disclosure = result.GetProperty("disclosure").GetString()!;
        Assert.Contains("Nothing is currently waiting", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot submit", disclosure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_awaiting_handoff_produces_exactly_one_structured_decision()
    {
        await ClearPendingHandoffsAsync();
        var seeded = await SeedPendingHandoffAsync();

        var result = await GetDecisionsAsync();
        var decision = Assert.Single(result.GetProperty("decisions").EnumerateArray());

        Assert.Equal(seeded.HandoffId, decision.GetProperty("decisionId").GetGuid());
        Assert.Equal("SessionHandoff", decision.GetProperty("decisionKind").GetString());
        Assert.Equal(seeded.ProjectId, decision.GetProperty("projectId").GetGuid());
        Assert.Equal(seeded.TaskId, decision.GetProperty("taskId").GetGuid());
        Assert.Equal(seeded.TaskTitle, decision.GetProperty("taskTitle").GetString());
        Assert.Equal("Planner", decision.GetProperty("proposedRole").GetString());
        Assert.Equal("NextRole", decision.GetProperty("proposedKind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("projectName").GetString()));
    }

    /// <summary>
    /// The identifiers must be the ones the approval service would actually require. If either drifts,
    /// a client would carry a person's decision to a gate that refuses it — or worse, to the wrong one.
    /// </summary>
    [Fact]
    public async Task The_identifiers_are_exactly_what_the_approval_service_requires()
    {
        await ClearPendingHandoffsAsync();
        var seeded = await SeedPendingHandoffAsync();

        var result = await GetDecisionsAsync();
        var decision = result.GetProperty("decisions").EnumerateArray().Single();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var handoff = await dbContext.SessionHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.HandoffId);

        Assert.Equal(SessionHandoffStatus.Pending, handoff.Status);
        Assert.Equal(handoff.Id, decision.GetProperty("decisionId").GetGuid());
        Assert.Equal(handoff.ConcurrencyToken, decision.GetProperty("expectedConcurrencyToken").GetGuid());
    }

    /// <summary>
    /// A Pending handoff accepts approval or decline, and nothing else. Offering a person a choice the
    /// workflow would refuse makes their answer meaningless.
    /// </summary>
    [Fact]
    public async Task The_legal_choices_are_exactly_what_the_workflow_accepts()
    {
        await ClearPendingHandoffsAsync();
        await SeedPendingHandoffAsync();

        var result = await GetDecisionsAsync();
        var choices = result.GetProperty("decisions").EnumerateArray().Single()
            .GetProperty("legalChoices").EnumerateArray().Select(value => value.GetString()).ToList();

        Assert.Equal(["approve", "decline"], choices);
    }

    /// <summary>
    /// Enough for a person to know what they are deciding without a second lookup: why they are being
    /// asked, and what the finished session found.
    /// </summary>
    [Fact]
    public async Task The_decision_carries_the_reason_and_the_prior_session_evidence()
    {
        await ClearPendingHandoffsAsync();
        await SeedPendingHandoffAsync();

        var decision = (await GetDecisionsAsync()).GetProperty("decisions").EnumerateArray().Single();

        var reason = decision.GetProperty("reason").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(reason));

        // The Demiplane's own sentence for an awaiting handoff, not a paraphrase composed here.
        Assert.Contains("approval", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", reason, StringComparison.OrdinalIgnoreCase);

        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("priorOutcome").GetString()));
    }

    // ---------------------------------------------------------------- sensitivity and isolation

    /// <summary>
    /// A sensitive project's decisions are absent, and the count is the only trace. Naming the project
    /// would be the disclosure the sensitivity rule exists to withhold.
    /// </summary>
    [Fact]
    public async Task Decisions_in_a_sensitive_project_are_withheld_and_only_counted()
    {
        await ClearPendingHandoffsAsync();
        var seeded = await SeedPendingHandoffAsync(sensitive: true);

        var result = await GetDecisionsAsync();
        var body = result.ToString();

        Assert.Empty(result.GetProperty("decisions").EnumerateArray());
        Assert.True(result.GetProperty("sensitiveWithheld").GetInt32() >= 1);

        // Nothing about it may travel: not the project, not the task, not the identifiers.
        foreach (var leaked in new[]
                 {
                     seeded.ProjectName, seeded.TaskTitle,
                     seeded.HandoffId.ToString(), seeded.TaskId.ToString(), seeded.ProjectId.ToString()
                 })
        {
            Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Two projects, one decision each: every decision must carry its own project's identity. A
    /// decision attributed to the wrong project is a person approving work they did not mean to.
    /// </summary>
    [Fact]
    public async Task Every_decision_carries_its_own_project_and_task()
    {
        await ClearPendingHandoffsAsync();
        var first = await SeedPendingHandoffAsync();
        var second = await SeedPendingHandoffAsync();

        var decisions = (await GetDecisionsAsync()).GetProperty("decisions").EnumerateArray().ToList();

        Assert.Equal(2, decisions.Count);

        foreach (var seeded in new[] { first, second })
        {
            var decision = decisions.Single(candidate => candidate.GetProperty("decisionId").GetGuid() == seeded.HandoffId);

            Assert.Equal(seeded.ProjectId, decision.GetProperty("projectId").GetGuid());
            Assert.Equal(seeded.TaskId, decision.GetProperty("taskId").GetGuid());
            Assert.Equal(seeded.TaskTitle, decision.GetProperty("taskTitle").GetString());
        }
    }

    /// <summary>Bounded like every other gateway answer, and the overflow is counted rather than dropped.</summary>
    [Fact]
    public async Task The_result_is_bounded_and_reports_what_it_omitted()
    {
        await ClearPendingHandoffsAsync();

        for (var index = 0; index < FamiliarOpenDecisionList.MaxDecisions + 2; index++)
        {
            await SeedPendingHandoffAsync();
        }

        var result = await GetDecisionsAsync();

        Assert.Equal(
            FamiliarOpenDecisionList.MaxDecisions,
            result.GetProperty("decisions").EnumerateArray().Count());
        Assert.Equal(2, result.GetProperty("omitted").GetInt32());
        Assert.Contains("not listed", result.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- credentials and scope

    [Fact]
    public async Task The_static_read_token_may_call_it()
    {
        await ClearPendingHandoffsAsync();
        await SeedPendingHandoffAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var response = await client.GetAsync(DecisionsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_may_not_call_it()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DecisionsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The scope model from Slice 1, applied. familiar.decide implies nothing about reading, so a
    /// credential granted only the decision scope is refused here — with 403, because the credential
    /// is valid and simply lacks this permission.
    /// </summary>
    [Fact]
    public async Task A_credential_without_the_read_scope_may_not_call_it()
    {
        var decideOnly = await ObtainAccessTokenAsync(FamiliarGatewayOptions.DecideScope);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + decideOnly);

        using var response = await client.GetAsync(DecisionsRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "insufficient_scope",
            response.Headers.WwwAuthenticate.Single().ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_scoped_oauth_token_may_call_it()
    {
        await ClearPendingHandoffsAsync();
        await SeedPendingHandoffAsync();

        var readToken = await ObtainAccessTokenAsync(FamiliarGatewayOptions.ReadScope);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + readToken);

        using var response = await client.GetAsync(DecisionsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- it reports, it does not act

    [Fact]
    public async Task The_tool_is_advertised_read_only_over_mcp()
    {
        using var client = Authenticated();

        var result = await CallMcpAsync(client, "tools/list", new { });
        var tool = result.GetProperty("tools").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == "open_decisions");

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());

        // The description must tell the model it cannot act, because the model will be asked to.
        var description = tool.GetProperty("description").GetString()!;
        Assert.Contains("cannot approve", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The boundary this slice must stop at. Reading a decision — including its handoff id and its
    /// concurrency token — changes nothing, and no session, task or handoff transition results.
    /// </summary>
    [Fact]
    public async Task Reading_decisions_changes_no_state_at_all()
    {
        await ClearPendingHandoffsAsync();
        var seeded = await SeedPendingHandoffAsync();
        var before = await StateFingerprintAsync();

        using var client = Authenticated();

        await client.GetAsync(DecisionsRoute);
        await CallMcpToolAsync(client, "open_decisions", new { });
        await CallMcpToolAsync(client, "open_decisions", new { });

        Assert.Equal(before, await StateFingerprintAsync());

        // The handoff is untouched: still Pending, still carrying the token it was read with.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var handoff = await dbContext.SessionHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.HandoffId);

        Assert.Equal(SessionHandoffStatus.Pending, handoff.Status);
        Assert.Null(handoff.DecidedUtc);
        Assert.Null(handoff.CreatedSessionId);
    }

    /// <summary>
    /// Holding the identifiers is not authority.
    ///
    /// Slice 2 could assert this by noting that no submission surface existed. Slice 3 built one, so
    /// the claim is now the sharper one: a connection that can read decisions still cannot act on
    /// them. open_decisions requires familiar.read; submitting requires familiar.decide, and this
    /// caller holds only the first.
    /// </summary>
    [Fact]
    public async Task Reading_a_decision_grants_no_ability_to_act_on_it()
    {
        await ClearPendingHandoffsAsync();
        var seeded = await SeedPendingHandoffAsync();

        // The static token reads everything and can decide nothing, permanently.
        using var client = Authenticated();
        var body = new { decisionId = seeded.HandoffId, expectedConcurrencyToken = seeded.Token, choice = "approve" };

        using var rest = await client.PostAsJsonAsync("/api/gateway/decisions/submit", body);
        Assert.Equal(HttpStatusCode.Forbidden, rest.StatusCode);

        var mcp = await CallMcpRawAsync(client, new
        {
            name = "submit_familiar_decision",
            arguments = body
        });

        Assert.Contains(FamiliarGatewayOptions.DecideScope, mcp, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId)).Status);
    }

    private static async Task<string> CallMcpRawAsync(HttpClient client, object toolCall)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = toolCall }),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        using var response = await client.SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        return client;
    }

    private async Task<JsonElement> GetDecisionsAsync()
    {
        using var client = Authenticated();

        return await client.GetFromJsonAsync<JsonElement>(DecisionsRoute);
    }

    private sealed record SeededHandoff(
        Guid HandoffId, Guid Token, Guid ProjectId, string ProjectName, Guid TaskId, string TaskTitle);

    /// <summary>
    /// A task whose Implementer session completed and proposed a Planner next — the ordinary shape of
    /// a handoff waiting on a human. Seeded through the real rows the approval service owns, so the
    /// projection classifies it exactly as it would in production.
    /// </summary>
    private async Task<SeededHandoff> SeedPendingHandoffAsync(bool sensitive = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Decision project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarOpenDecisionsTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Decision task {Guid.NewGuid():N}",
            RequestedOutcome = "A task with a step awaiting human approval.",
            Status = TaskStatus.InProgress,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Implementer,
            Status = AgentSessionStatus.Completed,
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = session.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Planner,
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

        return new SeededHandoff(
            handoff.Id, handoff.ConcurrencyToken, project.Id, project.Name, task.Id, task.Title);
    }

    /// <summary>
    /// The shared fixture accumulates state across the collection, so a test asserting "one decision"
    /// must first establish that only its own is pending. Handoffs are superseded rather than deleted,
    /// which is the transition the domain already uses for a decision point that no longer applies.
    /// </summary>
    private async Task ClearPendingHandoffsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var pending = await dbContext.SessionHandoffs
            .Where(handoff => handoff.Status == SessionHandoffStatus.Pending)
            .ToListAsync();

        foreach (var handoff in pending)
        {
            handoff.Status = SessionHandoffStatus.Superseded;
            handoff.UpdatedUtc = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>Everything a consequential operation would move.</summary>
    private async Task<(int Tasks, int Sessions, int Handoffs, int Pending, int Approved, int Entries)>
        StateFingerprintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.Tasks.CountAsync(),
            await dbContext.AgentSessions.CountAsync(),
            await dbContext.SessionHandoffs.CountAsync(),
            await dbContext.SessionHandoffs.CountAsync(h => h.Status == SessionHandoffStatus.Pending),
            await dbContext.SessionHandoffs.CountAsync(h => h.Status == SessionHandoffStatus.Approved),
            await dbContext.ContextEntries.CountAsync());
    }

    /// <summary>Registers, consents, and redeems a code for a token carrying exactly the named scope.</summary>
    private async Task<string> ObtainAccessTokenAsync(string scope)
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        const string redirectUri = "https://chatgpt.com/connector/oauth/open-decisions-tests";

        using var registration = await client.PostAsJsonAsync(
            "/oauth/register", new { redirect_uris = new[] { redirectUri }, client_name = "ChatGPT" });
        registration.EnsureSuccessStatusCode();

        var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("client_id").GetString()!;

        var verifier = FamiliarOAuthArtifacts.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = FamiliarOAuthArtifacts.Base64UrlEncode(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

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
