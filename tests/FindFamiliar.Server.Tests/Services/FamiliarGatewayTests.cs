using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar.Chat.Brief;
using FindFamiliar.Server.Services.Familiar.Chat.Retrieval;
using FindFamiliar.Server.Services.Familiar.Gateway;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The Summoning Gate, at the boundary rather than at the transport.
///
/// These are the tests that must hold for <i>every</i> adapter, now and later, which is why they are
/// written against <see cref="FamiliarGateway"/> and not against MCP or REST. The sprint's central
/// claim is that an external frontier client is a body the Familiar can inhabit and never an
/// authority over it — so what a body may see has to be decided here, once, and provably not in
/// whichever transport happens to be asking.
///
/// The properties below are inherited rather than reimplemented, and that is the point: the gateway
/// calls the same retrieval the native conversation calls, so sensitivity, supersession, the excluded
/// raw-provider kinds and the relevance floor are enforced by construction. A gateway that ran its
/// own search would need its own copy of all of it, and the copies would drift — with the divergence
/// surfacing to an external client, which is the worst place to find it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarGatewayTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- identity

    [Fact]
    public async Task The_manifest_reports_the_configured_identity()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext, name: "Sakura", guidance: "Speak plainly.").GetManifest();

        Assert.Equal("Sakura", manifest.Name);
        Assert.Equal("Find Familiar", manifest.Kind);
        Assert.Equal("Speak plainly.", manifest.Guidance);
    }

    /// <summary>
    /// The manifest declares what an external client may actually reach: four reads and one write.
    ///
    /// This replaces an assertion that the write list was empty, which was true when the gateway was
    /// read-only and became false two slices later without anything noticing — a connected client was
    /// told three capabilities and no writes while five reads and one write were live. The list is an
    /// allowlist by design, so the failure mode is understatement, and these are the assertions that
    /// make understatement visible.
    /// </summary>
    [Fact]
    public async Task The_manifest_declares_the_read_capabilities_a_client_can_reach()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext).GetManifest();

        Assert.Equal(
            ["get_project_context", "list_familiar_projects", "open_decisions", "search_familiar_context"],
            manifest.Capabilities.Order());
    }

    /// <summary>
    /// The write list is no longer empty, and carries exactly the decision relay. A second entry here
    /// would mean a second way for an external client to change something, which is a decision nobody
    /// should be able to make by editing an array.
    /// </summary>
    [Fact]
    public async Task The_manifest_declares_the_single_write_capability()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext).GetManifest();

        Assert.Equal(["submit_familiar_decision"], manifest.WriteCapabilities);
    }

    /// <summary>
    /// The write is not smuggled in as a read. A client that trusted the read list to be read-only
    /// would be right to, and this keeps it right.
    /// </summary>
    [Fact]
    public async Task The_write_capability_is_not_declared_as_a_read()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext).GetManifest();

        Assert.DoesNotContain("submit_familiar_decision", manifest.Capabilities);
        Assert.Empty(manifest.Capabilities.Intersect(manifest.WriteCapabilities));

        // No read capability is named for something that acts.
        Assert.All(manifest.Capabilities, capability =>
            Assert.DoesNotContain(
                capability,
                new[] { "create", "start", "approve", "submit", "write", "update", "delete" },
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nothing internal leaks out. The manifest is an allowlist, so the risk is not that it grows by
    /// itself — it cannot — but that somebody adds a name to it that no external client can call.
    /// </summary>
    [Fact]
    public async Task The_manifest_declares_nothing_a_client_cannot_call()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext).GetManifest();

        string[] reachable =
        [
            "search_familiar_context", "get_project_context", "list_familiar_projects",
            "open_decisions", "submit_familiar_decision"
        ];

        Assert.All(
            manifest.Capabilities.Concat(manifest.WriteCapabilities),
            capability => Assert.Contains(capability, reachable));
    }

    /// <summary>
    /// A deployment that has not chosen a name has not chosen one. Inventing a character here would
    /// put a persona in front of a person who never asked for one.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_identity_falls_back_to_the_common_noun()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext, name: "   ").GetManifest();

        Assert.Equal("Familiar", manifest.Name);
        Assert.Null(manifest.Guidance);
    }

    /// <summary>Guidance is a note on register, not a system prompt, and the bound is what keeps it one.</summary>
    [Fact]
    public async Task Identity_guidance_is_bounded()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var manifest = NewGateway(dbContext, guidance: new string('x', 5_000)).GetManifest();

        Assert.Equal(FamiliarIdentityOptions.MaxGuidanceLength, manifest.Guidance!.Length);
    }

    // ---------------------------------------------------------------- what a search returns

    [Fact]
    public async Task A_search_returns_recorded_context_with_citable_ids()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var entry = await SeedEntryAsync(
            dbContext, project.Id,
            "Retrieval relevance floor decision",
            "The retrieval relevance floor rejects the weak match and the narrow match.");

        var result = await NewGateway(dbContext)
            .SearchContextAsync("retrieval relevance floor");

        var item = Assert.Single(result.Items);

        Assert.Equal(entry.Id, item.ContextId);
        Assert.Equal(project.Id, item.ProjectId);
        Assert.Equal("Find Familiar", item.ProjectName);
        Assert.Equal("Decision", item.Category);
        Assert.False(result.FoundNothing);
    }

    /// <summary>
    /// An unrelated question gets an explicit nothing, with an instruction not to fill it.
    ///
    /// This is the property the whole gateway exists to carry across the network. A frontier model
    /// handed an empty list and no explanation answers from what it recalls about software projects
    /// in general, in the same confident register it uses for things it knows — the exact failure this
    /// system was built to prevent, now one hop further away where it is harder to notice.
    /// </summary>
    [Fact]
    public async Task An_unrelated_query_reports_nothing_rather_than_unrelated_rows()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project.Id, "Retrieval relevance floor", "About retrieval scoring.");

        var result = await NewGateway(dbContext)
            .SearchContextAsync("tarragon vinaigrette emulsification");

        Assert.Empty(result.Items);
        Assert.True(result.FoundNothing);
        Assert.Contains("Nothing is recorded", result.Disclosure, StringComparison.Ordinal);
        Assert.Contains("rather than answering from general knowledge", result.Disclosure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A near-miss is disclosed as a count and told apart from an empty store. The two license
    /// different answers, and the distinction is the reason the floor was worth adding at all.
    /// </summary>
    [Fact]
    public async Task A_near_miss_is_disclosed_as_a_count_and_not_as_content()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(
            dbContext, project.Id,
            "An entry about absolute paths",
            "Plans should not name absolute paths that sessions cannot reach.");

        // A multi-word question touching exactly one of the entry's words. This is the shape that
        // produced the original defect: a single term landing in a title, ranked top because ranking
        // always has a top, and narrated as though it answered the question.
        var result = await NewGateway(dbContext)
            .SearchContextAsync("kubernetes ingress certificate rotation paths");

        Assert.Empty(result.Items);
        Assert.True(result.NoMatchAboveFloor);
        Assert.True(result.BelowThreshold > 0);
        Assert.Contains("none was close enough", result.Disclosure, StringComparison.Ordinal);

        // The count travels; nothing about the near miss does.
        AssertNothingLeaked(result, "absolute paths", "sessions cannot reach");
    }

    // ---------------------------------------------------------------- what a search must never return

    /// <summary>A sensitive entry is withheld, counted, and never described.</summary>
    [Fact]
    public async Task A_sensitive_entry_is_withheld_and_only_counted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(
            dbContext, project.Id,
            "A distinctive withheld decision",
            "Sensitive body text about the retrieval relevance floor.",
            isSensitive: true);

        var result = await NewGateway(dbContext).SearchContextAsync("retrieval relevance floor");

        Assert.Empty(result.Items);
        Assert.Equal(1, result.SensitiveWithheld);
        AssertNothingLeaked(result, "distinctive withheld", "Sensitive body text");
    }

    /// <summary>A sensitive project takes its entries with it, however unremarkable they are.</summary>
    [Fact]
    public async Task A_sensitive_projects_context_is_never_exposed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, name: "A distinctive private project", isSensitive: true);
        await SeedEntryAsync(
            dbContext, project.Id,
            "A decision inside a private project",
            "Body text about the retrieval relevance floor.");

        var gateway = NewGateway(dbContext);
        var result = await gateway.SearchContextAsync("retrieval relevance floor");

        Assert.Empty(result.Items);
        AssertNothingLeaked(result, "distinctive private", "decision inside a private project");

        // And it is absent from the list and from a direct lookup, not merely absent from search.
        var list = await gateway.ListProjectsAsync();
        Assert.DoesNotContain(list.Projects, summary => summary.ProjectId == project.Id);
        Assert.Null(await gateway.GetProjectContextAsync(project.Id));
    }

    /// <summary>
    /// A superseded entry is history, not context. It stays in the store because the record of what
    /// was once believed is worth keeping; it does not travel, because a model handed both the old
    /// and the new has no way to tell which one it is meant to believe.
    /// </summary>
    [Fact]
    public async Task Superseded_context_is_not_exposed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(
            dbContext, project.Id,
            "A superseded decision about the retrieval relevance floor",
            "This was replaced by a later decision.",
            state: ContextEntryState.Superseded);

        var result = await NewGateway(dbContext).SearchContextAsync("retrieval relevance floor");

        Assert.Empty(result.Items);
        AssertNothingLeaked(result, "superseded decision", "replaced by a later");
    }

    /// <summary>
    /// The verbatim input and output of a previous agent run never travel.
    ///
    /// Not a privacy rule but a contamination one: feeding a session transcript back to a model
    /// teaches it to imitate the transcript — to write in the voice of a worker, or to treat an
    /// instruction addressed to a Planner as one addressed to itself. The native retrieval path
    /// excludes these kinds, so the gateway does too, without a second rule to keep in step.
    /// </summary>
    [Theory]
    [InlineData(ContextEntryKind.Prompt)]
    [InlineData(ContextEntryKind.RawOutput)]
    public async Task Raw_provider_prompts_and_output_are_not_exposed(ContextEntryKind kind)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(
            dbContext, project.Id,
            "A distinctive raw record about the retrieval relevance floor",
            "You are an Implementer. Begin by reading the retrieval relevance floor.",
            kind: kind);

        var result = await NewGateway(dbContext).SearchContextAsync("retrieval relevance floor");

        Assert.Empty(result.Items);
        AssertNothingLeaked(result, "distinctive raw record", "You are an Implementer");
    }

    /// <summary>
    /// One project's private state does not arrive on another project's scoped request.
    ///
    /// Cross-project search exists and is deliberate — "what should I work on?" is the question this
    /// system is for — but a request that named a project must answer about that project.
    /// </summary>
    [Fact]
    public async Task A_scoped_request_does_not_return_another_projects_context()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var wanted = await SeedProjectAsync(dbContext, name: "Wanted project");
        var other = await SeedProjectAsync(dbContext, name: "Other project");

        await SeedEntryAsync(dbContext, wanted.Id, "Wanted retrieval relevance floor note", "In scope.");
        await SeedEntryAsync(dbContext, other.Id, "Other retrieval relevance floor note", "Out of scope.");

        var result = await NewGateway(dbContext)
            .SearchContextAsync("retrieval relevance floor", projectId: wanted.Id);

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item => Assert.Equal(wanted.Id, item.ProjectId));
        AssertNothingLeaked(result, "Out of scope");
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public async Task A_caller_cannot_ask_for_more_items_than_the_ceiling()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        for (var index = 0; index < 20; index++)
        {
            await SeedEntryAsync(
                dbContext, project.Id,
                $"Retrieval relevance floor note {index}",
                "About the retrieval relevance floor and how it scores.");
        }

        var result = await NewGateway(dbContext)
            .SearchContextAsync("retrieval relevance floor", maxItems: 10_000);

        Assert.True(result.Items.Count <= FamiliarGateway.MaxItems);
    }

    /// <summary>
    /// A frontier model that guesses a parameter wrong should get a smaller answer, not a failed
    /// turn. Nothing here throws on a bad number.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    public async Task A_nonsensical_item_count_is_clamped_rather_than_rejected(int requested)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project.Id, "Retrieval relevance floor", "About the floor.");

        var result = await NewGateway(dbContext)
            .SearchContextAsync("retrieval relevance floor", maxItems: requested);

        Assert.InRange(result.Items.Count, 0, FamiliarGateway.MaxItems);
    }

    /// <summary>An over-long query is truncated to something answerable, not refused.</summary>
    [Fact]
    public async Task An_oversized_query_is_bounded_rather_than_fatal()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext);

        var result = await NewGateway(dbContext).SearchContextAsync(new string('q', 50_000));

        Assert.True(result.Query.Length <= FamiliarGateway.MaxQueryLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_query_searches_nothing_and_says_so(string query)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project.Id, "Retrieval relevance floor", "About the floor.");

        var result = await NewGateway(dbContext).SearchContextAsync(query);

        Assert.Empty(result.Items);
        Assert.Contains("No query was supplied", result.Disclosure, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- project context

    [Fact]
    public async Task A_project_snapshot_reuses_the_briefs_definition_of_project_truth()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedTaskAsync(dbContext, project.Id, "A ready task");

        var context = await NewGateway(dbContext).GetProjectContextAsync(project.Id);

        Assert.NotNull(context);
        Assert.Equal(project.Id, context!.ProjectId);
        Assert.Equal("Find Familiar", context.Name);
        Assert.Equal(1, context.TotalTasks);
        Assert.Contains(context.Tasks, task => task.Title == "A ready task");

        // The edge of what is known travels with it, so an external body describes what the records
        // show and when they end rather than asserting the present.
        Assert.NotEmpty(context.Limitations);
    }

    [Fact]
    public async Task A_project_that_does_not_exist_answers_like_one_that_may_not_be_read()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        Assert.Null(await NewGateway(dbContext).GetProjectContextAsync(Guid.NewGuid()));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Nothing about a withheld record may appear anywhere in what travels — not in an item, not in
    /// the project name, not in the disclosure sentence. Asserted over the whole serialised result
    /// rather than field by field, because the leak worth catching is the one through a field nobody
    /// thought to check.
    /// </summary>
    private static void AssertNothingLeaked(FamiliarContextResult result, params string[] fragments)
    {
        var serialised = System.Text.Json.JsonSerializer.Serialize(result);

        foreach (var fragment in fragments)
        {
            Assert.DoesNotContain(fragment, serialised, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The real retrieval and the real brief, never stand-ins.
    ///
    /// The gateway's entire claim is that it inherits the native path's rules rather than restating
    /// them, so a faked retrieval would let this suite pass while proving nothing: the sensitivity,
    /// supersession and excluded-kind assertions above are assertions about that inheritance, and
    /// they are only worth anything against the code that actually enforces it.
    /// </summary>
    private static FamiliarGateway NewGateway(
        FamiliarDbContext dbContext,
        string name = "Familiar",
        string? guidance = null)
    {
        var clock = new TestTimeProvider(new DateTimeOffset(Now));

        // One projection service, shared by the brief and the gateway. The real host wires it the same
        // way, and sharing it here is the point: both must be reading the same classification of task
        // state, because the gateway's whole claim is that it does not hold a second opinion.
        var projections = new DemiplaneProjectionService(dbContext, new NoProviderCapacityService(), clock);

        return new FamiliarGateway(
            new FamiliarContextRetrievalService(dbContext, Options.Create(new FamiliarRetrievalOptions())),
            new FamiliarStandingBriefService(dbContext, projections, clock),
            projections,
            Options.Create(new FamiliarIdentityOptions { Name = name, Guidance = guidance }));
    }

    /// <summary>Provider readiness is not part of any gateway answer, so it contributes nothing here.</summary>
    private sealed class NoProviderCapacityService : IProviderCapacityService
    {
        public Task<IReadOnlyList<ProviderCapacitySnapshot>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderCapacitySnapshot>>([]);
    }

    private static async Task<FamiliarProject> SeedProjectAsync(
        FamiliarDbContext dbContext,
        string name = "Find Familiar",
        bool isSensitive = false)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = "Seeded for FamiliarGatewayTests.",
            Status = ProjectStatus.Active,
            IsSensitive = isSensitive,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private static async Task<ContextEntry> SeedEntryAsync(
        FamiliarDbContext dbContext,
        Guid projectId,
        string title,
        string content,
        ContextEntryKind kind = ContextEntryKind.Decision,
        ContextEntryState state = ContextEntryState.Active,
        bool isSensitive = false)
    {
        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = kind,
            Title = title,
            Content = content,
            State = state,
            IsSensitive = isSensitive,
            CreatedUtc = Now
        };

        dbContext.ContextEntries.Add(entry);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return entry;
    }

    private static async Task SeedTaskAsync(FamiliarDbContext dbContext, Guid projectId, string title)
    {
        dbContext.Tasks.Add(new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            RequestedOutcome = "Seeded for FamiliarGatewayTests.",
            Status = FindFamiliar.Server.Domain.TaskStatus.Ready,
            CreatedUtc = Now,
            UpdatedUtc = Now
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }
}
