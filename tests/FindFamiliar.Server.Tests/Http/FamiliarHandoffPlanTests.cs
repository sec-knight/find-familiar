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
            offset += page.GetProperty("content").GetString()!.Length;
            Assert.Equal(seeded.Content.Length, page.GetProperty("totalLength").GetInt32());
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }
        while (true);

        Assert.Equal(seeded.Content, pages.ToString());
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
        Assert.Contains("complete bounded Planner artifact", result.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", FindFamiliarWebApplicationFactory.GatewayTestToken);
        return client;
    }

    private async Task<(Guid HandoffId, string Content)> SeedAsync()
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

            """ + new string('p', 5000) + "\nTAIL_PLAN_MARKER\n";

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
        db.AddRange(project, task, session, handoff,
            new ContextEntry
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, TaskId = task.Id, SourceSessionId = session.Id,
                Kind = ContextEntryKind.Plan, Title = "Approval-ready plan", Content = content,
                State = ContextEntryState.Active, CreatedUtc = now
            },
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
