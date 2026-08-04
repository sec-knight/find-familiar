using System.Net;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using FindFamiliar.Server.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The Talk workflow through the real HTTP pipeline: real antiforgery, real Razor rendering, real
/// POST/redirect/GET. No JavaScript is involved anywhere, which is the point — the whole approval
/// boundary has to work in a plain browser.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TalkPageTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Primary_navigation_offers_the_talk_entry_point()
    {
        var afClient = NewClient();

        var (response, html) = await afClient.GetPageAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/Talk\"", html, StringComparison.Ordinal);
        Assert.Contains(">Talk</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intake_creates_a_conversation_and_redirects_to_a_stable_details_page()
    {
        var project = await SeedProjectAsync($"Talk Intake {Guid.NewGuid():N}");
        var afClient = NewClient();

        var (_, html) = await afClient.GetPageAsync("/Talk");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            token,
            [new("NewRequest.Request", $"Plan the next slice of {project.Name}\nKeep it small.")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/Talk/", location, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var proposal = await dbContext.WorkProposals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProjectId == project.Id);

        Assert.Equal($"Plan the next slice of {project.Name}", proposal.Title);
        Assert.Equal(AgentSessionRole.Planner, proposal.Role);
        Assert.Equal(WorkProposalStatus.Pending, proposal.Status);

        // The redirect target is stable and renders the same conversation.
        var (detailsResponse, detailsHtml) = await afClient.GetPageAsync(location);
        Assert.Equal(HttpStatusCode.OK, detailsResponse.StatusCode);
        Assert.Contains("Nothing has been created or started yet", detailsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blank_intake_is_rejected_and_creates_nothing()
    {
        var afClient = NewClient();
        var (_, html) = await afClient.GetPageAsync("/Talk");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var conversationsBefore = await CountConversationsAsync();

        var response = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            token,
            [new("NewRequest.Request", "   ")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(conversationsBefore, await CountConversationsAsync());
    }

    [Fact]
    public async Task Oversized_intake_is_rejected_and_creates_nothing()
    {
        var afClient = NewClient();
        var (_, html) = await afClient.GetPageAsync("/Talk");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var conversationsBefore = await CountConversationsAsync();

        var response = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            token,
            [new("NewRequest.Request", new string('a', 4_001))]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(conversationsBefore, await CountConversationsAsync());
    }

    [Fact]
    public async Task Intake_without_an_antiforgery_token_is_refused()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("NewRequest.Request", "This must never be accepted.")]);
        var response = await client.PostAsync("/Talk?handler=Start", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Approval_without_an_antiforgery_token_is_refused_and_creates_no_work()
    {
        var project = await SeedProjectAsync($"Talk Antiforgery {Guid.NewGuid():N}");
        var (conversationId, token) = await SeedConversationAsync(project.Id);

        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("ActionConcurrencyToken", token.ToString())]);
        var response = await client.PostAsync($"/Talk/Details/{conversationId}?handler=Approve", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(0, await dbContext.Tasks.CountAsync(task => task.ProjectId == project.Id));
    }

    /// <summary>
    /// Every mutation lives on an <c>OnPost*</c> handler, so a GET cannot reach one. Razor Pages
    /// ignores an unmatched handler name on GET and runs the read-only <c>OnGet</c> instead, which
    /// is exactly the safe outcome: the page renders and nothing changes.
    /// </summary>
    [Fact]
    public async Task Mutation_handlers_are_unreachable_by_GET()
    {
        var project = await SeedProjectAsync($"Talk GET {Guid.NewGuid():N}");
        var (conversationId, _) = await SeedConversationAsync(project.Id);

        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        foreach (var handler in new[] { "Approve", "Reject", "Revise", "RefreshContext" })
        {
            var response = await client.GetAsync($"/Talk/Details/{conversationId}?handler={handler}");

            // The read-only OnGet ran; no redirect and no error, because no mutation was attempted.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        // Scoped to this project: the collection-scoped host shares one database across tests.
        Assert.Equal(0, await dbContext.Tasks.CountAsync(task => task.ProjectId == project.Id));
        Assert.Equal(
            0,
            await dbContext.AgentSessions.CountAsync(session => session.Task.ProjectId == project.Id));

        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        var proposal = await dbContext.WorkProposals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ConversationId == conversationId);

        Assert.Equal(ConversationStatus.AwaitingApproval, conversation.Status);
        Assert.Equal(WorkProposalStatus.Pending, proposal.Status);
    }

    [Fact]
    public async Task Hostile_input_is_encoded_rather_than_rendered_as_markup()
    {
        var project = await SeedProjectAsync($"Talk XSS {Guid.NewGuid():N}");
        var afClient = NewClient();

        const string hostile = "<script>alert('xss')</script><img src=x onerror=alert(1)>";

        var (_, indexHtml) = await afClient.GetPageAsync("/Talk");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(indexHtml);

        var response = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            token,
            [new("NewRequest.Request", $"{hostile} for {project.Name}")]);

        var (_, detailsHtml) = await afClient.GetPageAsync(response.Headers.Location!.ToString());

        // The literal text is present, encoded; the executable form is not.
        Assert.Contains("&lt;script&gt;", detailsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert('xss')</script>", detailsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", detailsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_name_containing_markup_is_encoded_on_the_details_page()
    {
        var project = await SeedProjectAsync($"<b>Bold {Guid.NewGuid():N}</b>");
        var (conversationId, _) = await SeedConversationAsync(project.Id);

        var afClient = NewClient();
        var (_, html) = await afClient.GetPageAsync($"/Talk/Details/{conversationId}");

        Assert.Contains("&lt;b&gt;Bold", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"<b>Bold", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_full_approve_flow_creates_exactly_one_task_and_one_planner_session()
    {
        var project = await SeedProjectAsync($"Talk Approve {Guid.NewGuid():N}");
        var revisionBefore = await CurrentRevisionAsync(project.Id);
        var afClient = NewClient();

        var (_, indexHtml) = await afClient.GetPageAsync("/Talk");
        var startToken = AntiforgeryHttpClient.ExtractAntiforgeryToken(indexHtml);

        var started = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            startToken,
            [new("NewRequest.Request", $"Plan the rollout for {project.Name}")]);

        var detailsUrl = started.Headers.Location!.ToString();

        // Nothing exists yet: this is the state the user sees before deciding.
        Assert.Equal(0, await CountTasksAsync(project.Id));
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(project.Id));

        var (_, detailsHtml) = await afClient.GetPageAsync(detailsUrl);
        var approveToken = AntiforgeryHttpClient.ExtractAntiforgeryToken(detailsHtml);
        var concurrencyToken = await CurrentTokenAsync(detailsUrl);

        var approved = await afClient.PostFormAsync(
            $"{detailsUrl}?handler=Approve",
            approveToken,
            [new("ActionConcurrencyToken", concurrencyToken.ToString())]);

        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = await dbContext.Tasks.AsNoTracking().SingleAsync(candidate => candidate.ProjectId == project.Id);
        Assert.Equal(TaskStatus.Ready, task.Status);

        var session = await dbContext.AgentSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.TaskId == task.Id);
        Assert.Equal(AgentSessionRole.Planner, session.Role);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Equal(revisionBefore + 2, await CurrentRevisionAsync(project.Id));
        Assert.Equal(revisionBefore + 2, session.ContextRevisionRead);

        // The details page links to the created work and shows live session status.
        var (_, afterHtml) = await afClient.GetPageAsync(detailsUrl);
        Assert.Contains($"/Tasks/Details/{task.Id}", afterHtml, StringComparison.Ordinal);
        Assert.Contains("Started", afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Approve", afterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resubmitting_approval_creates_no_second_task_or_session()
    {
        var project = await SeedProjectAsync($"Talk Replay {Guid.NewGuid():N}");
        var (conversationId, concurrencyToken) = await SeedConversationAsync(project.Id);
        var afClient = NewClient();

        var (_, html) = await afClient.GetPageAsync($"/Talk/Details/{conversationId}");
        var antiforgery = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var first = await afClient.PostFormAsync(
            $"/Talk/Details/{conversationId}?handler=Approve",
            antiforgery,
            [new("ActionConcurrencyToken", concurrencyToken.ToString())]);
        var second = await afClient.PostFormAsync(
            $"/Talk/Details/{conversationId}?handler=Approve",
            antiforgery,
            [new("ActionConcurrencyToken", concurrencyToken.ToString())]);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);

        Assert.Equal(1, await CountTasksAsync(project.Id));

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var task = await dbContext.Tasks.AsNoTracking().SingleAsync(candidate => candidate.ProjectId == project.Id);
        Assert.Equal(1, await dbContext.AgentSessions.CountAsync(candidate => candidate.TaskId == task.Id));
    }

    [Fact]
    public async Task Rejection_creates_no_work_and_removes_the_approve_action()
    {
        var project = await SeedProjectAsync($"Talk Reject {Guid.NewGuid():N}");
        var revisionBefore = await CurrentRevisionAsync(project.Id);
        var (conversationId, concurrencyToken) = await SeedConversationAsync(project.Id);
        var afClient = NewClient();

        var (_, html) = await afClient.GetPageAsync($"/Talk/Details/{conversationId}");
        var antiforgery = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Talk/Details/{conversationId}?handler=Reject",
            antiforgery,
            [new("ActionConcurrencyToken", concurrencyToken.ToString())]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(0, await CountTasksAsync(project.Id));
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(project.Id));

        var (_, afterHtml) = await afClient.GetPageAsync($"/Talk/Details/{conversationId}");
        Assert.Contains("This proposal was rejected", afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Approve", afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Revise", afterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_token_posted_from_an_old_page_is_reported_and_changes_nothing()
    {
        var project = await SeedProjectAsync($"Talk Stale {Guid.NewGuid():N}");
        var (conversationId, staleToken) = await SeedConversationAsync(project.Id);
        var afClient = NewClient();

        var (_, html) = await afClient.GetPageAsync($"/Talk/Details/{conversationId}");
        var antiforgery = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        // Someone revises the proposal, rotating the token.
        await afClient.PostFormAsync(
            $"/Talk/Details/{conversationId}?handler=Revise",
            antiforgery,
            [
                new("Revision.ConcurrencyToken", staleToken.ToString()),
                new("Revision.ProjectId", project.Id.ToString()),
                new("Revision.Title", "A newer reviewed title"),
                new("Revision.RequestedOutcome", "A newer reviewed outcome.")
            ]);

        // The original page is still open and tries to approve with the token it rendered.
        var stale = await afClient.PostFormAsync(
            $"/Talk/Details/{conversationId}?handler=Approve",
            antiforgery,
            [new("ActionConcurrencyToken", staleToken.ToString())]);

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Contains("The proposal changed after this page was loaded", staleHtml, StringComparison.Ordinal);
        Assert.Equal(0, await CountTasksAsync(project.Id));
    }

    [Fact]
    public async Task Overposting_cannot_set_approved_state_role_or_created_identifiers()
    {
        var project = await SeedProjectAsync($"Talk Overpost {Guid.NewGuid():N}");
        var (conversationId, concurrencyToken) = await SeedConversationAsync(project.Id);
        var afClient = NewClient();

        var (_, html) = await afClient.GetPageAsync($"/Talk/Details/{conversationId}");
        var antiforgery = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var forgedTaskId = Guid.NewGuid();

        await afClient.PostFormAsync(
            $"/Talk/Details/{conversationId}?handler=Revise",
            antiforgery,
            [
                new("Revision.ConcurrencyToken", concurrencyToken.ToString()),
                new("Revision.ProjectId", project.Id.ToString()),
                new("Revision.Title", "An honest title"),
                new("Revision.RequestedOutcome", "An honest outcome."),
                // None of these correspond to bindable properties; they must be ignored entirely.
                new("Revision.Role", nameof(AgentSessionRole.Implementer)),
                new("Revision.Status", nameof(WorkProposalStatus.Approved)),
                new("Revision.CreatedTaskId", forgedTaskId.ToString()),
                new("Document.Status", nameof(ConversationStatus.Approved))
            ]);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var proposal = await dbContext.WorkProposals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ConversationId == conversationId);
        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);

        Assert.Equal(AgentSessionRole.Planner, proposal.Role);
        Assert.Equal(WorkProposalStatus.Pending, proposal.Status);
        Assert.Null(proposal.CreatedTaskId);
        Assert.Equal(ConversationStatus.AwaitingApproval, conversation.Status);
        Assert.Null(conversation.ApprovedTaskId);
        Assert.Equal(0, await CountTasksAsync(project.Id));
    }

    [Fact]
    public async Task An_unknown_conversation_returns_not_found()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/Talk/Details/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private AntiforgeryHttpClient NewClient() =>
        new(factory.CreateClient(new() { AllowAutoRedirect = false }));

    private async Task<Guid> CurrentTokenAsync(string detailsUrl)
    {
        var conversationId = Guid.Parse(detailsUrl.Split('/')[^1]);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await dbContext.WorkProposals
            .AsNoTracking()
            .Where(proposal => proposal.ConversationId == conversationId)
            .Select(proposal => proposal.ConcurrencyToken)
            .SingleAsync();
    }

    private async Task<int> CountTasksAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await dbContext.Tasks.CountAsync(task => task.ProjectId == projectId);
    }

    private async Task<int> CountConversationsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await dbContext.Conversations.CountAsync();
    }

    private async Task<int> CurrentRevisionAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.ContextRevision)
            .SingleAsync();
    }

    private async Task<FamiliarProject> SeedProjectAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, name);
    }

    private async Task<(Guid ConversationId, Guid ConcurrencyToken)> SeedConversationAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var (conversationId, proposal) = await WorkProposalServiceTests.SeedConversationAsync(dbContext, projectId);
        return (conversationId, proposal.ConcurrencyToken);
    }
}
