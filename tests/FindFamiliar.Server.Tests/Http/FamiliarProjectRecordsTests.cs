using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Gateway;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Frontend parity for a project's own recorded context.
///
/// The gap this closes was subtle and worth naming precisely. The Familiar could already *search* a
/// project's records, so the information was not secret — but search applies a relevance floor, which
/// is right for a question and wrong for an inventory. A constraint the user wrote once and never
/// phrased again would never clear the floor, and a client cannot ask about a record whose existence
/// it has no way to learn. The project page enumerates; so must this.
///
/// So the tests below are about enumeration being unconditional, and about the two subtractions that
/// remain deliberate.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarProjectRecordsTests(FindFamiliarWebApplicationFactory factory)
{
    private static string Route(Guid projectId) => $"/api/gateway/projects/{projectId}";

    // ---------------------------------------------------------------- enumeration, not search

    /// <summary>
    /// The point of the slice. A record whose words appear in no plausible query is still returned,
    /// because enumeration does not ask what the reader was looking for.
    /// </summary>
    [Fact]
    public async Task A_project_record_is_returned_without_anyone_having_searched_for_it()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Constraint, "Zzyzx boundary", "Qwghlm applies to everything here.");

        var context = await GetContextAsync(project);
        var record = Assert.Single(context.GetProperty("records").EnumerateArray());

        Assert.Equal("Zzyzx boundary", record.GetProperty("title").GetString());
        Assert.Equal("Constraint", record.GetProperty("category").GetString());
        Assert.Contains("Qwghlm", record.GetProperty("excerpt").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same record, put through the search path, does not come back — which is exactly why
    /// enumeration was needed and why search was not an acceptable substitute.
    /// </summary>
    [Fact]
    public async Task The_same_record_enumeration_returns_is_not_reachable_by_an_unrelated_search()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Constraint, "Obscure standing rule", "Vorpal snicker-snack.");

        using var client = Authenticated();

        var searched = await client.PostAsJsonAsync(
            "/api/gateway/context/search",
            new { query = "what are the standing constraints on this project", projectId = project });
        var searchBody = await searched.Content.ReadAsStringAsync();

        var enumerated = (await GetContextAsync(project)).GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("title").GetString())
            .ToList();

        Assert.Contains("Obscure standing rule", enumerated);
        Assert.DoesNotContain("Vorpal snicker-snack", searchBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Project-level records only. A task's records belong to that task and are returned by
    /// get_task_detail; mixing them here would make a project's own context unreadable.
    /// </summary>
    [Fact]
    public async Task Task_records_are_not_returned_as_project_records()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Goal, "Project-level goal", "Belongs to the project.");
        var taskId = await SeedTaskWithRecordAsync(project, "Task-level note", "Belongs to a task.");

        var context = await GetContextAsync(project);
        var titles = context.GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("title").GetString())
            .ToList();

        Assert.Contains("Project-level goal", titles);
        Assert.DoesNotContain("Task-level note", titles);
        Assert.NotEqual(Guid.Empty, taskId);
    }

    /// <summary>A superseded record is not current context, and the project page does not show it either.</summary>
    [Fact]
    public async Task A_superseded_record_is_not_returned()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Decision, "Retired decision", "SUPERSEDEDMARKER old.", state: ContextEntryState.Superseded);

        var context = await GetContextAsync(project);

        Assert.DoesNotContain("SUPERSEDEDMARKER", context.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the two subtractions

    [Fact]
    public async Task A_record_marked_sensitive_is_not_returned_but_is_counted()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Goal, "Visible goal", "Ordinary.");
        await SeedRecordAsync(project, ContextEntryKind.Decision, "Private note", "SENSITIVEMARKER private.", sensitive: true);

        var context = await GetContextAsync(project);

        Assert.DoesNotContain("SENSITIVEMARKER", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private note", context.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, context.GetProperty("recordsWithheld").GetInt32());
    }

    [Theory]
    [InlineData(ContextEntryKind.Prompt)]
    [InlineData(ContextEntryKind.RawOutput)]
    public async Task Raw_provider_input_and_output_are_never_returned(ContextEntryKind kind)
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, kind, $"{kind} artifact", "RAWMARKER working material.");

        var context = await GetContextAsync(project);

        Assert.DoesNotContain("RAWMARKER", context.ToString(), StringComparison.Ordinal);
        Assert.True(context.GetProperty("recordsWithheld").GetInt32() >= 1);
    }

    // ---------------------------------------------------------------- isolation and bounds

    [Fact]
    public async Task A_projects_records_do_not_leak_into_another_project()
    {
        var first = await SeedProjectAsync();
        var second = await SeedProjectAsync();

        await SeedRecordAsync(first, ContextEntryKind.Goal, "First project goal", "Belongs to the first.");
        await SeedRecordAsync(second, ContextEntryKind.Goal, "Second project goal", "Belongs to the second.");

        var firstContext = await GetContextAsync(first);
        var secondContext = await GetContextAsync(second);

        Assert.DoesNotContain("Second project goal", firstContext.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("First project goal", secondContext.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A sensitive project answers as one that does not exist — unchanged, and re-asserted here.</summary>
    [Fact]
    public async Task A_sensitive_projects_records_are_unreachable()
    {
        var project = await SeedProjectAsync(sensitive: true);
        await SeedRecordAsync(project, ContextEntryKind.Goal, "Hidden goal", "HIDDENMARKER private project.");

        using var client = Authenticated();
        using var response = await client.GetAsync(Route(project));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("HIDDENMARKER", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_record_list_is_bounded_and_reports_what_it_withheld()
    {
        var project = await SeedProjectAsync();

        for (var index = 0; index < FamiliarProjectContext.MaxRecords + 4; index++)
        {
            await SeedRecordAsync(project, ContextEntryKind.Decision, $"Record {index}", $"Body {index}.");
        }

        var context = await GetContextAsync(project);

        Assert.Equal(FamiliarProjectContext.MaxRecords, context.GetProperty("records").EnumerateArray().Count());
        Assert.Equal(4, context.GetProperty("recordsWithheld").GetInt32());
    }

    [Fact]
    public async Task A_long_record_is_truncated_and_says_so()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Goal, "Long standing goal", new string('y', 5_000));

        var context = await GetContextAsync(project);
        var excerpt = context.GetProperty("records").EnumerateArray()
            .Single(record => record.GetProperty("title").GetString() == "Long standing goal")
            .GetProperty("excerpt").GetString()!;

        Assert.Contains("truncated", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.True(excerpt.Length < 5_000);
    }

    [Fact]
    public async Task A_project_with_no_records_returns_an_empty_list_rather_than_omitting_it()
    {
        var project = await SeedProjectAsync();

        var context = await GetContextAsync(project);

        Assert.Empty(context.GetProperty("records").EnumerateArray());
        Assert.Equal(0, context.GetProperty("recordsWithheld").GetInt32());
    }

    // ---------------------------------------------------------------- parity of the two adapters

    /// <summary>
    /// REST and MCP serialise one contract, so the records reach both. A client choosing a transport
    /// must not be choosing how much of the project it can see.
    /// </summary>
    [Fact]
    public async Task Mcp_and_rest_return_the_same_records()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Constraint, "Shared constraint", "Both adapters see this.");

        using var client = Authenticated();

        var rest = (await client.GetFromJsonAsync<JsonElement>(Route(project)))
            .GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("title").GetString()).ToList();

        var mcp = (await CallMcpToolAsync(client, "get_project_context", new { projectId = project }))
            .GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("title").GetString()).ToList();

        Assert.Equal(rest, mcp);
        Assert.Contains("Shared constraint", mcp);
    }

    /// <summary>The project page and the gateway enumerate one definition, so they cannot disagree.</summary>
    [Fact]
    public async Task The_projection_the_page_uses_is_the_projection_the_gateway_uses()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Goal, "Definition check", "One list, two readers.");
        await SeedRecordAsync(project, ContextEntryKind.Decision, "Also visible", "Second entry.");

        using var scope = factory.Services.CreateScope();
        var projection = scope.ServiceProvider.GetRequiredService<IContextProjectionService>();
        var fromService = (await projection.GetProjectEntriesAsync(project))
            .Select(entry => entry.Title).Order().ToList();

        var fromGateway = (await GetContextAsync(project)).GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("title").GetString()!).Order().ToList();

        Assert.Equal(fromService, fromGateway);
    }

    [Fact]
    public async Task Reading_project_records_changes_nothing()
    {
        var project = await SeedProjectAsync();
        await SeedRecordAsync(project, ContextEntryKind.Goal, "Read-only probe", "Nothing should move.");

        var before = await FingerprintAsync();

        using var client = Authenticated();
        await client.GetAsync(Route(project));
        await CallMcpToolAsync(client, "get_project_context", new { projectId = project });

        Assert.Equal(before, await FingerprintAsync());
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        return client;
    }

    private async Task<JsonElement> GetContextAsync(Guid projectId)
    {
        using var client = Authenticated();

        return await client.GetFromJsonAsync<JsonElement>(Route(projectId));
    }

    private async Task<Guid> SeedProjectAsync(bool sensitive = false)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Records project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarProjectRecordsTests.",
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        return project.Id;
    }

    private async Task SeedRecordAsync(
        Guid projectId,
        ContextEntryKind kind,
        string title,
        string content,
        bool sensitive = false,
        ContextEntryState state = ContextEntryState.Active)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TaskId = null,
            Kind = kind,
            Title = title,
            Content = content,
            State = state,
            IsSensitive = sensitive,
            CreatedUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedTaskWithRecordAsync(Guid projectId, string title, string content)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = $"Records task {Guid.NewGuid():N}",
            RequestedOutcome = "Holds a task-level record.",
            Status = FindFamiliar.Server.Domain.TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TaskId = task.Id,
            Kind = ContextEntryKind.Decision,
            Title = title,
            Content = content,
            State = ContextEntryState.Active,
            CreatedUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();

        return task.Id;
    }

    private async Task<(int Entries, int Tasks, int Projects)> FingerprintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.ContextEntries.CountAsync(),
            await dbContext.Tasks.CountAsync(),
            await dbContext.Projects.CountAsync());
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
