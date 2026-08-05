using System.Net;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Pages;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The Demiplane through the real HTTP pipeline.
///
/// The page is where Sprint 10's promise is either kept or broken: a human should understand the
/// project without a terminal, and should be able to act on a decision without losing any Sprint 09
/// guarantee. These tests assert both — including that approving from this page goes through the same
/// fenced service and creates exactly one session.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class DemiplanePageTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task An_unknown_project_is_not_found()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Demiplane/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task An_empty_project_renders_without_error()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/Demiplane/{project.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(project.Name, html, StringComparison.Ordinal);
        Assert.Contains("no tasks yet", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_this_projects_tasks_appear()
    {
        var mine = await SeedProjectAsync();
        var theirs = await SeedProjectAsync();

        var marker = Guid.NewGuid().ToString("N");
        await SeedTaskAsync(mine.Id, $"Mine {marker}");
        await SeedTaskAsync(theirs.Id, $"Theirs {marker}");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{mine.Id}");

        Assert.Contains($"Mine {marker}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"Theirs {marker}", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// State must never be carried by colour alone. Every state renders a glyph marker and a text
    /// label; the CSS accent is a third, redundant signal.
    /// </summary>
    [Fact]
    public async Task Task_state_is_communicated_by_text_and_marker_not_only_colour()
    {
        var project = await SeedProjectAsync();
        await SeedTaskAsync(project.Id, $"Fresh {Guid.NewGuid():N}");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        // ASP.NET Core's default encoder emits non-ASCII as numeric entities, so decode before
        // asserting on the marker glyph the reader actually sees.
        var rendered = WebUtility.HtmlDecode(html);

        Assert.Contains("Not started", rendered, StringComparison.Ordinal);
        Assert.Contains(DemiplaneModel.MarkerFor(TaskDisplayState.NotStarted), rendered, StringComparison.Ordinal);

        // Markers are decorative duplicates of the adjacent text, so they are hidden from assistive
        // technology rather than read out as punctuation.
        Assert.Contains("state-marker", html, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sections_are_labelled_for_assistive_technology()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        Assert.Contains("aria-labelledby=\"health-title\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"attention-title\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"providers-title\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The readiness strip must say plainly that capacity is unknown rather than showing a fabricated
    /// number. This asserts the honesty commitment ADR-0011 records.
    /// </summary>
    [Fact]
    public async Task Provider_readiness_reports_unknown_rather_than_inventing_usage()
    {
        var project = await SeedProjectAsync();

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        Assert.Contains("Provider readiness", html, StringComparison.Ordinal);
        Assert.Contains("Claude", html, StringComparison.Ordinal);
        Assert.Contains("Unknown", html, StringComparison.Ordinal);
        Assert.Contains("no non-interactive usage or quota surface", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settled_project_does_not_ask_the_browser_to_refresh()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Done {Guid.NewGuid():N}");
        await SetTaskStatusAsync(task.Id, TaskStatus.Completed);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refresh exists only while something is genuinely in flight, and its interval is bounded — an
    /// idle Demiplane must not poll, and a running one must not hammer.
    /// </summary>
    [Fact]
    public async Task A_running_project_refreshes_at_a_bounded_interval()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Running {Guid.NewGuid():N}");
        await SeedRunningSessionAsync(task.Id, AgentSessionRole.Planner);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        Assert.Contains($"content=\"{DemiplaneModel.ActiveRefreshSeconds}\"", html, StringComparison.Ordinal);
        Assert.True(DemiplaneModel.ActiveRefreshSeconds >= 15, "Refresh must not be aggressive.");
    }

    [Fact]
    public async Task Selecting_a_task_reveals_the_familiar_summary()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Selectable {Guid.NewGuid():N}");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}?taskId={task.Id}");

        Assert.Contains("Familiar's account", html, StringComparison.Ordinal);
        Assert.Contains("What happened", html, StringComparison.Ordinal);
        Assert.Contains("Right now", html, StringComparison.Ordinal);

        // The limitation is stated rather than papered over with a plausible sentence.
        Assert.Contains("stores no structured build or test result", html, StringComparison.Ordinal);
    }

    /// <summary>A task id from another project must not open a detail panel here.</summary>
    [Fact]
    public async Task Selecting_a_task_from_another_project_shows_no_detail()
    {
        var mine = await SeedProjectAsync();
        var theirs = await SeedProjectAsync();
        var foreignTask = await SeedTaskAsync(theirs.Id, $"Foreign {Guid.NewGuid():N}");

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{mine.Id}?taskId={foreignTask.Id}");

        Assert.DoesNotContain(foreignTask.Title, html, StringComparison.Ordinal);
        Assert.DoesNotContain("The Familiar&#x27;s account", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_handoff_is_surfaced_with_approve_and_decline_controls()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Proposed {Guid.NewGuid():N}");
        await SeedCompletedPlannerWithHandoffAsync(task.Id);

        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/Demiplane/{project.Id}");

        Assert.Contains("Waiting for you", html, StringComparison.Ordinal);
        Assert.Contains("handler=Approve", html, StringComparison.Ordinal);
        Assert.Contains("handler=Decline", html, StringComparison.Ordinal);
        Assert.Contains("Waiting for your approval", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Approving from the Demiplane must be the same fenced Sprint 09 transaction, not a second path.
    /// </summary>
    [Fact]
    public async Task Approving_from_the_demiplane_creates_exactly_one_session()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Approve me {Guid.NewGuid():N}");
        var handoff = await SeedCompletedPlannerWithHandoffAsync(task.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Demiplane/{project.Id}");

        var response = await afClient.PostFormAsync(
            $"/Demiplane/{project.Id}?handler=Approve",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [
                new("Decision.HandoffId", handoff.Id.ToString()),
                new("Decision.ExpectedConcurrencyToken", handoff.ConcurrencyToken.ToString())
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var implementer = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == task.Id && session.Role == AgentSessionRole.Implementer)
            .ToListAsync();
        Assert.Single(implementer);
        Assert.Equal(AgentSessionStatus.Started, implementer[0].Status);

        var stored = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == handoff.Id);
        Assert.Equal(SessionHandoffStatus.Approved, stored.Status);
        Assert.Equal(implementer[0].Id, stored.CreatedSessionId);
    }

    /// <summary>A double submit — the classic double-click — must still produce one session.</summary>
    [Fact]
    public async Task Double_approval_from_the_demiplane_creates_exactly_one_session()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Double {Guid.NewGuid():N}");
        var handoff = await SeedCompletedPlannerWithHandoffAsync(task.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Demiplane/{project.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Decision.HandoffId", handoff.Id.ToString()),
            new("Decision.ExpectedConcurrencyToken", handoff.ConcurrencyToken.ToString())
        };

        var first = await afClient.PostFormAsync($"/Demiplane/{project.Id}?handler=Approve", token, fields);
        var second = await afClient.PostFormAsync($"/Demiplane/{project.Id}?handler=Approve", token, fields);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Single(await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == task.Id && session.Role == AgentSessionRole.Implementer)
            .ToListAsync());
    }

    [Fact]
    public async Task Declining_from_the_demiplane_creates_nothing_and_is_terminal()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Decline me {Guid.NewGuid():N}");
        var handoff = await SeedCompletedPlannerWithHandoffAsync(task.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Demiplane/{project.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Decision.HandoffId", handoff.Id.ToString()),
            new("Decision.ExpectedConcurrencyToken", handoff.ConcurrencyToken.ToString())
        };

        Assert.Equal(
            HttpStatusCode.Redirect,
            (await afClient.PostFormAsync($"/Demiplane/{project.Id}?handler=Decline", token, fields)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Empty(await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == task.Id && session.Role == AgentSessionRole.Implementer)
            .ToListAsync());

        var stored = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == handoff.Id);
        Assert.Equal(SessionHandoffStatus.Declined, stored.Status);

        // A declined step cannot then be approved: the token rotated and the state is terminal.
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await afClient.PostFormAsync($"/Demiplane/{project.Id}?handler=Approve", token, fields)).StatusCode);

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == task.Id && session.Role == AgentSessionRole.Implementer)
            .ToListAsync());
    }

    /// <summary>
    /// Every mutation is POST-only with antiforgery. A GET, or a POST without a token, must change
    /// nothing — the Demiplane adds no unguarded way to start work.
    /// </summary>
    [Fact]
    public async Task Approval_requires_a_post_with_an_antiforgery_token()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id, $"Guarded {Guid.NewGuid():N}");
        var handoff = await SeedCompletedPlannerWithHandoffAsync(task.Id);

        using var raw = factory.CreateClient(new() { AllowAutoRedirect = false });

        var untokenised = await raw.PostAsync(
            $"/Demiplane/{project.Id}?handler=Approve",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Decision.HandoffId"] = handoff.Id.ToString(),
                ["Decision.ExpectedConcurrencyToken"] = handoff.ConcurrencyToken.ToString()
            }));

        Assert.Equal(HttpStatusCode.BadRequest, untokenised.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Empty(await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == task.Id && session.Role == AgentSessionRole.Implementer)
            .ToListAsync());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<FamiliarProject> SeedProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Demiplane page project {Guid.NewGuid():N}",
            Purpose = "Seeded for DemiplanePageTests.",
            Status = ProjectStatus.Active,
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
            RequestedOutcome = "Seeded for DemiplanePageTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }

    private async Task SetTaskStatusAsync(Guid taskId, TaskStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        await dbContext.Tasks
            .Where(task => task.Id == taskId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Status, status));
    }

    /// <summary>
    /// Seeds a session that is claimed and running. Claiming it makes the resulting display state
    /// deterministic: an unclaimed session's state depends on which workers happen to be registered
    /// in the shared test database, which other tests also write to.
    /// </summary>
    private async Task SeedRunningSessionAsync(Guid taskId, AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            WorkerKey = $"demiplane-page-{Guid.NewGuid():N}",
            DisplayName = "Demiplane page test worker",
            Enabled = true,
            Capabilities = role.ToString(),
            RegisteredUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow
        };

        dbContext.Workers.Add(worker);
        dbContext.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Role = role,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 1,
            StartedUtc = DateTime.UtcNow,
            ClaimedByWorkerId = worker.Id,
            ClaimedUtc = DateTime.UtcNow,
            ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(20),
            ClaimId = Guid.NewGuid()
        });

        await dbContext.SaveChangesAsync();
    }

    private async Task<SessionHandoff> SeedCompletedPlannerWithHandoffAsync(Guid taskId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var planner = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Completed,
            ContextRevisionRead = 1,
            StartedUtc = DateTime.UtcNow.AddMinutes(-20),
            CompletedUtc = DateTime.UtcNow.AddMinutes(-2)
        };

        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SourceSessionId = planner.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Implementer,
            Kind = SessionHandoffKind.NextRole,
            Status = SessionHandoffStatus.Pending,
            ObservedContextRevision = 1,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.AddRange(planner, handoff);
        await dbContext.SaveChangesAsync();
        return handoff;
    }
}
