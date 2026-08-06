using System.Net;
using System.Reflection;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Pages;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The Familiar page through the real HTTP pipeline, while it is still read-only.
///
/// Two properties are load-bearing here and are asserted rather than assumed. The first is that a
/// <c>GET</c> is a read: opening a project must not create a conversation, touch a timestamp or move
/// a revision. The second is that provider-authored text is inert — a URL a model wrote renders as
/// characters, and markup it wrote renders as characters, because nothing on this page interprets
/// stored content.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarPageTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task An_existing_project_renders()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/Familiar/{project.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(project.Name, html, StringComparison.Ordinal);
        Assert.Contains(project.Purpose, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_project_is_not_found()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Familiar/{Guid.NewGuid()}")).StatusCode);
    }

    /// <summary>The route's guid constraint refuses a malformed id before any handler runs.</summary>
    [Fact]
    public async Task A_malformed_project_id_is_not_found()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Familiar/not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Familiar/12345")).StatusCode);
    }

    /// <summary>
    /// Looking at a project does not change it. Counts are scoped to this project so a test running
    /// in another collection cannot make this pass or fail for the wrong reason.
    /// </summary>
    [Fact]
    public async Task A_page_load_writes_nothing()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Untouched {Guid.NewGuid():N}");
        await SeedConversationAsync(project.Id, task.Id);

        var before = await CaptureAsync(project.Id);

        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Familiar/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Familiar/{project.Id}")).StatusCode);

        Assert.Equal(before, await CaptureAsync(project.Id));
    }

    /// <summary>
    /// A project nobody has spoken to keeps no conversation row. Creating one lazily on a read would
    /// be tidier and would make a read a write.
    /// </summary>
    [Fact]
    public async Task A_page_load_creates_no_conversation()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains("Nothing asked yet", html, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.FamiliarConversations
            .AsNoTracking()
            .Where(conversation => conversation.ProjectId == project.Id)
            .ToListAsync());
    }

    /// <summary>
    /// The floor of this page: with no reasoning provider registered anywhere in the application, the
    /// deterministic account still renders and still describes the project.
    /// </summary>
    [Fact]
    public async Task The_deterministic_summary_renders_with_no_provider_configured()
    {
        var project = await SeedProjectAsync();
        await SeedTaskAsync(project.Id, $"Summarised {Guid.NewGuid():N}");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains("What is recorded", html, StringComparison.Ordinal);
        Assert.Contains("Project status Active", html, StringComparison.Ordinal);
        Assert.Contains("1 task recorded", html, StringComparison.Ordinal);
        Assert.Contains("Nothing is running and nothing is waiting for a worker.", html, StringComparison.Ordinal);
    }

    /// <summary>Limitations are rendered inline, not behind a disclosure control.</summary>
    [Fact]
    public async Task Snapshot_limitations_are_always_visible()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains("What I can't see", html, StringComparison.Ordinal);
        Assert.Contains("Worker capabilities are self-reported", html, StringComparison.Ordinal);

        // <details>/<summary> would hide a limitation behind a click, which is how limitations stop
        // being read at all.
        Assert.DoesNotContain("<details", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Project_and_demiplane_links_are_present()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains($"/Projects/Details/{project.Id}", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/Demiplane/{project.Id}", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_demiplane_links_back_to_the_familiar()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        Assert.Contains($"/Familiar/{project.Id}", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Order comes from <c>Sequence</c>, never from a timestamp. These rows are seeded with the
    /// newest sequence carrying the oldest instant, so a page that sorted on time would fail here.
    /// </summary>
    [Fact]
    public async Task Conversation_messages_render_oldest_first()
    {
        var project = await SeedProjectAsync();
        var marker = Guid.NewGuid().ToString("N");

        await SeedConversationAsync(
            project.Id,
            targetTaskId: null,
            messages:
            [
                (1, FamiliarMessageAuthor.Human, $"first {marker}", DateTime.UtcNow),
                (2, FamiliarMessageAuthor.Familiar, $"second {marker}", DateTime.UtcNow.AddMinutes(-10)),
                (3, FamiliarMessageAuthor.System, $"third {marker}", DateTime.UtcNow.AddMinutes(-20))
            ]);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        var first = html.IndexOf($"first {marker}", StringComparison.Ordinal);
        var second = html.IndexOf($"second {marker}", StringComparison.Ordinal);
        var third = html.IndexOf($"third {marker}", StringComparison.Ordinal);

        Assert.True(first > 0 && second > first && third > second, "Messages must render in Sequence order.");
    }

    [Fact]
    public async Task A_familiar_message_shows_its_provider_and_model()
    {
        var project = await SeedProjectAsync();
        var marker = Guid.NewGuid().ToString("N");

        await SeedConversationAsync(
            project.Id,
            targetTaskId: null,
            messages: [(1, FamiliarMessageAuthor.Familiar, $"attributed {marker}", DateTime.UtcNow)],
            providerName: "Claude",
            providerModel: "claude-opus-5");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains("Claude", html, StringComparison.Ordinal);
        Assert.Contains("claude-opus-5", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property this feature would most quietly lose: a URL a model wrote must be characters, and
    /// markup it wrote must be characters. Nothing on this page renders stored content as HTML.
    /// </summary>
    [Fact]
    public async Task A_model_authored_url_is_not_a_link_and_markup_is_not_markup()
    {
        var project = await SeedProjectAsync();
        var hostile = "See https://example.invalid/steal and <script>alert('x')</script> and <a href=\"https://example.invalid/b\">click</a>";

        await SeedConversationAsync(
            project.Id,
            targetTaskId: null,
            messages: [(1, FamiliarMessageAuthor.Familiar, hostile, DateTime.UtcNow)],
            providerName: "Claude",
            providerModel: "claude-opus-5");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        // The characters are shown ...
        Assert.Contains("https://example.invalid/steal", html, StringComparison.Ordinal);

        // ... but never as an anchor, a script, or any other live element.
        Assert.DoesNotContain("href=\"https://example.invalid", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;a href=", html, StringComparison.Ordinal);

        // Line breaks come from CSS, not from injected markup.
        Assert.Contains("conversation-block", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A proposal must be impossible to mistake for a sentence: it renders in its own labelled
    /// region, outside the transcript, and this slice offers no control that would decide it.
    /// </summary>
    [Fact]
    public async Task A_pending_proposal_renders_outside_the_message_content()
    {
        var project = await SeedProjectAsync();
        var marker = Guid.NewGuid().ToString("N");

        await SeedConversationAsync(
            project.Id,
            targetTaskId: null,
            messages: [(1, FamiliarMessageAuthor.Familiar, $"spoken {marker}", DateTime.UtcNow)],
            providerName: "Claude",
            providerModel: "claude-opus-5",
            proposalTitle: $"Proposed title {marker}");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        var region = html.IndexOf("class=\"familiar-proposals\"", StringComparison.Ordinal);
        var title = html.IndexOf($"Proposed title {marker}", StringComparison.Ordinal);

        Assert.True(region > 0, "Pending proposals need their own region.");
        Assert.True(title > region, "The proposal must render inside its own region, not in the transcript.");
        Assert.Contains("aria-labelledby=\"proposals-title\"", html, StringComparison.Ordinal);
        Assert.Contains("nothing has happened yet", html, StringComparison.OrdinalIgnoreCase);

        // The transcript itself must not carry the proposal.
        var messagesStart = html.IndexOf("<ol class=\"familiar-messages\">", StringComparison.Ordinal);
        var messagesEnd = html.IndexOf("</ol>", messagesStart, StringComparison.Ordinal);
        var transcript = html[messagesStart..messagesEnd];
        Assert.DoesNotContain($"Proposed title {marker}", transcript, StringComparison.Ordinal);

        // The decision controls live in the proposal region, never in the transcript.
        var confirmAt = html.IndexOf("handler=Confirm", StringComparison.Ordinal);
        var dismissAt = html.IndexOf("handler=Dismiss", StringComparison.Ordinal);

        Assert.True(confirmAt > region, "Confirm must render inside the proposal region.");
        Assert.True(dismissAt > region, "Dismiss must render inside the proposal region.");
        Assert.DoesNotContain("handler=Confirm", transcript, StringComparison.Ordinal);

        // Real submit buttons in a form, not links: nothing state-changing is reachable by GET.
        Assert.Contains("<button type=\"submit\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly three writes exist on this page, and no more. A fourth handler is a fourth thing a
    /// conversation could drive, which is the surface this feature is deliberately keeping small.
    /// </summary>
    [Fact]
    public async Task The_page_has_exactly_three_post_handlers()
    {
        var handlers = typeof(FamiliarModel)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name.StartsWith("OnPost", StringComparison.Ordinal))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["OnPostConfirmAsync", "OnPostDismissAsync", "OnPostSendAsync"], handlers);

        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Token source {Guid.NewGuid():N}");

        // Without a token the framework's antiforgery filter refuses outright.
        using var raw = factory.CreateClient(new() { AllowAutoRedirect = false });
        var untokenised = await raw.PostAsync(
            $"/Familiar/{project.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["anything"] = "at all" }));
        Assert.Equal(HttpStatusCode.BadRequest, untokenised.StatusCode);

        // With a valid token the request reaches the page, which — having no handler to select —
        // simply renders. Razor Pages does not answer 405 for that, so the guarantee worth asserting
        // is not the status code but the effect: a POST to this page writes nothing at all.
        await SeedConversationAsync(project.Id, targetTaskId: null);
        var before = await CaptureAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        var tokenised = await afClient.PostFormAsync(
            $"/Familiar/{project.Id}",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [new("anything", "at all")]);

        Assert.Equal(HttpStatusCode.OK, tokenised.StatusCode);
        Assert.Equal(before, await CaptureAsync(project.Id));
    }

    /// <summary>
    /// No meta refresh, deliberately: re-requesting this URL would re-render a conversation somebody
    /// is part-way through reading, and there is no in-flight work here to poll for.
    /// </summary>
    [Fact]
    public async Task The_page_does_not_ask_the_browser_to_refresh()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_page_requires_no_javascript()
    {
        var project = await SeedProjectAsync();
        await SeedConversationAsync(project.Id, targetTaskId: null);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        var body = html[(html.IndexOf("</head>", StringComparison.Ordinal) + 7)..];

        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onsubmit", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Archiving hides nothing: the record stays readable and the reason is stated.</summary>
    [Fact]
    public async Task An_archived_project_renders_read_only_and_says_why()
    {
        var project = await SeedProjectAsync(ProjectStatus.Archived);
        await SeedTaskAsync(project.Id, $"Archived work {Guid.NewGuid():N}");

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/Familiar/{project.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The record stays fully readable: the account, its counts and its limitations all render.
        Assert.Contains("Project status Archived", html, StringComparison.Ordinal);
        Assert.Contains("1 task recorded", html, StringComparison.Ordinal);
        Assert.Contains("What I can't see", html, StringComparison.Ordinal);
        Assert.Contains("This project is archived", html, StringComparison.Ordinal);
        Assert.Contains("Project status <strong>Archived</strong>", html, StringComparison.Ordinal);

        // The input is disabled and the reason is stated, exactly as user-experience.md §5 requires.
        Assert.Contains("disabled", html, StringComparison.Ordinal);
        Assert.Contains("no work can be started from here", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A disabled control is a hint to a browser, not a boundary. A crafted post to an archived
    /// project must be refused by the server and write nothing.
    /// </summary>
    [Fact]
    public async Task An_archived_project_refuses_a_crafted_send()
    {
        var project = await SeedProjectAsync(ProjectStatus.Archived);
        var task = await SeedTaskAsync(project.Id, $"Archived {Guid.NewGuid():N}");

        var before = await CaptureAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, tokenPage) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        var response = await afClient.PostFormAsync(
            $"/Familiar/{project.Id}?handler=Send",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(tokenPage),
            [new("Message", "start something on this archived project")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "This project is archived",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(before, await CaptureAsync(project.Id));
    }

    /// <summary>
    /// The stock build: no credential anywhere, and sending still works. The message is saved and
    /// the page says the one thing that is true about why there is no reply.
    /// </summary>
    [Fact]
    public async Task Sending_works_with_no_provider_configured()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Sendable {Guid.NewGuid():N}");
        var marker = Guid.NewGuid().ToString("N");

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, tokenPage) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        var response = await afClient.PostFormAsync(
            $"/Familiar/{project.Id}?handler=Send",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(tokenPage),
            [new("Message", $"why is this blocked? {marker}")]);

        // Post/redirect/get, so a refresh does not send again.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains($"why is this blocked? {marker}", html, StringComparison.Ordinal);
        Assert.Contains(
            "No reasoning provider is configured, so I can only show you what is recorded.",
            html,
            StringComparison.Ordinal);

        // The deterministic account is intact beside the failure, which is the whole point.
        Assert.Contains("What is recorded", html, StringComparison.Ordinal);
        Assert.Contains("What I can't see", html, StringComparison.Ordinal);
    }

    /// <summary>The default registration is the honest one.</summary>
    [Fact]
    public void The_unconfigured_provider_is_the_default_registration()
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IFamiliarReasoningProvider>();

        Assert.IsType<UnconfiguredFamiliarReasoningProvider>(provider);
        Assert.Equal(UnconfiguredFamiliarReasoningProvider.ProviderName, provider.Provider);
    }

    [Fact]
    public async Task Sending_requires_an_antiforgery_token()
    {
        var project = await SeedProjectAsync();
        var before = await CaptureAsync(project.Id);

        using var raw = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await raw.PostAsync(
            $"/Familiar/{project.Id}?handler=Send",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Message"] = "unguarded" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CaptureAsync(project.Id));
    }

    [Fact]
    public async Task An_empty_send_is_refused_in_place_and_writes_nothing()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Empty {Guid.NewGuid():N}");
        var before = await CaptureAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, tokenPage) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        var response = await afClient.PostFormAsync(
            $"/Familiar/{project.Id}?handler=Send",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(tokenPage),
            [new("Message", "   ")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Type a message before sending.",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(before, await CaptureAsync(project.Id));
    }

    /// <summary>Sending to a project that does not exist is a 404, not a new conversation.</summary>
    [Fact]
    public async Task Sending_to_an_unknown_project_is_not_found()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Token {Guid.NewGuid():N}");

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, tokenPage) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        var response = await afClient.PostFormAsync(
            $"/Familiar/{Guid.NewGuid()}?handler=Send",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(tokenPage),
            [new("Message", "hello")]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Confirm and Dismiss are guarded exactly as Send is, and change nothing without a token.</summary>
    [Theory]
    [InlineData("Confirm")]
    [InlineData("Dismiss")]
    public async Task Deciding_a_proposal_requires_an_antiforgery_token(string handler)
    {
        var project = await SeedProjectAsync();
        var proposal = await SeedPendingProposalAsync(project.Id);
        var before = await CaptureAsync(project.Id);

        using var raw = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await raw.PostAsync(
            $"/Familiar/{project.Id}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Decision.ProposalId"] = proposal.Id.ToString(),
                ["Decision.ExpectedConcurrencyToken"] = proposal.ConcurrencyToken.ToString()
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CaptureAsync(project.Id));
    }

    /// <summary>
    /// Confirming through the real page creates exactly one task, and the proposal carries a durable
    /// link to it.
    /// </summary>
    [Fact]
    public async Task Confirming_from_the_page_creates_exactly_one_task()
    {
        var project = await SeedProjectAsync();
        var proposal = await SeedPendingProposalAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Familiar/{project.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Decision.ProposalId", proposal.Id.ToString()),
            new("Decision.ExpectedConcurrencyToken", proposal.ConcurrencyToken.ToString()),
            new("Decision.Title", "A confirmed task"),
            new("Decision.RequestedOutcome", "A confirmed outcome.")
        };

        var first = await afClient.PostFormAsync($"/Familiar/{project.Id}?handler=Confirm", token, fields);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        // A double submit — the classic double-click — must still produce one task.
        var replay = await afClient.PostFormAsync($"/Familiar/{project.Id}?handler=Confirm", token, fields);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var tasks = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.ProjectId == project.Id).ToListAsync();
        Assert.Single(tasks);
        Assert.Equal("A confirmed task", tasks[0].Title);

        var stored = await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(FamiliarActionStatus.Confirmed, stored.Status);
        Assert.Equal(tasks[0].Id, stored.CreatedTaskId);
    }

    /// <summary>
    /// The model-binding boundary. A crafted post naming another project's proposal, or trying to
    /// set the kind, the target task or a created id, changes nothing — those are read server-side.
    /// </summary>
    [Fact]
    public async Task A_crafted_decision_cannot_retarget_a_proposal()
    {
        var mine = await SeedProjectAsync();
        var theirs = await SeedProjectAsync();
        var theirProposal = await SeedPendingProposalAsync(theirs.Id);

        var before = await CaptureAsync(theirs.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Familiar/{mine.Id}");

        // Confirming another project's proposal from this project's page.
        var response = await afClient.PostFormAsync(
            $"/Familiar/{mine.Id}?handler=Confirm",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [
                new("Decision.ProposalId", theirProposal.Id.ToString()),
                new("Decision.ExpectedConcurrencyToken", theirProposal.ConcurrencyToken.ToString()),
                // Fields that are not bound at all — the page reads these from the row.
                new("Decision.Kind", "StartPlanner"),
                new("Decision.ProjectId", mine.Id.ToString()),
                new("Decision.TargetTaskId", Guid.NewGuid().ToString()),
                new("Decision.Status", "Confirmed"),
                new("Decision.CreatedTaskId", Guid.NewGuid().ToString())
            ]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await CaptureAsync(theirs.Id));

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var stored = await dbContext.FamiliarActionProposals.AsNoTracking()
            .SingleAsync(p => p.Id == theirProposal.Id);
        Assert.Equal(FamiliarActionStatus.Pending, stored.Status);
        Assert.Equal(FamiliarActionKind.CreateTask, stored.Kind);
        Assert.Equal(theirs.Id, stored.ProjectId);
        Assert.Null(stored.TargetTaskId);
    }

    [Fact]
    public async Task Dismissing_from_the_page_creates_nothing()
    {
        var project = await SeedProjectAsync();
        var proposal = await SeedPendingProposalAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Familiar/{project.Id}");

        var response = await afClient.PostFormAsync(
            $"/Familiar/{project.Id}?handler=Dismiss",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [
                new("Decision.ProposalId", proposal.Id.ToString()),
                new("Decision.ExpectedConcurrencyToken", proposal.ConcurrencyToken.ToString())
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
        Assert.Equal(
            FamiliarActionStatus.Dismissed,
            (await dbContext.FamiliarActionProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id)).Status);
    }

    /// <summary>
    /// Staleness is derived at render time: a CreateTask proposal whose project moved shows the
    /// reason instead of a Confirm button, and Dismiss is still offered.
    /// </summary>
    [Fact]
    public async Task A_stale_proposal_shows_the_reason_instead_of_confirm()
    {
        var project = await SeedProjectAsync();
        await SeedPendingProposalAsync(project.Id);

        // Anything at all moves the project's revision.
        await SeedTaskAsync(project.Id, $"Mover {Guid.NewGuid():N}");
        await BumpRevisionAsync(project.Id);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/{project.Id}");

        Assert.Contains("The project&#x27;s context changed after this was proposed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Confirm", html, StringComparison.Ordinal);
        Assert.Contains("handler=Dismiss", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Everything about this project that a read must leave exactly as it found it.</summary>
    private async Task<string> CaptureAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = await dbContext.Projects.AsNoTracking().SingleAsync(candidate => candidate.Id == projectId);

        var conversationIds = await dbContext.FamiliarConversations
            .AsNoTracking()
            .Where(conversation => conversation.ProjectId == projectId)
            .Select(conversation => conversation.Id)
            .ToListAsync();

        var taskIds = await dbContext.Tasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId)
            .Select(task => task.Id)
            .ToListAsync();

        var counts = new[]
        {
            $"revision={project.ContextRevision}",
            $"updated={project.UpdatedUtc:O}",
            $"status={project.Status}",
            $"tasks={taskIds.Count}",
            $"sessions={await dbContext.AgentSessions.AsNoTracking().CountAsync(s => taskIds.Contains(s.TaskId))}",
            $"entries={await dbContext.ContextEntries.AsNoTracking().CountAsync(e => e.ProjectId == projectId)}",
            $"conversations={conversationIds.Count}",
            $"messages={await dbContext.FamiliarMessages.AsNoTracking().CountAsync(m => conversationIds.Contains(m.ConversationId))}",
            $"proposals={await dbContext.FamiliarActionProposals.AsNoTracking().CountAsync(p => p.ProjectId == projectId)}"
        };

        return string.Join("; ", counts);
    }

    /// <summary>
    /// Seeds a conversation carrying one Pending CreateTask proposal, observed at the project's
    /// current revision so it is confirmable until something moves the project.
    /// </summary>
    private async Task<FamiliarActionProposal> SeedPendingProposalAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var now = DateTime.UtcNow;

        var conversation = new FamiliarConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var message = new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = FamiliarMessageAuthor.Familiar,
            Sequence = 1,
            Content = "I could create a task for that.",
            CreatedUtc = now,
            ProviderName = "Fake",
            ProviderModel = "fake-model-1",
            Delivery = FamiliarMessageDelivery.Delivered
        };

        var revision = await dbContext.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.ContextRevision)
            .SingleAsync();

        var proposal = new FamiliarActionProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            ProjectId = projectId,
            MessageId = message.Id,
            Kind = FamiliarActionKind.CreateTask,
            Status = FamiliarActionStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = revision,
            Title = "A proposed task",
            RequestedOutcome = "A proposed outcome.",
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.AddRange(conversation, message, proposal);
        await dbContext.SaveChangesAsync();
        return proposal;
    }

    /// <summary>Advances the project's context revision, as any real mutation would.</summary>
    private async Task BumpRevisionAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = await dbContext.Projects.SingleAsync(candidate => candidate.Id == projectId);
        project.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
    }

    private async Task<FamiliarProject> SeedProjectAsync(ProjectStatus status = ProjectStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Familiar page project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarPageTests.",
            Status = status,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    private async Task<FamiliarTask> SeedTaskAsync(Guid projectId, string title)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            RequestedOutcome = "Seeded for FamiliarPageTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }

    /// <summary>
    /// Seeds a conversation directly, because this slice has no code that writes one. The rows are
    /// exactly the shape Slice 2's schema defines, so the page is exercised against real data.
    /// </summary>
    private async Task SeedConversationAsync(
        Guid projectId,
        Guid? targetTaskId,
        IReadOnlyList<(int Sequence, FamiliarMessageAuthor Author, string Content, DateTime CreatedUtc)>? messages = null,
        string? providerName = null,
        string? providerModel = null,
        string? proposalTitle = null)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var now = DateTime.UtcNow;

        var conversation = new FamiliarConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.FamiliarConversations.Add(conversation);

        var seeded = messages ?? [(1, FamiliarMessageAuthor.Human, "Seeded question.", now)];
        var rows = new List<FamiliarMessage>();

        foreach (var (sequence, author, content, createdUtc) in seeded)
        {
            var message = new FamiliarMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Author = author,
                Sequence = sequence,
                Content = content,
                CreatedUtc = createdUtc,
                ProviderName = author == FamiliarMessageAuthor.Familiar ? providerName : null,
                ProviderModel = author == FamiliarMessageAuthor.Familiar ? providerModel : null,
                Delivery = FamiliarMessageDelivery.Delivered
            };

            rows.Add(message);
            dbContext.FamiliarMessages.Add(message);
        }

        if (proposalTitle is not null)
        {
            dbContext.FamiliarActionProposals.Add(new FamiliarActionProposal
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                ProjectId = projectId,
                MessageId = rows[^1].Id,
                Kind = targetTaskId is null ? FamiliarActionKind.CreateTask : FamiliarActionKind.StartPlanner,
                Status = FamiliarActionStatus.Pending,
                ConcurrencyToken = Guid.NewGuid(),
                ObservedContextRevision = 0,
                Title = targetTaskId is null ? proposalTitle : null,
                RequestedOutcome = targetTaskId is null ? "Seeded requested outcome." : null,
                TargetTaskId = targetTaskId,
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private Task SeedConversationAsync(Guid projectId, Guid targetTaskId) =>
        SeedConversationAsync(projectId, (Guid?)targetTaskId);
}
