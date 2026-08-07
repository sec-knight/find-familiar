using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The system-wide Familiar through the real HTTP pipeline.
///
/// Slice 1's acceptance question is answered here and nowhere else: a conversation survives a reload
/// and appears identically to a second device. "A second device" is a second
/// <see cref="HttpClient"/> with its own cookie jar — it shares nothing with the first but the
/// server, which is the whole claim.
///
/// Two further properties are load-bearing. A <c>GET</c> is a read: opening <c>/Familiar</c> must not
/// create a conversation. And stored text is inert — markup a person or a provider wrote renders as
/// characters, because nothing on these pages interprets stored content.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatPageTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task The_conversation_list_renders_with_no_project_in_the_route()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/Familiar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Conversations", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The system-wide page and the Sprint 11 per-project page are different routes serving different
    /// aggregates, and both still work. The bare route is not a variant of the project one.
    /// </summary>
    [Fact]
    public async Task The_per_project_familiar_page_is_unaffected()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Familiar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Familiar/{project.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_conversation_is_not_found()
    {
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/Familiar/Chat/{Guid.NewGuid()}")).StatusCode);
    }

    /// <summary>Opening the list creates nothing. A page somebody only looked at leaves no row.</summary>
    [Fact]
    public async Task A_get_creates_no_conversation()
    {
        var before = await CountChatsAsync();

        using var client = factory.CreateClient();
        await client.GetAsync("/Familiar");
        await client.GetAsync("/Familiar");

        Assert.Equal(before, await CountChatsAsync());
    }

    // ---------------------------------------------------------------- the acceptance question

    /// <summary>
    /// Slice 1's acceptance criterion, end to end: start a conversation on one client, and read it
    /// back identically on another that has never seen it.
    /// </summary>
    [Fact]
    public async Task A_conversation_started_on_one_device_appears_on_another()
    {
        var chatId = await StartConversationAsync("what is blocked across everything?");

        // A second client with its own cookie jar: it shares nothing with the first but the server.
        using var secondDevice = factory.CreateClient();
        var response = await secondDevice.GetAsync($"/Familiar/Chat/{chatId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("what is blocked across everything?", html, StringComparison.Ordinal);

        // And it is in the list, which is read from the server on every render.
        var (_, listHtml) = await new AntiforgeryHttpClient(secondDevice).GetPageAsync("/Familiar");
        Assert.Contains(chatId.ToString(), listHtml, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reload renders the same conversation. Nothing that matters lives in the browser, so
    /// re-requesting the URL is a read of the same server state rather than a rebuild of local state.
    /// </summary>
    [Fact]
    public async Task A_reload_renders_the_same_conversation()
    {
        var chatId = await StartConversationAsync("does this survive a reload?");

        using var client = factory.CreateClient();
        var first = await client.GetStringAsync($"/Familiar/Chat/{chatId}");
        var second = await client.GetStringAsync($"/Familiar/Chat/{chatId}");

        Assert.Contains("does this survive a reload?", first, StringComparison.Ordinal);
        Assert.Contains("does this survive a reload?", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// A send commits and redirects. The reply is generated out of band, so the response returns
    /// without waiting for it — which is what lets the page be closed immediately after sending.
    /// </summary>
    [Fact]
    public async Task Starting_a_conversation_redirects_to_it()
    {
        using var raw = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var client = new AntiforgeryHttpClient(raw);
        var (_, html) = await client.GetPageAsync("/Familiar");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await client.PostFormAsync(
            "/Familiar?handler=Start",
            token,
            [new KeyValuePair<string, string>("Message", "a redirected question")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Familiar/Chat/", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_message_starts_no_conversation()
    {
        var before = await CountChatsAsync();

        using var raw = factory.CreateClient();
        var client = new AntiforgeryHttpClient(raw);
        var (_, html) = await client.GetPageAsync("/Familiar");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await client.PostFormAsync(
            "/Familiar?handler=Start",
            token,
            [new KeyValuePair<string, string>("Message", "   ")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Type a message before sending.",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(before, await CountChatsAsync());
    }

    /// <summary>
    /// Stored text is characters. No <c>Html.Raw</c>, no markdown rendering, no autolinking — so a
    /// script tag somebody typed renders as a script tag rather than running as one.
    /// </summary>
    [Fact]
    public async Task Stored_text_is_rendered_inert()
    {
        const string Markup = "<script>alert('x')</script>";

        var chatId = await StartConversationAsync(Markup);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/Chat/{chatId}");

        Assert.DoesNotContain(Markup, html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the resume endpoint

    /// <summary>
    /// The one endpoint every client resumes through. A cursor of zero and a cursor at the head take
    /// the same call and differ only in what comes back.
    /// </summary>
    [Fact]
    public async Task The_resume_endpoint_returns_everything_after_a_cursor()
    {
        var chatId = await StartConversationAsync("the only question so far");

        using var client = factory.CreateClient();

        var fromStart = await client.GetFromJsonAsync<JsonElement>(
            $"/api/familiar/chats/{chatId}/turns?after=0");

        Assert.Equal(1, fromStart.GetProperty("turns").GetArrayLength());
        Assert.Equal(1, fromStart.GetProperty("latestSequence").GetInt32());
        Assert.Equal(
            "the only question so far",
            fromStart.GetProperty("turns")[0].GetProperty("userText").GetString());

        var caughtUp = await client.GetFromJsonAsync<JsonElement>(
            $"/api/familiar/chats/{chatId}/turns?after=1");

        // No turns, and the true head reported anyway — a caught-up client must not be handed a
        // cursor it would then sit on forever.
        Assert.Equal(0, caughtUp.GetProperty("turns").GetArrayLength());
        Assert.Equal(1, caughtUp.GetProperty("latestSequence").GetInt32());
    }

    /// <summary>No cursor means from the beginning. It is the same call, not a different one.</summary>
    [Fact]
    public async Task The_resume_endpoint_defaults_to_the_beginning()
    {
        var chatId = await StartConversationAsync("a question with no cursor");

        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/familiar/chats/{chatId}/turns");

        Assert.Equal(0, page.GetProperty("afterSequence").GetInt32());
        Assert.Equal(1, page.GetProperty("turns").GetArrayLength());
    }

    [Fact]
    public async Task The_resume_endpoint_is_not_found_for_an_unknown_conversation()
    {
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/familiar/chats/{Guid.NewGuid()}/turns?after=0")).StatusCode);
    }

    [Fact]
    public async Task The_conversation_list_endpoint_is_server_side()
    {
        var chatId = await StartConversationAsync("a listed question");

        using var client = factory.CreateClient();
        var chats = await client.GetFromJsonAsync<List<JsonElement>>("/api/familiar/chats");

        Assert.NotNull(chats);
        Assert.Contains(chats, chat => chat.GetProperty("chatId").GetGuid() == chatId);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Starts a conversation through the service, and waits for its turn to settle.
    ///
    /// The wait matters: the registered generator is the unconfigured one, so every turn reaches a
    /// terminal state quickly, and a page asserted mid-generation would carry a meta refresh that
    /// makes the assertions time-dependent.
    /// </summary>
    private async Task<Guid> StartConversationAsync(string message)
    {
        using var scope = factory.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<IFamiliarChatService>();

        var result = await chats.SendAsync(null, message);
        Assert.Equal(FamiliarChatSendStatus.Accepted, result.Status);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var page = await chats.ReadTurnsAfterAsync(result.ChatId, 0);

            if (page is { HasTurnInFlight: false })
            {
                return result.ChatId;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Conversation {result.ChatId} did not settle.");
    }

    private async Task<int> CountChatsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await dbContext.FamiliarChats.AsNoTracking().CountAsync();
    }

    // ---------------------------------------------------------------- citations on the page

    /// <summary>
    /// A cited id renders as a readable, tappable chip rather than as a raw UUID mid-sentence, and it
    /// links to the project the entry belongs to. A source nobody can follow is not a source.
    /// </summary>
    [Fact]
    public async Task A_supported_citation_renders_as_a_chip_and_not_as_a_raw_id()
    {
        var project = await SeedProjectAsync();
        var entryId = await SeedEntryAsync(project.Id, "Two provider seams (ADR-0013)");
        var chatId = await StartConversationAsync("why are the lanes separate?");

        await AnswerWithCitationAsync(chatId, entryId, offered: true);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/Chat/{chatId}");

        Assert.Contains("familiar-citation", html, StringComparison.Ordinal);
        Assert.Contains("Two provider seams (ADR-0013)", html, StringComparison.Ordinal);
        Assert.Contains($"/Demiplane/{project.Id}", html, StringComparison.OrdinalIgnoreCase);

        // The id itself is gone from the prose: the chip replaced it, rather than being added beside it.
        Assert.DoesNotContain(entryId.ToString(), html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The check that makes citations worth anything. An id the answer was never given renders as
    /// unsupported — visible, and visibly not a source.
    /// </summary>
    [Fact]
    public async Task An_unsupported_citation_is_marked_on_the_page()
    {
        var chatId = await StartConversationAsync("why are the lanes separate?");

        await AnswerWithCitationAsync(chatId, Guid.NewGuid(), offered: false);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/Chat/{chatId}");

        Assert.Contains("is-unsupported", html, StringComparison.Ordinal);
        Assert.Contains("unsupported reference", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- plans on the page

    /// <summary>
    /// A drafted plan renders inline in the transcript, is durable, and reads identically on a second
    /// device with its own cookie jar — the acceptance criterion for slice 3.
    ///
    /// It also states what approving would do before there is anything to press. That disclosure is
    /// what makes an approval an act of reading rather than of trust, so it is asserted now rather
    /// than added beside the button later.
    /// </summary>
    [Fact]
    public async Task A_drafted_plan_renders_in_the_transcript_and_on_a_second_device()
    {
        var project = await SeedProjectAsync();
        var chatId = await StartConversationAsync("plan the next sprint");

        await DraftPlanAsync(chatId, project.Id);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Familiar/Chat/{chatId}");

        Assert.Contains("Drafted plan", html, StringComparison.Ordinal);
        Assert.Contains("Re-specify the anchor task", html, StringComparison.Ordinal);
        Assert.Contains("starts a Planner session", html, StringComparison.Ordinal);
        Assert.Contains("records the work, starts nothing", html, StringComparison.Ordinal);

        // The disclosure: how many tasks, and that exactly one session starts (ADR-0014 §4).
        Assert.Contains("would create 2 task(s)", html, StringComparison.Ordinal);
        Assert.Contains("start one Planner session", html, StringComparison.Ordinal);
        Assert.Contains("Nothing has been created yet", html, StringComparison.Ordinal);

        // A second device, sharing nothing with the first but the server.
        using var secondDevice = factory.CreateClient();
        var onPhone = await secondDevice.GetStringAsync($"/Familiar/Chat/{chatId}");

        Assert.Contains("Re-specify the anchor task", onPhone, StringComparison.Ordinal);
        Assert.Contains("would create 2 task(s)", onPhone, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan travels on the turn in the JSON read too, so a client that resumed over the stream
    /// builds the same card rather than having to reload to see it.
    /// </summary>
    [Fact]
    public async Task A_drafted_plan_travels_with_the_turn_over_the_resume_read()
    {
        var project = await SeedProjectAsync();
        var chatId = await StartConversationAsync("plan the next sprint");

        await DraftPlanAsync(chatId, project.Id);

        using var client = factory.CreateClient();
        var json = await client.GetStringAsync($"/api/familiar/chats/{chatId}/turns?after=0");

        using var document = JsonDocument.Parse(json);
        var plan = document.RootElement.GetProperty("turns")[0].GetProperty("plan");

        Assert.Equal("Pending", plan.GetProperty("status").GetString());
        Assert.Equal(2, plan.GetProperty("items").GetArrayLength());
        Assert.Equal("Planner", plan.GetProperty("items")[0].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("items")[1].GetProperty("role").ValueKind);
    }

    /// <summary>
    /// A plan proposes and creates nothing. Asserted from outside the service that wrote it, because
    /// this is the property a reader of the page is being promised.
    /// </summary>
    [Fact]
    public async Task A_drafted_plan_has_created_no_task()
    {
        var project = await SeedProjectAsync();
        var chatId = await StartConversationAsync("plan the next sprint");

        await DraftPlanAsync(chatId, project.Id);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(task => task.ProjectId == project.Id).ToListAsync());
        Assert.All(
            await dbContext.FamiliarPlanItems.AsNoTracking().ToListAsync(),
            item => Assert.Null(item.CreatedTaskId));
    }

    /// <summary>
    /// The sprint's acceptance criterion, end to end through HTTP: a plan is approved in the
    /// conversation, the tasks appear, one session starts, and no task page is opened at any point.
    /// </summary>
    [Fact]
    public async Task Approving_a_plan_in_the_conversation_creates_the_work()
    {
        var project = await SeedProjectAsync();
        var chatId = await StartConversationAsync("plan the next sprint");
        var planId = await DraftPlanAsync(chatId, project.Id);

        // Redirects are not followed, so the post/redirect/get is observable rather than collapsed
        // into the page it lands on.
        var client = new AntiforgeryHttpClient(factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

        var (_, html) = await client.GetPageAsync($"/Familiar/Chat/{chatId}");

        var token = ReadPlanToken(html);

        var response = await client.PostFormAsync(
            $"/Familiar/Chat/{chatId}?handler=DecidePlan",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            new Dictionary<string, string>
            {
                ["PlanId"] = planId.ToString(),
                ["ExpectedConcurrencyToken"] = token,
                ["Approve"] = "true",
                ["Items[0].ItemId"] = await ItemIdAsync(planId, 0),
                ["Items[0].IsIncluded"] = "true",
                ["Items[0].Title"] = "Re-specify the anchor task",
                ["Items[0].RequestedOutcome"] = "The constraint reflects that the application now ships JavaScript.",
                ["Items[1].ItemId"] = await ItemIdAsync(planId, 1),
                ["Items[1].IsIncluded"] = "true",
                ["Items[1].Title"] = "Record the superseded boundary",
                ["Items[1].RequestedOutcome"] = "The no-JavaScript constraint is marked superseded."
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var tasks = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.ProjectId == project.Id)
            .ToListAsync();

        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, task => task.Title == "Re-specify the anchor task");

        // Exactly one session, on the first item that named a role.
        var session = Assert.Single(await dbContext.AgentSessions.AsNoTracking()
            .Where(candidate => tasks.Select(task => task.Id).Contains(candidate.TaskId))
            .ToListAsync());
        Assert.Equal(AgentSessionRole.Planner, session.Role);

        var plan = await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync(candidate => candidate.Id == planId);
        Assert.Equal(FamiliarPlanStatus.Approved, plan.Status);

        // And the transcript now reads as a receipt rather than a proposal.
        var after = await factory.CreateClient().GetStringAsync($"/Familiar/Chat/{chatId}");
        Assert.Contains("Approved. 2 task(s) were created", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve this plan", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declining in the conversation creates nothing, and the card says so rather than disappearing.
    /// </summary>
    [Fact]
    public async Task Declining_a_plan_in_the_conversation_creates_nothing()
    {
        var project = await SeedProjectAsync();
        var chatId = await StartConversationAsync("plan the next sprint");
        var planId = await DraftPlanAsync(chatId, project.Id);

        var client = new AntiforgeryHttpClient(factory.CreateClient());
        var (_, html) = await client.GetPageAsync($"/Familiar/Chat/{chatId}");

        await client.PostFormAsync(
            $"/Familiar/Chat/{chatId}?handler=DecidePlan",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            new Dictionary<string, string>
            {
                ["PlanId"] = planId.ToString(),
                ["ExpectedConcurrencyToken"] = ReadPlanToken(html),
                ["Approve"] = "false"
            });

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(task => task.ProjectId == project.Id).ToListAsync());
        Assert.Equal(
            FamiliarPlanStatus.Declined,
            (await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync(plan => plan.Id == planId)).Status);

        var after = await factory.CreateClient().GetStringAsync($"/Familiar/Chat/{chatId}");
        Assert.Contains("Declined. Nothing was created.", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token from a page loaded before someone else decided the plan creates nothing. The card is
    /// re-rendered with the reason rather than the person being redirected away from what they read.
    /// </summary>
    [Fact]
    public async Task A_stale_plan_token_creates_nothing_and_says_so()
    {
        var project = await SeedProjectAsync();
        var chatId = await StartConversationAsync("plan the next sprint");
        var planId = await DraftPlanAsync(chatId, project.Id);

        var client = new AntiforgeryHttpClient(factory.CreateClient());
        var (_, html) = await client.GetPageAsync($"/Familiar/Chat/{chatId}");

        var response = await client.PostFormAsync(
            $"/Familiar/Chat/{chatId}?handler=DecidePlan",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            new Dictionary<string, string>
            {
                ["PlanId"] = planId.ToString(),
                ["ExpectedConcurrencyToken"] = Guid.NewGuid().ToString(),
                ["Approve"] = "true"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "This plan changed since the page was loaded",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(task => task.ProjectId == project.Id).ToListAsync());
    }

    // ---------------------------------------------------------------- closing the loop

    /// <summary>
    /// Slice 5's acceptance criterion: a finished session's next step surfaces in the conversation,
    /// is decided there, and starts the session — with no task page opened.
    ///
    /// The panel renders on <i>any</i> conversation, not only the one that started the work, because a
    /// person should never have to remember which chat they began something in.
    /// </summary>
    [Fact]
    public async Task A_pending_handoff_surfaces_in_the_conversation_and_starts_the_next_session()
    {
        var project = await SeedProjectAsync();
        var (taskId, handoffId) = await SeedPendingHandoffAsync(project.Id);

        // A conversation with nothing to do with that task.
        var chatId = await StartConversationAsync("an unrelated question");

        var client = new AntiforgeryHttpClient(factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

        var (_, html) = await client.GetPageAsync($"/Familiar/Chat/{chatId}");

        Assert.Contains("Waiting for you", html, StringComparison.Ordinal);
        Assert.Contains("Start the Implementer session", html, StringComparison.Ordinal);

        // What finished, and what it produced — the decision is answerable from the card itself.
        var flattened = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ");
        Assert.Contains("The Planner session finished", flattened, StringComparison.Ordinal);
        Assert.Contains("Plan: three steps, smallest first", html, StringComparison.Ordinal);
        Assert.Contains("starts one Implementer session", html, StringComparison.Ordinal);

        var response = await client.PostFormAsync(
            $"/Familiar/Chat/{chatId}?handler=DecideHandoff",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            new Dictionary<string, string>
            {
                ["HandoffId"] = handoffId.ToString(),
                ["HandoffToken"] = ReadHandoffToken(html, handoffId),
                ["ApproveHandoff"] = "true"
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var started = Assert.Single(await dbContext.AgentSessions.AsNoTracking()
            .Where(session => session.TaskId == taskId && session.Status == AgentSessionStatus.Started)
            .ToListAsync());

        Assert.Equal(AgentSessionRole.Implementer, started.Role);

        // The Familiar never chooses a worker.
        Assert.Null(started.Provider);

        Assert.Equal(
            SessionHandoffStatus.Approved,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(handoff => handoff.Id == handoffId)).Status);

        // And this decision is no longer waiting on anybody. Asserted by id: the panel is
        // system-wide and these tests share a database, so other decisions legitimately remain.
        var after = await factory.CreateClient().GetStringAsync($"/Familiar/Chat/{chatId}");
        Assert.DoesNotContain(handoffId.ToString(), after, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Declining_a_handoff_in_the_conversation_starts_nothing()
    {
        var project = await SeedProjectAsync();
        var (taskId, handoffId) = await SeedPendingHandoffAsync(project.Id);
        var chatId = await StartConversationAsync("another question");

        var client = new AntiforgeryHttpClient(factory.CreateClient());
        var (_, html) = await client.GetPageAsync($"/Familiar/Chat/{chatId}");

        await client.PostFormAsync(
            $"/Familiar/Chat/{chatId}?handler=DecideHandoff",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            new Dictionary<string, string>
            {
                ["HandoffId"] = handoffId.ToString(),
                ["HandoffToken"] = ReadHandoffToken(html, handoffId),
                ["ApproveHandoff"] = "false"
            });


        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.AgentSessions.AsNoTracking()
            .Where(session => session.TaskId == taskId && session.Status == AgentSessionStatus.Started)
            .ToListAsync());

        Assert.Equal(
            SessionHandoffStatus.Declined,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(handoff => handoff.Id == handoffId)).Status);
    }

    /// <summary>
    /// A token from a page rendered before somebody else decided starts nothing. The same fence the
    /// task page presents, because it is the same transaction behind both doors.
    /// </summary>
    [Fact]
    public async Task A_stale_handoff_token_starts_nothing()
    {
        var project = await SeedProjectAsync();
        var (taskId, handoffId) = await SeedPendingHandoffAsync(project.Id);
        var chatId = await StartConversationAsync("a third question");

        var client = new AntiforgeryHttpClient(factory.CreateClient());
        var (_, html) = await client.GetPageAsync($"/Familiar/Chat/{chatId}");

        await client.PostFormAsync(
            $"/Familiar/Chat/{chatId}?handler=DecideHandoff",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            new Dictionary<string, string>
            {
                ["HandoffId"] = handoffId.ToString(),
                ["HandoffToken"] = Guid.NewGuid().ToString(),
                ["ApproveHandoff"] = "true"
            });

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.AgentSessions.AsNoTracking()
            .Where(session => session.TaskId == taskId && session.Status == AgentSessionStatus.Started)
            .ToListAsync());

        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(handoff => handoff.Id == handoffId)).Status);
    }

    /// <summary>
    /// The token belonging to one specific decision.
    ///
    /// Scoped to the handoff id rather than taking the first token on the page, because these tests
    /// share a database and the panel is system-wide: several decisions render at once, and grabbing
    /// the first would decide somebody else's.
    /// </summary>
    private static string ReadHandoffToken(string html, Guid handoffId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"HandoffId\" value=\"" + handoffId + "\"\\s*/>\\s*<input type=\"hidden\" name=\"HandoffToken\" value=\"([^\"]+)\"");

        Assert.True(match.Success, "The decision card for this handoff did not render a token.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// A finished Planner session with a recorded result and a pending handoff — the state the loop
    /// reaches on its own, staged directly so this test stays about the conversation.
    /// </summary>
    private async Task<(Guid TaskId, Guid HandoffId)> SeedPendingHandoffAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // The decision panel is system-wide and capped, and these tests share one database — so a
        // handoff left pending by another test can push this one off the list. Settling them first is
        // test hygiene, not a workaround: the panel is behaving exactly as designed.
        await dbContext.SessionHandoffs
            .Where(handoff => handoff.Status == SessionHandoffStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(handoff => handoff.Status, SessionHandoffStatus.Superseded));

        var now = DateTime.UtcNow;

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = "Re-specify the anchor task",
            RequestedOutcome = "The constraint reflects reality.",
            Status = FindFamiliar.Server.Domain.TaskStatus.InProgress,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Completed,
            ContextRevisionRead = 0,
            StartedUtc = now,
            CompletedUtc = now
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
            ObservedContextRevision = 0,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.Tasks.Add(task);
        dbContext.AgentSessions.Add(session);
        dbContext.SessionHandoffs.Add(handoff);
        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TaskId = task.Id,
            SourceSessionId = session.Id,
            Kind = ContextEntryKind.Plan,
            Title = "Plan: three steps, smallest first",
            Content = "What the Planner produced.",
            State = ContextEntryState.Active,
            CreatedUtc = now
        });

        await dbContext.SaveChangesAsync();

        return (task.Id, handoff.Id);
    }

    private static string ReadPlanToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"ExpectedConcurrencyToken\" value=\"([^\"]+)\"");

        Assert.True(match.Success, "The plan card did not render a concurrency token.");
        return match.Groups[1].Value;
    }

    private async Task<string> ItemIdAsync(Guid planId, int position)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (await dbContext.FamiliarPlanItems
                .AsNoTracking()
                .SingleAsync(item => item.PlanId == planId && item.Position == position))
            .Id.ToString();
    }

    /// <summary>
    /// Writes a plan directly, standing in for the drafting service so these tests stay about
    /// rendering and durability rather than about a provider.
    /// </summary>
    private async Task<Guid> DraftPlanAsync(Guid chatId, Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var turn = await dbContext.FamiliarChatTurns
            .Where(candidate => candidate.ChatId == chatId)
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstAsync();

        turn.State = FamiliarChatTurnState.Completed;
        turn.Output = "Here is what I would do next.";
        turn.RequestedPlan = true;
        turn.CompletedUtc = DateTime.UtcNow;

        var planId = Guid.NewGuid();

        dbContext.FamiliarPlanProposals.Add(new FamiliarPlanProposal
        {
            Id = planId,
            ChatId = chatId,
            TurnId = turn.Id,
            ProjectId = projectId,
            Status = FamiliarPlanStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = 0,
            Summary = "Close the anchor-navigation task.",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Items =
            [
                new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = 0,
                    Title = "Re-specify the anchor task",
                    RequestedOutcome = "The constraint reflects that the application now ships JavaScript.",
                    Role = AgentSessionRole.Planner,
                    IsIncluded = true
                },
                new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = 1,
                    Title = "Record the superseded boundary",
                    RequestedOutcome = "The no-JavaScript constraint is marked superseded.",
                    Role = null,
                    IsIncluded = true
                }
            ]
        });

        await dbContext.SaveChangesAsync();

        return planId;
    }

    private async Task<Guid> SeedEntryAsync(Guid projectId, string title)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = ContextEntryKind.Decision,
            Title = title,
            Content = "Conversation and agentic execution do not share a provider abstraction.",
            State = ContextEntryState.Active,
            CreatedUtc = DateTime.UtcNow
        };

        dbContext.ContextEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        return entry.Id;
    }

    /// <summary>
    /// Completes the conversation's in-flight turn with a reply that cites an id, standing in for the
    /// generation host so this test stays about rendering.
    /// </summary>
    private async Task AnswerWithCitationAsync(Guid chatId, Guid entryId, bool offered)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var turn = await dbContext.FamiliarChatTurns
            .Where(candidate => candidate.ChatId == chatId)
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstAsync();

        turn.State = FamiliarChatTurnState.Completed;
        turn.Output = $"They are different seams ({entryId}).";
        turn.EvidenceEntryIds = offered ? FamiliarChatCitations.SerialiseEvidence([entryId]) : null;
        turn.CompletedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
    }

    private async Task<FamiliarProject> SeedProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var now = DateTime.UtcNow;
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Chat page project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarChatPageTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        return project;
    }
}
