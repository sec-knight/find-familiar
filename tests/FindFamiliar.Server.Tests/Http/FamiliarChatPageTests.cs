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
