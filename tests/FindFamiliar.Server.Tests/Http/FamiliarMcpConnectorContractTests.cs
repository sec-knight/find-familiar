using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Shape rules every tool must satisfy to survive import by an external connector.
///
/// <b>Why this exists.</b> A connector that dislikes one tool's metadata does not report an error to
/// this server — the tool simply does not appear in the client, and `tools/list` here keeps looking
/// perfect. That failure is invisible from the inside, which is precisely the kind that needs a test
/// rather than a convention. These bounds are deliberately conservative: they cost nothing to honour
/// and they keep the whole surface inside the shape that has demonstrably imported.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarMcpConnectorContractTests(FindFamiliarWebApplicationFactory factory)
{
    /// <summary>
    /// An empirical ceiling, not a published vendor limit.
    ///
    /// <b>Say what this is and is not.</b> No connector documents a description limit that these tools
    /// approach, and the largest description on this surface — open_decisions, around 1,140 characters
    /// — is confirmed to import into ChatGPT. So this bound is not a fix for anything observed; it is a
    /// guard against unbounded growth, set just above the largest description known to work. A tool
    /// that needs more prose than the largest one that demonstrably imports should be a deliberate
    /// decision by whoever raises this number, not a side effect of editing a description.
    /// </summary>
    private const int MaxDescriptionLength = 1_200;

    [Fact]
    public async Task Every_tool_description_stays_within_the_length_known_to_import()
    {
        var tools = await ListToolsAsync();

        var oversized = tools
            .Where(tool => tool.Description.Length > MaxDescriptionLength)
            .Select(tool => $"{tool.Name} ({tool.Description.Length})")
            .ToList();

        Assert.True(oversized.Count == 0, $"Descriptions over {MaxDescriptionLength} characters: {string.Join(", ", oversized)}");
    }

    /// <summary>
    /// Line breaks were the one formatting difference between the tool missing from the connector and
    /// the fourteen present. That turned out not to be the cause — the connector was serving a cached
    /// tool list from an older commit — but no tool needs them either: a description is prose, and
    /// prose that has to be paragraphed to be followed is too long for a tool description.
    /// </summary>
    [Fact]
    public async Task No_tool_description_contains_a_line_break()
    {
        var tools = await ListToolsAsync();

        var multiline = tools.Where(tool => tool.Description.Contains('\n')).Select(tool => tool.Name).ToList();

        Assert.True(multiline.Count == 0, $"Descriptions containing line breaks: {string.Join(", ", multiline)}");
    }

    /// <summary>Duplicate names would make one tool unreachable, and which one is not decided here.</summary>
    [Fact]
    public async Task Tool_names_are_unique()
    {
        var names = (await ListToolsAsync()).Select(tool => tool.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>
    /// The contradiction a stale description creates. open_decisions told the caller it could not submit
    /// a decision and that the user must go to Find Familiar directly — while submit_familiar_decision
    /// sat beside it in the same list. A model reading both either disbelieves one or refuses a
    /// capability the human granted, and the human is told to go elsewhere for something they enabled.
    /// </summary>
    [Fact]
    public async Task Open_decisions_points_at_the_tool_that_submits_rather_than_denying_it_exists()
    {
        var tools = await ListToolsAsync();
        var openDecisions = tools.Single(tool => tool.Name == "open_decisions");

        Assert.Contains(tools, tool => tool.Name == "submit_familiar_decision");
        Assert.Contains("submit_familiar_decision", openDecisions.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("must use Find Familiar directly", openDecisions.Description, StringComparison.OrdinalIgnoreCase);

        // The guardrail that must survive the correction: reporting is still not deciding.
        Assert.Contains("only reports", openDecisions.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The plan tool keeps everything that makes it safe and usable while it is being kept small: it
    /// reports rather than acts, and it still teaches a caller how to page to completeness.
    /// </summary>
    [Fact]
    public async Task The_plan_tool_keeps_its_paging_and_read_only_contract()
    {
        var plan = (await ListToolsAsync()).Single(tool => tool.Name == "get_session_handoff_plan");

        foreach (var required in new[]
                 {
                     "nextOffset", "hasMore", "isWholeArtifactRetrieved", "completeness",
                     "Complete", "Page", "PartiallyRetained", "Excerpt"
                 })
        {
            Assert.Contains(required, plan.Description, StringComparison.Ordinal);
        }

        Assert.Contains("never returned", plan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot approve or decline", plan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(plan.ReadOnly, "The plan tool must stay annotated read-only.");
    }

    private sealed record ToolDescriptor(string Name, string Description, bool ReadOnly);

    private async Task<IReadOnlyList<ToolDescriptor>> ListToolsAsync()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
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
        return document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => new ToolDescriptor(
                tool.GetProperty("name").GetString()!,
                tool.GetProperty("description").GetString()!,
                tool.TryGetProperty("annotations", out var annotations)
                    && annotations.TryGetProperty("readOnlyHint", out var readOnly)
                    && readOnly.GetBoolean()))
            .ToList();
    }
}
