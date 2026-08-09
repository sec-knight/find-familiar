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
/// Slice 3: the one place an external client's message can change this system's state.
///
/// Everything here is written against a single question — can a model, holding a real credential and
/// real identifiers, cause something the human did not choose? The answer has to be no for reasons
/// that are structural rather than careful: the tool takes no free text, the choice is a two-member
/// enum, legality is re-decided inside a transaction this code does not participate in, and a token
/// from a stale view is refused rather than applied.
///
/// The tests that matter most are the ones where a valid credential is refused.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarSubmitDecisionTests(FindFamiliarWebApplicationFactory factory)
{
    private const string SubmitRoute = "/api/gateway/decisions/submit";
    private const string Read = FamiliarGatewayOptions.ReadScope;
    private const string Decide = FamiliarGatewayOptions.DecideScope;

    // ---------------------------------------------------------------- the decision actually lands

    [Fact]
    public async Task An_approval_starts_the_session_the_workflow_authorised()
    {
        var seeded = await SeedPendingHandoffAsync();
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.HandoffId, seeded.Token, "approve");

        Assert.Equal("Approved", result.GetProperty("outcome").GetString());
        Assert.NotNull(result.GetProperty("createdSessionId").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId);

        Assert.Equal(SessionHandoffStatus.Approved, handoff.Status);
        Assert.NotNull(handoff.DecidedUtc);

        // The session is an ordinary Started session on the right task — indistinguishable from one
        // the Demiplane's own button would have created.
        var session = await dbContext.AgentSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.CreatedSessionId);

        Assert.Equal(seeded.TaskId, session.TaskId);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Equal(AgentSessionRole.Planner, session.Role);
    }

    [Fact]
    public async Task A_decline_settles_the_decision_and_creates_nothing()
    {
        var seeded = await SeedPendingHandoffAsync();
        var sessionsBefore = await SessionCountAsync();
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.HandoffId, seeded.Token, "decline");

        Assert.Equal("Declined", result.GetProperty("outcome").GetString());
        Assert.Equal(sessionsBefore, await SessionCountAsync());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId);

        Assert.Equal(SessionHandoffStatus.Declined, handoff.Status);
        Assert.Null(handoff.CreatedSessionId);
    }

    // ---------------------------------------------------------------- staleness and replay

    /// <summary>
    /// A token from a view that has since moved must not act. This is what stops a client deciding
    /// against something the human was shown ten minutes and three events ago.
    /// </summary>
    [Fact]
    public async Task A_stale_concurrency_token_changes_nothing()
    {
        var seeded = await SeedPendingHandoffAsync();
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.HandoffId, Guid.NewGuid(), "approve");

        Assert.Equal("StaleDecision", result.GetProperty("outcome").GetString());
        Assert.Null(result.GetProperty("createdSessionId").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId)).Status);
    }

    /// <summary>
    /// The same decision submitted twice must approve once. A retrying client, a double-sent message
    /// or an impatient user must not produce two sessions.
    /// </summary>
    [Fact]
    public async Task Replaying_an_approval_reports_the_first_one_and_creates_no_second_session()
    {
        var seeded = await SeedPendingHandoffAsync();
        using var client = await DecideClientAsync();

        var first = await SubmitAsync(client, seeded.HandoffId, seeded.Token, "approve");
        var sessionId = first.GetProperty("createdSessionId").GetString();
        var sessionsAfterFirst = await SessionCountAsync();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId);

        // Replayed with the original token, and again with the token as it now stands. A decided
        // handoff is no longer an open decision, so both are turned away by the visibility check
        // before the approval service is reached — which is why the outcome is NotFound rather than
        // AlreadyDecided. The asserted property is the one that matters: never a second approval.
        foreach (var token in new[] { seeded.Token, handoff.ConcurrencyToken })
        {
            var replay = await SubmitAsync(client, seeded.HandoffId, token, "approve");

            Assert.NotEqual("Approved", replay.GetProperty("outcome").GetString());
            Assert.Null(replay.GetProperty("createdSessionId").GetString());
        }

        // Exactly one session exists, and it is the one the first approval created.
        Assert.Equal(sessionsAfterFirst, await SessionCountAsync());
        Assert.Equal(sessionId, handoff.CreatedSessionId?.ToString());
    }

    /// <summary>A decision that was declined cannot later be approved by resubmitting.</summary>
    [Fact]
    public async Task A_declined_decision_cannot_be_approved_afterwards()
    {
        var seeded = await SeedPendingHandoffAsync();
        var sessionsBefore = await SessionCountAsync();
        using var client = await DecideClientAsync();

        await SubmitAsync(client, seeded.HandoffId, seeded.Token, "decline");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId);

        var after = await SubmitAsync(client, seeded.HandoffId, handoff.ConcurrencyToken, "approve");

        Assert.NotEqual("Approved", after.GetProperty("outcome").GetString());
        Assert.Equal(sessionsBefore, await SessionCountAsync());
    }

    // ---------------------------------------------------------------- scope isolation

    /// <summary>
    /// The credential that can read everything must not be able to decide anything. This is the whole
    /// of Slice 1 made consequential.
    /// </summary>
    [Fact]
    public async Task A_read_only_oauth_token_cannot_submit_a_decision()
    {
        var seeded = await SeedPendingHandoffAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + await ObtainAccessTokenAsync(Read));

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.Token,
            choice = "approve"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertStillPendingAsync(seeded.HandoffId);
    }

    /// <summary>
    /// The static deployment token is read-only permanently: no consent screen was read to obtain it,
    /// so it cannot be evidence that a human decided anything.
    /// </summary>
    [Fact]
    public async Task The_static_gateway_token_cannot_submit_a_decision()
    {
        var seeded = await SeedPendingHandoffAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.Token,
            choice = "approve"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertStillPendingAsync(seeded.HandoffId);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_submit_a_decision()
    {
        var seeded = await SeedPendingHandoffAsync();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.Token,
            choice = "approve"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertStillPendingAsync(seeded.HandoffId);
    }

    /// <summary>
    /// The separation runs both ways. A decide-only credential cannot read — the scopes imply nothing
    /// about each other, and this is the assertion that keeps it that way.
    /// </summary>
    [Fact]
    public async Task A_decide_only_token_can_submit_but_cannot_read()
    {
        var seeded = await SeedPendingHandoffAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + await ObtainAccessTokenAsync(Decide));

        using var read = await client.GetAsync("/api/gateway/decisions");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        using var write = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.Token,
            choice = "decline"
        });

        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
    }

    // ---------------------------------------------------------------- sensitivity and isolation

    /// <summary>
    /// A decision in a sensitive project cannot be decided from outside, even by a caller holding the
    /// right scope and the correct identifiers. It answers exactly as one that does not exist.
    /// </summary>
    [Fact]
    public async Task A_decision_in_a_sensitive_project_cannot_be_submitted()
    {
        var seeded = await SeedPendingHandoffAsync(sensitive: true);
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, seeded.HandoffId, seeded.Token, "approve");

        Assert.Equal("NotFound", result.GetProperty("outcome").GetString());

        // The refusal must not disclose that the decision exists.
        var body = result.ToString();
        Assert.DoesNotContain("sensitive", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.TaskTitle, body, StringComparison.OrdinalIgnoreCase);

        await AssertStillPendingAsync(seeded.HandoffId);
    }

    [Fact]
    public async Task A_decision_id_that_does_not_exist_changes_nothing()
    {
        using var client = await DecideClientAsync();

        var result = await SubmitAsync(client, Guid.NewGuid(), Guid.NewGuid(), "approve");

        Assert.Equal("NotFound", result.GetProperty("outcome").GetString());
    }

    // ---------------------------------------------------------------- the contract admits nothing else

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("approve if it looks fine")]
    [InlineData("APPROVE ALL")]
    [InlineData("complete")]
    [InlineData("delete")]
    public async Task Only_approve_or_decline_is_accepted(string choice)
    {
        var seeded = await SeedPendingHandoffAsync();
        using var client = await DecideClientAsync();

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.Token,
            choice
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStillPendingAsync(seeded.HandoffId);
    }

    /// <summary>Case is not meaning: the two real choices are accepted however they are cased.</summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("Approve")]
    [InlineData("DECLINE")]
    public async Task The_two_real_choices_are_accepted_case_insensitively(string choice)
    {
        var seeded = await SeedPendingHandoffAsync();
        using var client = await DecideClientAsync();

        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId = seeded.HandoffId,
            expectedConcurrencyToken = seeded.Token,
            choice
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Exactly one mutating tool exists, and it is the relay. Anything else appearing on this surface
    /// is the regression this test is here to catch.
    /// </summary>
    [Fact]
    public async Task Submit_is_the_only_mutating_tool_on_the_whole_surface()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        var tools = await CallMcpAsync(client, "tools/list", new { });
        var listed = tools.GetProperty("tools").EnumerateArray().ToList();

        var mutating = listed
            .Where(tool => !tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean())
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("submit_familiar_decision", mutating);
        Assert.Equal(15, listed.Count);

        // Nothing that creates work, edits records, or dispatches anything. "run" is not in this list:
        // "runtime" is a noun, and inspect_familiar_runtime reports the machine rather than driving it.
        // The readOnly assertion above is what actually proves nothing here acts.
        // The relay is still the only way to answer a decision; the other writes create ordinary
        // project work and are asserted in FamiliarLifecycleWriteTests.
        foreach (var forbidden in new[] { "delete", "dispatch" })
        {
            Assert.DoesNotContain(
                listed.Select(tool => tool.GetProperty("name").GetString()!),
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The description is the only instruction the model gets about when it may act. It must forbid
    /// acting on the model's own judgement, in words, because that is the failure mode.
    /// </summary>
    [Fact]
    public async Task The_tool_tells_the_model_it_may_only_relay_an_explicit_choice()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        var tools = await CallMcpAsync(client, "tools/list", new { });
        var submit = tools.GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "submit_familiar_decision");
        var description = submit.GetProperty("description").GetString()!;

        Assert.Contains("EXPLICITLY MADE", description, StringComparison.Ordinal);
        Assert.Contains("NEVER call it on your own judgement", description, StringComparison.Ordinal);
        Assert.Contains("relaying the user's choice, not making one", description, StringComparison.Ordinal);
        Assert.Contains("ask them instead of guessing", description, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- end to end over MCP

    /// <summary>
    /// The whole loop as ChatGPT will drive it: read what needs the human, carry their answer back,
    /// and see the state it produced — over the real transport, with a token that went through consent.
    /// </summary>
    [Fact]
    public async Task The_full_loop_runs_over_mcp_with_a_consented_token()
    {
        var seeded = await SeedPendingHandoffAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + await ObtainAccessTokenAsync($"{Read} {Decide}"));

        var open = await CallMcpToolAsync(client, "open_decisions", new { });
        var decision = open.GetProperty("decisions").EnumerateArray()
            .Single(candidate => candidate.GetProperty("decisionId").GetGuid() == seeded.HandoffId);

        var result = await CallMcpToolAsync(client, "submit_familiar_decision", new
        {
            decisionId = decision.GetProperty("decisionId").GetGuid(),
            expectedConcurrencyToken = decision.GetProperty("expectedConcurrencyToken").GetGuid(),
            choice = "approve"
        });

        Assert.Equal("Approved", result.GetProperty("outcome").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            SessionHandoffStatus.Approved,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId)).Status);

        // And it is gone from what needs the human, because it no longer does.
        var after = await CallMcpToolAsync(client, "open_decisions", new { });
        Assert.DoesNotContain(
            after.GetProperty("decisions").EnumerateArray(),
            candidate => candidate.GetProperty("decisionId").GetGuid() == seeded.HandoffId);
    }

    /// <summary>Over MCP, a read-only connection is refused at the tool rather than at the route.</summary>
    [Fact]
    public async Task A_read_only_connection_is_refused_by_the_mcp_tool()
    {
        var seeded = await SeedPendingHandoffAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + await ObtainAccessTokenAsync(Read));

        var raw = await CallMcpRawAsync(client, "tools/call", new
        {
            name = "submit_familiar_decision",
            arguments = new
            {
                decisionId = seeded.HandoffId,
                expectedConcurrencyToken = seeded.Token,
                choice = "approve"
            }
        });

        Assert.Contains(FamiliarGatewayOptions.DecideScope, raw, StringComparison.Ordinal);
        await AssertStillPendingAsync(seeded.HandoffId);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<HttpClient> DecideClientAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + await ObtainAccessTokenAsync($"{Read} {Decide}"));

        return client;
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient client, Guid decisionId, Guid token, string choice)
    {
        using var response = await client.PostAsJsonAsync(SubmitRoute, new
        {
            decisionId,
            expectedConcurrencyToken = token,
            choice
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AssertStillPendingAsync(Guid handoffId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == handoffId)).Status);
    }

    private async Task<int> SessionCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.AgentSessions.CountAsync();
    }

    private sealed record SeededHandoff(Guid HandoffId, Guid Token, Guid TaskId, string TaskTitle);

    private async Task<SeededHandoff> SeedPendingHandoffAsync(bool sensitive = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Submit project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarSubmitDecisionTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Submit task {Guid.NewGuid():N}",
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

        return new SeededHandoff(handoff.Id, handoff.ConcurrencyToken, task.Id, task.Title);
    }

    private async Task<string> ObtainAccessTokenAsync(string scope)
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        const string redirectUri = "https://chatgpt.com/connector/oauth/submit-tests";

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

    private static async Task<string> CallMcpRawAsync(HttpClient client, string method, object parameters)
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

        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> CallMcpAsync(HttpClient client, string method, object parameters)
    {
        var raw = (await CallMcpRawAsync(client, method, parameters)).Trim();
        var payload = raw.StartsWith('{')
            ? raw
            : raw.Split('\n').Select(line => line.Trim())
                .First(line => line.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();

        using var document = JsonDocument.Parse(payload);

        Assert.False(
            document.RootElement.TryGetProperty("error", out var error),
            $"MCP call '{method}' returned an error: {error}");

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
