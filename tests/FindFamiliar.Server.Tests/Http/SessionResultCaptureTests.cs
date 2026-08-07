using System.Net;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Pages.Tasks;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class SessionResultCaptureTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public void Details_model_does_not_expose_the_legacy_completion_handler()
    {
        Assert.Null(typeof(DetailsModel).GetMethod("OnPostCompleteSessionAsync"));
    }

    [Fact]
    public async Task Direct_post_to_removed_complete_handler_performs_no_writes()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        int projectRevisionBefore;
        DateTime taskUpdatedBefore;
        AgentSessionStatus sessionStatusBefore;
        DateTime sessionStartedBefore;
        DateTime? sessionCompletedBefore;
        int taskContextEntryCountBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            projectRevisionBefore = await dbContext.Projects
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ContextRevision)
                .SingleAsync();
            taskUpdatedBefore = await dbContext.Tasks
                .Where(candidate => candidate.Id == task.Id)
                .Select(candidate => candidate.UpdatedUtc)
                .SingleAsync();
            var storedSession = await dbContext.AgentSessions
                .SingleAsync(candidate => candidate.Id == session.Id);
            sessionStatusBefore = storedSession.Status;
            sessionStartedBefore = storedSession.StartedUtc;
            sessionCompletedBefore = storedSession.CompletedUtc;
            taskContextEntryCountBefore = await dbContext.ContextEntries
                .CountAsync(candidate => candidate.TaskId == task.Id);
        }

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        _ = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CompleteSession&sessionId={session.Id}",
            token,
            []);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var refreshedSession = await verifyDbContext.AgentSessions
            .SingleAsync(candidate => candidate.Id == session.Id);
        var refreshedProject = await verifyDbContext.Projects
            .SingleAsync(candidate => candidate.Id == project.Id);
        var refreshedTask = await verifyDbContext.Tasks
            .SingleAsync(candidate => candidate.Id == task.Id);
        var taskContextEntryCountAfter = await verifyDbContext.ContextEntries
            .CountAsync(candidate => candidate.TaskId == task.Id);

        Assert.Equal(sessionStatusBefore, refreshedSession.Status);
        Assert.Equal(sessionStartedBefore, refreshedSession.StartedUtc);
        Assert.Equal(sessionCompletedBefore, refreshedSession.CompletedUtc);
        Assert.Equal(projectRevisionBefore, refreshedProject.ContextRevision);
        Assert.Equal(taskUpdatedBefore, refreshedTask.UpdatedUtc);
        Assert.Equal(taskContextEntryCountBefore, taskContextEntryCountAfter);
    }

    [Theory]
    [InlineData(AgentSessionRole.Planner, ContextEntryKind.Plan)]
    [InlineData(AgentSessionRole.Implementer, ContextEntryKind.Implementation)]
    [InlineData(AgentSessionRole.Reviewer, ContextEntryKind.Review)]
    public async Task Capture_maps_persisted_role_to_expected_artifact_kind(AgentSessionRole role, ContextEntryKind expectedKind)
    {
        var (_, task, session) = await SeedStartedSessionAsync(role);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CaptureSessionResult",
            token,
            BuildFormFields(session.Id, "Result title"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var artifact = await dbContext.ContextEntries.SingleAsync(
            entry => entry.SourceSessionId == session.Id && entry.Kind == expectedKind);
        Assert.Equal("Result title", artifact.Title);
    }

    [Fact]
    public async Task Valid_capture_creates_exactly_four_entries_completes_session_and_updates_bookkeeping_once()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Implementer);

        int revisionBefore;
        DateTime taskUpdatedBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            revisionBefore = await dbContext.Projects
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ContextRevision)
                .SingleAsync();
            taskUpdatedBefore = await dbContext.Tasks
                .Where(candidate => candidate.Id == task.Id)
                .Select(candidate => candidate.UpdatedUtc)
                .SingleAsync();
        }

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CaptureSessionResult",
            token,
            BuildFormFields(session.Id, "Implementation title"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entries = await verifyDbContext.ContextEntries
            .Where(entry => entry.SourceSessionId == session.Id)
            .ToListAsync();

        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry => Assert.Equal(ContextEntryState.Active, entry.State));
        Assert.All(entries, entry => Assert.Equal(project.Id, entry.ProjectId));
        Assert.All(entries, entry => Assert.Equal(task.Id, entry.TaskId));
        Assert.All(entries, entry => Assert.Equal(session.Id, entry.SourceSessionId));

        Assert.Contains(entries, entry =>
            entry.Kind == ContextEntryKind.Prompt
            && entry.Title == "Implementer session prompt"
            && entry.Content == "The exact prompt.");
        Assert.Contains(entries, entry =>
            entry.Kind == ContextEntryKind.RawOutput && entry.Title == "Implementer raw output");
        Assert.Contains(entries, entry =>
            entry.Kind == ContextEntryKind.Summary && entry.Title == "Implementer summary");
        Assert.Contains(entries, entry =>
            entry.Kind == ContextEntryKind.Implementation && entry.Title == "Implementation title");

        var refreshedSession = await verifyDbContext.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Completed, refreshedSession.Status);
        Assert.NotNull(refreshedSession.CompletedUtc);

        var refreshedTask = await verifyDbContext.Tasks.SingleAsync(candidate => candidate.Id == task.Id);
        Assert.True(refreshedTask.UpdatedUtc > taskUpdatedBefore);

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore + 1, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Omitted_required_field_performs_no_writes()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = BuildFormFields(session.Id, "Plan title")
            .Where(field => field.Key != "SessionResult.Summary")
            .ToList();

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CaptureSessionResult",
            token,
            fields);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Oversized_raw_output_performs_no_writes()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = BuildFormFields(session.Id, "Plan title")
            .Select(field => field.Key == "SessionResult.RawOutput"
                ? new KeyValuePair<string, string>(field.Key, new string('x', 12_001))
                : field)
            .ToList();

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CaptureSessionResult",
            token,
            fields);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Cross_task_session_is_rejected_with_no_writes()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var otherTask = await SeedTaskAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{otherTask.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{otherTask.Id}?handler=CaptureSessionResult",
            token,
            BuildFormFields(session.Id, "Plan title"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertNoWritesAsync(session.Id);
    }

    [Fact]
    public async Task Replaying_capture_for_completed_session_creates_no_duplicates_and_does_not_increment_revision()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, firstHtml) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var firstToken = AntiforgeryHttpClient.ExtractAntiforgeryToken(firstHtml);
        var firstResponse = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CaptureSessionResult",
            firstToken,
            BuildFormFields(session.Id, "Plan title"));
        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);

        int revisionAfterFirst;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            revisionAfterFirst = await dbContext.Projects
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ContextRevision)
                .SingleAsync();
        }

        var (_, replayHtml) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var replayToken = AntiforgeryHttpClient.ExtractAntiforgeryToken(replayHtml);
        var replayResponse = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CaptureSessionResult",
            replayToken,
            BuildFormFields(session.Id, "Plan title replay"));

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entries = await verifyDbContext.ContextEntries
            .Where(entry => entry.SourceSessionId == session.Id)
            .ToListAsync();
        Assert.Equal(4, entries.Count);

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionAfterFirst, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Get_page_offers_only_started_sessions_and_no_standalone_complete_form()
    {
        var (_, task, startedSession) = await SeedStartedSessionAsync(AgentSessionRole.Reviewer);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            dbContext.AgentSessions.Add(new AgentSession
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Role = AgentSessionRole.Planner,
                Status = AgentSessionStatus.Completed,
                ContextRevisionRead = 0,
                StartedUtc = DateTime.UtcNow.AddHours(-1),
                CompletedUtc = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/Tasks/Details/{task.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"value=\"{startedSession.Id}\"", html);
        Assert.DoesNotContain("handler=CompleteSession", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateContextEntry_succeeds_without_any_session_result_fields()
    {
        var (project, task, _) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CreateContextEntry",
            token,
            [
                new("NewContextEntry.Kind", nameof(ContextEntryKind.Handoff)),
                new("NewContextEntry.Title", "Manual handoff note"),
                new("NewContextEntry.Content", "Recorded without any SessionResult.* fields.")
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entry = await dbContext.ContextEntries.SingleAsync(candidate =>
            candidate.ProjectId == project.Id && candidate.Title == "Manual handoff note");
        Assert.Equal(ContextEntryKind.Handoff, entry.Kind);
    }

    [Fact]
    public async Task UpdateStatus_succeeds_without_any_session_result_fields()
    {
        var (_, task, _) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=UpdateStatus",
            token,
            [
                new("NewTaskStatus", nameof(TaskStatus.InProgress))
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var refreshed = await dbContext.Tasks.SingleAsync(candidate => candidate.Id == task.Id);
        Assert.Equal(TaskStatus.InProgress, refreshed.Status);
    }

    /// <summary>
    /// Closing a task retires the decision that was waiting on it — the defect this test exists for.
    ///
    /// Before this, the handoff stayed Pending forever: it could not be approved, because the approval
    /// service refuses a Completed task, and nothing retired it. It sat in every "waiting for you"
    /// list being asked about and never answerable. Two real ones had been doing that for a day.
    /// </summary>
    [Fact]
    public async Task Completing_a_task_retires_the_handoff_waiting_on_it()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var handoffId = await SeedPendingHandoffAsync(task.Id, session.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=UpdateStatus",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [new("NewTaskStatus", nameof(TaskStatus.Completed))]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            TaskStatus.Completed,
            (await dbContext.Tasks.AsNoTracking().SingleAsync(candidate => candidate.Id == task.Id)).Status);

        var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoffId);
        Assert.Equal(SessionHandoffStatus.Superseded, handoff.Status);

        // Nobody decided this — it stopped applying — so it is not recorded as a human decision.
        Assert.Null(handoff.DecidedUtc);
    }

    /// <summary>
    /// A status change that is not a closure leaves the decision alone. A task moving to Blocked still
    /// has a real question waiting on it, and retiring that would quietly discard the next step.
    /// </summary>
    [Theory]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.Blocked)]
    public async Task A_status_change_that_is_not_a_closure_leaves_the_handoff_pending(TaskStatus status)
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var handoffId = await SeedPendingHandoffAsync(task.Id, session.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");

        await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=UpdateStatus",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [new("NewTaskStatus", status.ToString())]);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(
            SessionHandoffStatus.Pending,
            (await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoffId)).Status);
    }

    private async Task<Guid> SeedPendingHandoffAsync(Guid taskId, Guid sourceSessionId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SourceSessionId = sourceSessionId,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = AgentSessionRole.Implementer,
            Kind = SessionHandoffKind.NextRole,
            Status = SessionHandoffStatus.Pending,
            ObservedContextRevision = 0,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.SessionHandoffs.Add(handoff);
        await dbContext.SaveChangesAsync();

        return handoff.Id;
    }

    private static List<KeyValuePair<string, string>> BuildFormFields(Guid sessionId, string artifactTitle)
    {
        return
        [
            new("SessionResult.SessionId", sessionId.ToString()),
            new("SessionResult.Prompt", "The exact prompt."),
            new("SessionResult.RawOutput", "A bounded excerpt of the response."),
            new("SessionResult.Summary", "A concise summary."),
            new("SessionResult.ArtifactTitle", artifactTitle),
            new("SessionResult.ArtifactContent", "The role-specific artifact content.")
        ];
    }

    private async Task AssertNoWritesAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entryCount = await dbContext.ContextEntries.CountAsync(entry => entry.SourceSessionId == sessionId);
        Assert.Equal(0, entryCount);

        var session = await dbContext.AgentSessions.SingleAsync(candidate => candidate.Id == sessionId);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Null(session.CompletedUtc);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionResultCaptureTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionResultCaptureTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        };

        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }

    private async Task<FamiliarTask> SeedTaskAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = $"Sibling task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionResultCaptureTests cross-task rejection.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }
}
