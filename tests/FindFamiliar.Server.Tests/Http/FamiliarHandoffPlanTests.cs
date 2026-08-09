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

[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarHandoffPlanTests(FindFamiliarWebApplicationFactory factory)
{
    /// <summary>
    /// The approval invariant, end to end: a plan far longer than the 12,000-character excerpt bound is
    /// reassembled exactly by paging, and the caller is told mechanically when it holds all of it.
    /// </summary>
    [Fact]
    public async Task Sakura_can_page_through_the_complete_plan_without_raw_provider_io()
    {
        var seeded = await SeedAsync();
        using var client = Authenticated();
        var pages = new StringBuilder();
        var offset = 0;
        JsonElement page;

        do
        {
            page = await client.GetFromJsonAsync<JsonElement>(
                $"/api/gateway/handoffs/{seeded.HandoffId}?offset={offset}&maxCharacters=1000");
            pages.Append(page.GetProperty("content").GetString());
            offset = page.GetProperty("nextOffset").GetInt32();
            Assert.Equal(seeded.Content.Length, page.GetProperty("totalLength").GetInt32());
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            // Every page before the last must say so, so a caller cannot stop early believing it is done.
            Assert.False(page.GetProperty("isWholeArtifactRetrieved").GetBoolean());
        }
        while (true);

        // The whole artifact, not merely a lot of it. This is the assertion the old excerpt-only path
        // could never have passed: it stored 12,000 characters and this plan is much longer.
        Assert.Equal(seeded.Content, pages.ToString());
        Assert.True(seeded.Content.Length > 12_000);
        Assert.True(page.GetProperty("isWholeArtifactRetrieved").GetBoolean());
        Assert.Equal("Page", page.GetProperty("completeness").GetString());

        Assert.Contains("Goal and outcome", pages.ToString(), StringComparison.Ordinal);
        Assert.Contains("TAIL_PLAN_MARKER", pages.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_PROVIDER_MARKER", pages.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT_PROVIDER_MARKER", pages.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mcp_handoff_plan_tool_is_read_only_and_returns_a_bounded_page()
    {
        var seeded = await SeedAsync();
        using var client = Authenticated();
        var result = await CallMcpToolAsync(client, "get_session_handoff_plan", new
        {
            handoffId = seeded.HandoffId,
            maxCharacters = 4000
        });

        Assert.Equal(seeded.HandoffId, result.GetProperty("handoffId").GetGuid());
        Assert.Equal(seeded.Content.Length, result.GetProperty("totalLength").GetInt32());
        Assert.True(result.GetProperty("content").GetString()!.Length <= 4000);
        Assert.Equal("Page", result.GetProperty("completeness").GetString());
        Assert.Contains("page of the complete plan", result.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A plan short enough to arrive whole says so in one call, and says nothing remains — the caller
    /// must not be sent paging for a remainder that does not exist.
    /// </summary>
    [Fact]
    public async Task A_short_plan_is_returned_whole_and_declares_itself_complete()
    {
        var seeded = await SeedAsync(planLength: 400);
        using var client = Authenticated();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/gateway/handoffs/{seeded.HandoffId}");

        Assert.Equal("Complete", page.GetProperty("completeness").GetString());
        Assert.Equal(seeded.Content, page.GetProperty("content").GetString());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.True(page.GetProperty("isWholeArtifactRetrieved").GetBoolean());
        Assert.Contains("nothing further remains", page.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pre-fix case, which still exists in the live database: a plan captured before complete
    /// retention has only its bounded excerpt. It must not be dressed up as complete, and must not
    /// invite paging toward text nobody stored. This is the honest half of the fix — a reader is told
    /// the artifact is unavailable rather than handed a cut and left to assume it was whole.
    /// </summary>
    [Fact]
    public async Task A_plan_stored_before_complete_retention_is_reported_as_an_excerpt_not_as_the_plan()
    {
        var seeded = await SeedAsync(planLength: 300, retainComplete: false);
        using var client = Authenticated();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/gateway/handoffs/{seeded.HandoffId}");

        Assert.Equal("Excerpt", page.GetProperty("completeness").GetString());
        Assert.False(page.GetProperty("isWholeArtifactRetrieved").GetBoolean());
        Assert.False(page.GetProperty("isCompleteArtifactAvailable").GetBoolean());
        Assert.False(page.GetProperty("hasMore").GetBoolean());

        var disclosure = page.GetProperty("disclosure").GetString()!;
        Assert.Contains("never retained", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no remainder", disclosure, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An artifact that overran the retention bound reports the shortfall in characters rather than
    /// presenting a prefix as the whole. Distinct from an excerpt: here the gap is measurable.
    /// </summary>
    [Fact]
    public async Task An_over_long_plan_reports_what_it_could_not_keep()
    {
        var seeded = await SeedAsync(planLength: 5_000, declaredOriginalLength: 9_000);
        using var client = Authenticated();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/gateway/handoffs/{seeded.HandoffId}?maxCharacters=4000");

        Assert.Equal("PartiallyRetained", page.GetProperty("completeness").GetString());
        Assert.Equal(9_000, page.GetProperty("originalLength").GetInt32());
        Assert.Equal(seeded.Content.Length, page.GetProperty("totalLength").GetInt32());
        Assert.False(page.GetProperty("isWholeArtifactRetrieved").GetBoolean());
        Assert.Contains(
            $"remaining {9_000 - seeded.Content.Length} characters were never stored",
            page.GetProperty("disclosure").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two Planner runs on one task produce two handoffs. Each must show the plan of its own session,
    /// or a human would approve one proposal having read another.
    /// </summary>
    [Fact]
    public async Task Each_handoff_returns_the_plan_of_its_own_session()
    {
        var seeded = await SeedAsync();
        var second = await AddSecondPlannerRunAsync(seeded.TaskId, seeded.ProjectId);
        using var client = Authenticated();

        var first = await client.GetFromJsonAsync<JsonElement>($"/api/gateway/handoffs/{seeded.HandoffId}?maxCharacters=4000");
        var later = await client.GetFromJsonAsync<JsonElement>($"/api/gateway/handoffs/{second.HandoffId}");

        Assert.Equal(second.Content, later.GetProperty("content").GetString());
        Assert.DoesNotContain("SECOND_RUN_MARKER", first.GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A plan read is a read. The route must not answer a write verb.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task The_plan_route_refuses_write_verbs(string verb)
    {
        var seeded = await SeedAsync();
        using var client = Authenticated();
        using var request = new HttpRequestMessage(new HttpMethod(verb), $"/api/gateway/handoffs/{seeded.HandoffId}");
        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <summary>An unauthenticated caller learns nothing about a plan, including whether it exists.</summary>
    [Fact]
    public async Task An_unauthenticated_caller_cannot_read_a_plan()
    {
        var seeded = await SeedAsync();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/gateway/handoffs/{seeded.HandoffId}");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The Demiplane shows the whole plan too, through bounded paging rather than a single unbounded
    /// page, and links to it from the approval box so it is reachable at the moment of decision.
    /// </summary>
    [Fact]
    public async Task The_demiplane_can_navigate_the_complete_plan()
    {
        var seeded = await SeedAsync();
        using var client = factory.CreateClient();

        var first = await client.GetStringAsync($"/handoffs/{seeded.HandoffId}/plan");
        Assert.Contains("Goal and outcome", first, StringComparison.Ordinal);
        Assert.Contains("part of the complete plan", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TAIL_PLAN_MARKER", first, StringComparison.Ordinal);

        var second = await client.GetStringAsync($"/handoffs/{seeded.HandoffId}/plan?offset=20000");
        Assert.Contains("TAIL_PLAN_MARKER", second, StringComparison.Ordinal);
        Assert.Contains("reached the end of the plan", second, StringComparison.OrdinalIgnoreCase);

        var task = await client.GetStringAsync($"/Tasks/Details/{seeded.TaskId}");
        Assert.Contains($"/handoffs/{seeded.HandoffId}/plan", task, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", FindFamiliarWebApplicationFactory.GatewayTestToken);
        return client;
    }

    /// <param name="planLength">Filler length; the default makes the plan far longer than the excerpt bound.</param>
    /// <param name="retainComplete">False reproduces a session captured before complete retention existed.</param>
    /// <param name="declaredOriginalLength">Set above the retained length to reproduce an over-long artifact.</param>
    private async Task<(Guid HandoffId, Guid TaskId, Guid ProjectId, string Content)> SeedAsync(
        int planLength = 25_000,
        bool retainComplete = true,
        int? declaredOriginalLength = null)
    {
        var content = """
            # Goal and outcome
            Make the new user workflow reproducible.

            ## Scope
            Change only the worker and Sakura read paths.

            ## Concrete changes
            Add diagnostics, isolated workspaces, and a complete plan read.

            ## Architecture and approach
            Persist structured metadata and page the approved artifact.

            ## Risks and migrations
            Add nullable columns and preserve dirty work.

            ## Non-goals
            Do not expose provider transcripts.

            ## Acceptance and verification
            Run focused tests and inspect every page.

            """ + new string('p', planLength) + "\nTAIL_PLAN_MARKER\n";

        // The excerpt the entry carries stays bounded exactly as every other record is — that bound is
        // not what this fix removes. What changed is that the excerpt is now an excerpt *of* something.
        var excerpt = content.Length <= 12_000 ? content : content[..12_000];

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var now = DateTime.UtcNow;
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Handoff plan project {Guid.NewGuid():N}",
            Purpose = "Plan artifact test project.",
            Status = ProjectStatus.Active,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Reproduce the new user workflow",
            RequestedOutcome = "A new user can run the workflow from a clean baseline.",
            Status = TaskStatus.InProgress,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Completed,
            StartedUtc = now.AddMinutes(-5),
            CompletedUtc = now.AddMinutes(-1)
        };
        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = session.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Implementer,
            Kind = SessionHandoffKind.NextRole,
            Status = SessionHandoffStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var planEntry = new ContextEntry
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, TaskId = task.Id, SourceSessionId = session.Id,
            Kind = ContextEntryKind.Plan, Title = "Approval-ready plan", Content = excerpt,
            State = ContextEntryState.Active, CreatedUtc = now
        };

        db.AddRange(project, task, session, handoff, planEntry,
            new ContextEntry
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, TaskId = task.Id, SourceSessionId = session.Id,
                Kind = ContextEntryKind.RawOutput, Title = "Raw provider", Content = "RAW_PROVIDER_MARKER",
                State = ContextEntryState.Active, CreatedUtc = now
            },
            new ContextEntry
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, TaskId = task.Id, SourceSessionId = session.Id,
                Kind = ContextEntryKind.Prompt, Title = "Provider prompt", Content = "PROMPT_PROVIDER_MARKER",
                State = ContextEntryState.Active, CreatedUtc = now
            });

        if (retainComplete)
        {
            db.Add(new ContextEntryArtifact
            {
                Id = Guid.NewGuid(),
                ContextEntryId = planEntry.Id,
                Content = content,
                OriginalLength = declaredOriginalLength ?? content.Length,
                CreatedUtc = now
            });
        }

        await db.SaveChangesAsync();
        return (handoff.Id, task.Id, project.Id, retainComplete ? content : excerpt);
    }

    /// <summary>
    /// A second Planner run on the same task, with its own handoff and its own plan. The first handoff
    /// is decided first, because the schema allows only one pending handoff per task — which is also
    /// the real sequence: approve, run, propose again.
    /// </summary>
    private async Task<(Guid HandoffId, string Content)> AddSecondPlannerRunAsync(Guid taskId, Guid projectId)
    {
        var content = "# Second plan\nSECOND_RUN_MARKER\n" + new string('q', 200);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var now = DateTime.UtcNow.AddMinutes(10);
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Completed,
            StartedUtc = now.AddMinutes(-5),
            CompletedUtc = now
        };
        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SourceSessionId = session.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Implementer,
            Kind = SessionHandoffKind.NextRole,
            Status = SessionHandoffStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(), ProjectId = projectId, TaskId = taskId, SourceSessionId = session.Id,
            Kind = ContextEntryKind.Plan, Title = "Second plan", Content = content,
            State = ContextEntryState.Active, CreatedUtc = now
        };

        await db.SessionHandoffs
            .Where(candidate => candidate.TaskId == taskId && candidate.Status == SessionHandoffStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, SessionHandoffStatus.Approved));

        db.AddRange(session, handoff, entry, new ContextEntryArtifact
        {
            Id = Guid.NewGuid(),
            ContextEntryId = entry.Id,
            Content = content,
            OriginalLength = content.Length,
            CreatedUtc = now
        });
        await db.SaveChangesAsync();
        return (handoff.Id, content);
    }

    private static async Task<JsonElement> CallMcpToolAsync(HttpClient client, string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var raw = (await response.Content.ReadAsStringAsync()).Trim();
        var json = raw.StartsWith('{')
            ? raw
            : raw.Split('\n').Select(line => line.Trim()).First(line => line.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
        {
            return structured.Clone();
        }
        using var content = JsonDocument.Parse(result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!);
        return content.RootElement.Clone();
    }
}
