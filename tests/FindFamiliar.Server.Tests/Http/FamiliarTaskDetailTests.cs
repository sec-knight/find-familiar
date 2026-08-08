using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Gateway;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Frontend parity for one task: the Familiar must be able to find out what the Demiplane's task page
/// shows about it.
///
/// The page renders a <c>TaskContextDocument</c> — project, task, records, sessions — plus whichever
/// decision is pending. This surface returns the same thing, minus the two categories no external
/// answer ever carries. The tests below are mostly about those two subtractions being right, because
/// the service this leans on is built for assignment packets and applies no sensitivity rule of its
/// own: every filter here is one this boundary adds, so every one of them needs a test.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarTaskDetailTests(FindFamiliarWebApplicationFactory factory)
{
    private static string Route(Guid taskId) => $"/api/gateway/tasks/{taskId}";

    // ---------------------------------------------------------------- what the page shows

    [Fact]
    public async Task A_task_reports_its_state_reason_and_identity()
    {
        var seeded = await SeedTaskAsync();

        var detail = await GetDetailAsync(seeded.TaskId);

        Assert.Equal(seeded.TaskId, detail.GetProperty("taskId").GetGuid());
        Assert.Equal(seeded.Title, detail.GetProperty("title").GetString());
        Assert.Equal("A task seeded for detail parity.", detail.GetProperty("requestedOutcome").GetString());
        Assert.Equal(seeded.ProjectId, detail.GetProperty("projectId").GetGuid());
        Assert.Equal(seeded.ProjectName, detail.GetProperty("projectName").GetString());

        // Display state and reason come from the Demiplane's own classification, never re-derived.
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("displayState").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task The_sessions_that_ran_are_reported_with_role_status_and_timing()
    {
        var seeded = await SeedTaskAsync(withCompletedImplementer: true);

        var detail = await GetDetailAsync(seeded.TaskId);
        var session = Assert.Single(detail.GetProperty("sessions").EnumerateArray());

        Assert.Equal("Implementer", session.GetProperty("role").GetString());
        Assert.Equal("Completed", session.GetProperty("status").GetString());
        Assert.Equal("test-provider", session.GetProperty("provider").GetString());
        Assert.NotEqual(JsonValueKind.Null, session.GetProperty("completedUtc").ValueKind);
    }

    /// <summary>
    /// A record must be traceable to the session that produced it, so a reader can say "the Reviewer
    /// found X" instead of "something found X".
    /// </summary>
    [Fact]
    public async Task Records_are_returned_and_linked_to_the_session_that_produced_them()
    {
        var seeded = await SeedTaskAsync(withCompletedImplementer: true);
        await SeedRecordAsync(seeded, ContextEntryKind.Summary, "Implementer summary", "It did the thing.", linkToSession: true);

        var detail = await GetDetailAsync(seeded.TaskId);
        var record = detail.GetProperty("records").EnumerateArray()
            .Single(candidate => candidate.GetProperty("title").GetString() == "Implementer summary");

        Assert.Equal("Summary", record.GetProperty("category").GetString());
        Assert.Contains("did the thing", record.GetProperty("excerpt").GetString()!, StringComparison.Ordinal);
        Assert.Equal(seeded.SessionId, record.GetProperty("sourceSessionId").GetGuid());
    }

    /// <summary>The decision a task is waiting on, with the identifiers a later submission needs.</summary>
    [Fact]
    public async Task A_task_awaiting_a_decision_carries_it_with_its_identifiers()
    {
        var seeded = await SeedTaskAsync(withCompletedImplementer: true, withPendingHandoff: true);

        var detail = await GetDetailAsync(seeded.TaskId);
        var awaiting = detail.GetProperty("awaitingDecision");

        Assert.Equal(seeded.HandoffId, awaiting.GetProperty("decisionId").GetGuid());
        Assert.Equal(seeded.HandoffToken, awaiting.GetProperty("expectedConcurrencyToken").GetGuid());
        Assert.Equal(["approve", "decline"], awaiting.GetProperty("legalChoices").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("waiting on a decision", detail.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_task_awaiting_nothing_says_so_rather_than_omitting_the_field()
    {
        var seeded = await SeedTaskAsync();

        var detail = await GetDetailAsync(seeded.TaskId);

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("awaitingDecision").ValueKind);
        Assert.Contains("Nothing on this task is waiting", detail.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the two subtractions

    /// <summary>
    /// Raw provider prompts and output are never returned to any external caller — the same rule the
    /// retrieval path applies. The service this surface leans on does not apply it, so this boundary
    /// must, and this is the test that says it does.
    /// </summary>
    [Theory]
    [InlineData(ContextEntryKind.Prompt)]
    [InlineData(ContextEntryKind.RawOutput)]
    public async Task Raw_provider_input_and_output_are_never_returned(ContextEntryKind kind)
    {
        var seeded = await SeedTaskAsync(withCompletedImplementer: true);
        await SeedRecordAsync(seeded, kind, $"{kind} artifact", "SECRETPROMPTMARKER internal working material.");

        var detail = await GetDetailAsync(seeded.TaskId);
        var body = detail.ToString();

        Assert.DoesNotContain("SECRETPROMPTMARKER", body, StringComparison.Ordinal);
        Assert.DoesNotContain(kind.ToString(), detail.GetProperty("records").ToString(), StringComparison.Ordinal);

        // Withheld, not vanished: the count is what stops a reader believing it saw everything.
        Assert.True(detail.GetProperty("recordsWithheld").GetInt32() >= 1);
    }

    [Fact]
    public async Task A_record_marked_sensitive_is_not_returned()
    {
        var seeded = await SeedTaskAsync();
        await SeedRecordAsync(seeded, ContextEntryKind.Decision, "Sensitive note", "SENSITIVEMARKER private.", sensitive: true);

        var detail = await GetDetailAsync(seeded.TaskId);

        Assert.DoesNotContain("SENSITIVEMARKER", detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            detail.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("title").GetString()),
            title => title == "Sensitive note");
    }

    /// <summary>
    /// A task in a sensitive project answers exactly as one that does not exist. Naming which of the
    /// two applied would be the disclosure the rule withholds.
    /// </summary>
    [Fact]
    public async Task A_task_in_a_sensitive_project_answers_as_though_it_does_not_exist()
    {
        var seeded = await SeedTaskAsync(sensitiveProject: true);

        using var client = Authenticated();
        using var response = await client.GetAsync(Route(seeded.TaskId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(seeded.Title, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", body, StringComparison.OrdinalIgnoreCase);

        // And the MCP answer is the same refusal, not a different one.
        var viaTool = await CallMcpToolAsync(client, "get_task_detail", new { taskId = seeded.TaskId });
        Assert.True(viaTool.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task An_unknown_task_id_answers_the_same_as_an_unreadable_one()
    {
        using var client = Authenticated();

        using var response = await client.GetAsync(Route(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A task id from another project is resolved on its own merits, never against a caller's guess.</summary>
    [Fact]
    public async Task Each_task_reports_its_own_project()
    {
        var first = await SeedTaskAsync();
        var second = await SeedTaskAsync();

        var firstDetail = await GetDetailAsync(first.TaskId);
        var secondDetail = await GetDetailAsync(second.TaskId);

        Assert.Equal(first.ProjectId, firstDetail.GetProperty("projectId").GetGuid());
        Assert.Equal(second.ProjectId, secondDetail.GetProperty("projectId").GetGuid());
        Assert.NotEqual(
            firstDetail.GetProperty("projectId").GetGuid(),
            secondDetail.GetProperty("projectId").GetGuid());
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public async Task The_record_list_is_bounded_and_reports_what_it_withheld()
    {
        var seeded = await SeedTaskAsync();

        for (var index = 0; index < FamiliarTaskDetail.MaxRecords + 3; index++)
        {
            await SeedRecordAsync(seeded, ContextEntryKind.Decision, $"Record {index}", $"Body {index}.");
        }

        var detail = await GetDetailAsync(seeded.TaskId);

        Assert.Equal(FamiliarTaskDetail.MaxRecords, detail.GetProperty("records").EnumerateArray().Count());
        Assert.Equal(3, detail.GetProperty("recordsWithheld").GetInt32());
        Assert.Contains("not shown", detail.GetProperty("disclosure").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_long_record_is_truncated_and_says_so()
    {
        var seeded = await SeedTaskAsync();
        await SeedRecordAsync(seeded, ContextEntryKind.Implementation, "Long artifact", new string('x', 5_000));

        var detail = await GetDetailAsync(seeded.TaskId);
        var excerpt = detail.GetProperty("records").EnumerateArray()
            .Single(r => r.GetProperty("title").GetString() == "Long artifact")
            .GetProperty("excerpt").GetString()!;

        Assert.Contains("truncated", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.True(excerpt.Length < 5_000);
    }

    // ---------------------------------------------------------------- authorization and read-only

    [Fact]
    public async Task An_unauthenticated_caller_may_not_read_a_task()
    {
        var seeded = await SeedTaskAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Route(seeded.TaskId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_task_changes_nothing()
    {
        var seeded = await SeedTaskAsync(withCompletedImplementer: true, withPendingHandoff: true);
        var before = await FingerprintAsync();

        using var client = Authenticated();
        await client.GetAsync(Route(seeded.TaskId));
        await CallMcpToolAsync(client, "get_task_detail", new { taskId = seeded.TaskId });

        Assert.Equal(before, await FingerprintAsync());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == seeded.HandoffId)).Status);
    }

    [Fact]
    public async Task The_tool_is_advertised_read_only_and_declared_in_the_manifest()
    {
        using var client = Authenticated();

        var tools = await CallMcpAsync(client, "tools/list", new { });
        var tool = tools.GetProperty("tools").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == "get_task_detail");

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        var manifest = await client.GetFromJsonAsync<JsonElement>("/api/gateway/manifest");

        Assert.Contains(
            "get_task_detail",
            manifest.GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()));
        Assert.DoesNotContain(
            "get_task_detail",
            manifest.GetProperty("writeCapabilities").EnumerateArray().Select(v => v.GetString()));
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        return client;
    }

    private async Task<JsonElement> GetDetailAsync(Guid taskId)
    {
        using var client = Authenticated();

        return await client.GetFromJsonAsync<JsonElement>(Route(taskId));
    }

    private sealed record SeededTask(
        Guid TaskId, string Title, Guid ProjectId, string ProjectName, Guid SessionId, Guid HandoffId, Guid HandoffToken);

    private async Task<SeededTask> SeedTaskAsync(
        bool sensitiveProject = false,
        bool withCompletedImplementer = false,
        bool withPendingHandoff = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Detail project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarTaskDetailTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitiveProject,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Detail task {Guid.NewGuid():N}",
            RequestedOutcome = "A task seeded for detail parity.",
            Status = withCompletedImplementer ? TaskStatus.InProgress : TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        dbContext.Tasks.Add(task);

        var sessionId = Guid.Empty;
        var handoffId = Guid.Empty;
        var handoffToken = Guid.Empty;

        if (withCompletedImplementer)
        {
            var session = new AgentSession
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Role = AgentSessionRole.Implementer,
                Status = AgentSessionStatus.Completed,
                Provider = "test-provider",
                StartedUtc = DateTime.UtcNow.AddMinutes(-20),
                CompletedUtc = DateTime.UtcNow.AddMinutes(-10)
            };

            dbContext.AgentSessions.Add(session);
            sessionId = session.Id;

            if (withPendingHandoff)
            {
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

                dbContext.SessionHandoffs.Add(handoff);
                handoffId = handoff.Id;
                handoffToken = handoff.ConcurrencyToken;
            }
        }

        await dbContext.SaveChangesAsync();

        return new SeededTask(task.Id, task.Title, project.Id, project.Name, sessionId, handoffId, handoffToken);
    }

    private async Task SeedRecordAsync(
        SeededTask seeded,
        ContextEntryKind kind,
        string title,
        string content,
        bool sensitive = false,
        bool linkToSession = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = seeded.ProjectId,
            TaskId = seeded.TaskId,
            SourceSessionId = linkToSession && seeded.SessionId != Guid.Empty ? seeded.SessionId : null,
            Kind = kind,
            Title = title,
            Content = content,
            State = ContextEntryState.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();
    }

    private async Task<(int Tasks, int Sessions, int Handoffs, int Entries)> FingerprintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.Tasks.CountAsync(),
            await dbContext.AgentSessions.CountAsync(),
            await dbContext.SessionHandoffs.CountAsync(),
            await dbContext.ContextEntries.CountAsync());
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
