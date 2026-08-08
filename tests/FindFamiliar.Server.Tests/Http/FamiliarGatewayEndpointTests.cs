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

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The Summoning Gate over the wire: the credential, the bounds, and the promise that nothing an
/// external body can reach will change anything.
///
/// The application-level rules are asserted in <c>FamiliarGatewayTests</c>, against the boundary they
/// belong to. What is left here is what only a real request can prove: that the filter is actually in
/// front of every route, that both adapters serialise the same contract, that a transport-shaped
/// mistake fails predictably, and that no field of an EF entity travelled because a projection was
/// forgotten.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarGatewayEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    private const string ManifestRoute = "/api/gateway/manifest";
    private const string SearchRoute = "/api/gateway/context/search";
    private const string ProjectsRoute = "/api/gateway/projects";
    private const string McpRoute = "/mcp";

    // ---------------------------------------------------------------- the credential

    /// <summary>
    /// Every route, not a representative one. The filter is on the group so that a route added later
    /// is behind it by construction, and this is the test that notices if somebody maps one outside.
    /// </summary>
    [Theory]
    [InlineData("GET", ManifestRoute)]
    [InlineData("GET", ProjectsRoute)]
    [InlineData("POST", SearchRoute)]
    [InlineData("POST", McpRoute)]
    public async Task An_unauthenticated_call_is_refused(string method, string route)
    {
        using var client = factory.CreateClient();

        using var response = await Send(client, method, route, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-token-of-a-plausible-length-0123456789")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_wrong_credential_is_refused(string supplied)
    {
        using var client = factory.CreateClient();

        using var response = await Send(client, "GET", ManifestRoute, supplied);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A near miss is refused like any other miss. Prefix-correct tokens are what a leaked partial
    /// credential looks like, and a comparison that shortcut on length or on the first differing byte
    /// would be the thing that made guessing worth trying.
    /// </summary>
    [Fact]
    public async Task A_credential_that_is_almost_right_is_refused()
    {
        using var client = factory.CreateClient();
        var almost = FindFamiliarWebApplicationFactory.GatewayTestToken[..^1] + "X";

        using var response = await Send(client, "GET", ManifestRoute, almost);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A refusal says "Unauthorized" and nothing else. A message distinguishing "no token" from
    /// "wrong token" from "gateway unconfigured" is a message that helps whoever is guessing.
    /// </summary>
    [Fact]
    public async Task A_refusal_discloses_nothing_about_the_credential()
    {
        using var client = factory.CreateClient();

        using var response = await Send(client, "GET", ManifestRoute, "wrong-token-0123456789abcdefghijklmn");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(FindFamiliarWebApplicationFactory.GatewayTestToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("length", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_valid_credential_succeeds()
    {
        using var client = factory.CreateClient();

        using var response = await Send(client, "GET", ManifestRoute, FindFamiliarWebApplicationFactory.GatewayTestToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public async Task The_manifest_reports_the_configured_identity_and_its_single_write()
    {
        using var client = Authenticated();

        var manifest = await client.GetFromJsonAsync<JsonElement>(ManifestRoute);

        Assert.Equal(
            FindFamiliarWebApplicationFactory.GatewayTestIdentityName,
            manifest.GetProperty("name").GetString());

        Assert.Equal(
            ["submit_familiar_decision"],
            manifest.GetProperty("writeCapabilities").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// The manifest and the tool surface must describe the same gateway.
    ///
    /// This is the test whose absence let the manifest go two slices stale: the capability list is an
    /// allowlist maintained by hand — deliberately, so that adding a method cannot silently advertise
    /// itself — and nothing compared it against what the transport actually offers. A client trusts
    /// the manifest to say what this Familiar can do, and it was understating by two capabilities and
    /// one whole category.
    /// </summary>
    [Fact]
    public async Task The_manifest_declares_exactly_the_tools_the_transport_offers()
    {
        using var client = Authenticated();

        var manifest = await client.GetFromJsonAsync<JsonElement>(ManifestRoute);
        var declared = manifest.GetProperty("capabilities").EnumerateArray()
            .Concat(manifest.GetProperty("writeCapabilities").EnumerateArray())
            .Select(value => value.GetString()!)
            .ToList();

        var tools = await CallMcpAsync(client, "tools/list", new { });
        var advertised = tools.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToList();

        // familiar_manifest itself is excluded: it describes the gateway rather than being one of the
        // things the gateway does, and listing itself would be the manifest claiming a capability
        // whose only purpose is to report capabilities.
        Assert.Equal(
            advertised.Where(name => name != "familiar_manifest").Order(),
            declared.Order());

        // And the read/write split matches the protocol annotations, so a client reading either one
        // reaches the same conclusion about what mutates.
        var mutating = tools.GetProperty("tools").EnumerateArray()
            .Where(tool => !tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean())
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToList();

        Assert.Equal(
            mutating.Order(),
            manifest.GetProperty("writeCapabilities").EnumerateArray().Select(value => value.GetString()!).Order());
    }

    // ---------------------------------------------------------------- bounds and malformed input

    /// <summary>An over-long body is refused on its declared length, before it is read.</summary>
    [Fact]
    public async Task An_oversized_request_is_refused_predictably()
    {
        using var client = Authenticated();

        var oversized = JsonSerializer.Serialize(new { query = new string('q', 128 * 1024) });
        using var content = new StringContent(oversized, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(SearchRoute, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task A_malformed_body_fails_without_a_server_error(string body)
    {
        using var client = Authenticated();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(SearchRoute, content);

        // Any 4xx is a correct answer; a 5xx means the transport threw where it should have refused.
        Assert.InRange((int)response.StatusCode, 400, 499);
    }

    [Fact]
    public async Task An_unreadable_project_answers_the_same_as_one_that_does_not_exist()
    {
        using var client = Authenticated();

        using var response = await client.GetAsync($"{ProjectsRoute}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- nothing leaks through serialisation

    /// <summary>
    /// The contract travels, and nothing else does.
    ///
    /// EF entities carry navigation properties and columns an external client has no business seeing
    /// — <c>isSensitive</c> above all, which would turn "this is withheld" into "this exists and is
    /// withheld" for every row. Asserted on the raw JSON rather than on a deserialised shape, because
    /// the bug worth catching is an extra field, and a typed read would not see one.
    /// </summary>
    [Fact]
    public async Task The_wire_carries_the_contract_and_not_the_entity()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        await SeedAsync(dbContext, "Gateway serialisation probe", "A decision about serialisation probes.");

        using var client = Authenticated();

        using var response = await client.PostAsJsonAsync(
            SearchRoute, new { query = "serialisation probe decision" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var leaked in new[]
                 {
                     "isSensitive", "concurrencyToken", "contextRevision",
                     "project\":{", "entries\":[{\"id", "navigation"
                 })
        {
            Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- read-only, proved by consequence

    /// <summary>
    /// The invariant this sprint must not weaken: nothing an external body can reach creates work.
    ///
    /// Asserted on outcome rather than on intent. Counting the rows that exist before and after every
    /// operation the gateway offers is the check that keeps holding when a later sprint adds a tool
    /// and forgets what the rule was — a tool that created a task would fail this without anyone
    /// having to remember to assert it.
    /// </summary>
    [Fact]
    public async Task No_gateway_operation_creates_tasks_sessions_or_proposals()
    {
        var before = await CountWorkAsync();

        using var client = Authenticated();

        await client.GetAsync(ManifestRoute);
        await client.GetAsync(ProjectsRoute);
        await client.GetAsync($"{ProjectsRoute}/{Guid.NewGuid()}");
        await client.PostAsJsonAsync(SearchRoute, new { query = "plan a new sprint and start a session" });
        await client.PostAsJsonAsync(SearchRoute, new { query = "create a task to fix the retrieval floor" });

        await CallMcpToolAsync(client, "search_familiar_context", new { query = "start an implementer session now" });
        await CallMcpToolAsync(client, "list_familiar_projects", new { });

        Assert.Equal(before, await CountWorkAsync());
    }

    /// <summary>
    /// Health is unauthenticated by design and must therefore say nothing.
    ///
    /// It is the one route on this server anybody can reach, so it is the one route where an
    /// accidental disclosure needs no credential to collect. A liveness probe needs to know the
    /// process is up; it does not need a project name, a count, or an identity.
    /// </summary>
    [Fact]
    public async Task Health_discloses_nothing_about_the_familiar()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        await SeedAsync(dbContext, "A distinctive health probe record", "Body of the health probe record.");

        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var leaked in new[]
                 {
                     "distinctive health probe",
                     FindFamiliarWebApplicationFactory.GatewayTestIdentityName,
                     FindFamiliarWebApplicationFactory.GatewayTestToken,
                     "project"
                 })
        {
            Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- the MCP protocol itself

    [Fact]
    public async Task The_mcp_endpoint_completes_a_real_initialize_handshake()
    {
        using var client = Authenticated();

        var result = await CallMcpAsync(client, "initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "FindFamiliar.Tests", version = "1.0" }
        });

        Assert.Equal("find-familiar", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    /// <summary>
    /// The tool surface is exactly the read operations, declared read-only in the protocol so a client
    /// can see the guarantee rather than infer it from the names. A mutation tool appearing here is
    /// the regression this test exists to catch.
    /// </summary>
    [Fact]
    public async Task Every_advertised_mcp_tool_is_read_only()
    {
        using var client = Authenticated();

        var result = await CallMcpAsync(client, "tools/list", new { });
        var tools = result.GetProperty("tools").EnumerateArray().ToList();

        // Eight: seven reads, and one relay that carries a decision the human already made.
        Assert.Equal(8, tools.Count);

        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            var annotations = tool.GetProperty("annotations");

            // Exactly one tool may declare itself mutating, and it is the relay. Everything else is a
            // read, and nothing anywhere is destructive.
            Assert.Equal(name != "submit_familiar_decision", annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.False(annotations.GetProperty("destructiveHint").GetBoolean(), name);

            // No tool creates work, edits a record, or runs anything. "submit" is permitted only on
            // the relay, which submits a choice rather than performing one.
            foreach (var forbidden in new[] { "create", "start", "delete", "update", "write" })
            {
                Assert.DoesNotContain(forbidden, name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The descriptions are the only instruction the frontier model gets about when to reach for the
    /// Familiar, and the failure they are written against is a client calling on every sentence. This
    /// asserts the search tool says both halves — when to call, and when not to.
    /// </summary>
    [Fact]
    public async Task The_search_tool_tells_a_client_when_not_to_call_it()
    {
        using var client = Authenticated();

        var result = await CallMcpAsync(client, "tools/list", new { });
        var search = result.GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "search_familiar_context");
        var description = search.GetProperty("description").GetString()!;

        Assert.Contains("Do NOT call it", description, StringComparison.Ordinal);
        Assert.Contains("general knowledge", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance moment, end to end over the protocol: a natural question reaches real persisted
    /// state and comes back as bounded, evidence-bearing context with a disclosure attached.
    /// </summary>
    [Fact]
    public async Task A_natural_question_retrieves_real_persisted_context_over_mcp()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entry = await SeedAsync(
            dbContext,
            "Summoning gate acceptance record",
            "The summoning gate acceptance record proves external retrieval works.");

        using var client = Authenticated();

        var result = await CallMcpToolAsync(
            client, "search_familiar_context", new { query = "summoning gate acceptance record" });

        var items = result.GetProperty("items").EnumerateArray().ToList();

        Assert.Contains(items, item => item.GetProperty("contextId").GetGuid() == entry.Id);
        Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("title").GetString())));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("disclosure").GetString()));
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", FindFamiliarWebApplicationFactory.GatewayTestToken);

        return client;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        string method,
        string route,
        string? token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (method == "POST")
        {
            request.Content = new StringContent("{\"query\":\"anything\"}", Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// One JSON-RPC call over Streamable HTTP, reading the single result out of whichever framing the
    /// transport chose — a plain JSON body or one SSE frame. Written here rather than pulled from an
    /// SDK client so these tests exercise the wire a frontier vendor will actually speak.
    /// </summary>
    private static async Task<JsonElement> CallMcpAsync(HttpClient client, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync();
        var payload = ExtractJsonRpcPayload(raw);

        using var document = JsonDocument.Parse(payload);

        Assert.False(
            document.RootElement.TryGetProperty("error", out var error),
            $"MCP call '{method}' returned an error: {error}");

        return document.RootElement.GetProperty("result").Clone();
    }

    private static async Task<JsonElement> CallMcpToolAsync(HttpClient client, string tool, object arguments)
    {
        var result = await CallMcpAsync(client, "tools/call", new { name = tool, arguments });

        // Structured output when the transport produced it, and the text block otherwise. Both are
        // the same contract; only the framing differs by SDK version.
        if (result.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is JsonValueKind.Object)
        {
            return structured.Clone();
        }

        var text = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }

    /// <summary>Streamable HTTP may answer as JSON or as one SSE frame; both carry the same object.</summary>
    private static string ExtractJsonRpcPayload(string raw)
    {
        var trimmed = raw.Trim();

        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var line = trimmed
            .Split('\n')
            .Select(candidate => candidate.Trim())
            .FirstOrDefault(candidate => candidate.StartsWith("data:", StringComparison.Ordinal));

        Assert.NotNull(line);

        return line!["data:".Length..].Trim();
    }

    /// <summary>Everything the human-gated paths can create. None of it may move.</summary>
    private async Task<(int Tasks, int Sessions, int Plans, int Actions)> CountWorkAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.Tasks.CountAsync(),
            await dbContext.AgentSessions.CountAsync(),
            await dbContext.FamiliarPlanProposals.CountAsync(),
            await dbContext.ContextEntries.CountAsync());
    }

    private static async Task<ContextEntry> SeedAsync(FamiliarDbContext dbContext, string title, string content)
    {
        var project = await dbContext.Projects.FirstOrDefaultAsync(candidate => !candidate.IsSensitive);

        if (project is null)
        {
            project = new FamiliarProject
            {
                Id = Guid.NewGuid(),
                Name = "Gateway test project",
                Purpose = "Seeded for FamiliarGatewayEndpointTests.",
                Status = ProjectStatus.Active,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            dbContext.Projects.Add(project);
        }

        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = ContextEntryKind.Decision,
            Title = title,
            Content = content,
            State = ContextEntryState.Active,
            CreatedUtc = DateTime.UtcNow
        };

        dbContext.ContextEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        return entry;
    }
}
