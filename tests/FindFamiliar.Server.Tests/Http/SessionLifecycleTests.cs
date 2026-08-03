using System.Net;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class SessionLifecycleTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Fresh_StartSession_reads_the_incremented_revision_and_assignment_has_no_stale_warning()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id);

        int revisionBeforeStart;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            revisionBeforeStart = await dbContext.Projects
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ContextRevision)
                .SingleAsync();
        }

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=StartSession",
            token,
            [new("NewSession.Role", nameof(AgentSessionRole.Planner))]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var session = await verifyDbContext.AgentSessions.SingleAsync(candidate => candidate.TaskId == task.Id);
        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);

        Assert.Equal(revisionBeforeStart + 1, refreshedProject.ContextRevision);
        Assert.Equal(refreshedProject.ContextRevision, session.ContextRevisionRead);

        using var packetClient = factory.CreateClient();
        var packetResponse = await packetClient.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");
        var packet = await packetResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, packetResponse.StatusCode);
        Assert.DoesNotContain("STALE CONTEXT WARNING", packet);
    }

    [Fact]
    public async Task Later_genuine_revision_change_still_produces_stale_warning_after_the_fix()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=StartSession",
            token,
            [new("NewSession.Role", nameof(AgentSessionRole.Planner))]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Guid sessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var session = await dbContext.AgentSessions.SingleAsync(candidate => candidate.TaskId == task.Id);
            sessionId = session.Id;

            var trackedProject = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
            trackedProject.IncrementContextRevision();
            await dbContext.SaveChangesAsync();
        }

        using var packetClient = factory.CreateClient();
        var packetResponse = await packetClient.GetAsync($"/tasks/{task.Id}/sessions/{sessionId}/assignment.md");
        var packet = await packetResponse.Content.ReadAsStringAsync();

        Assert.Contains("STALE CONTEXT WARNING", packet);
    }

    [Fact]
    public async Task Second_StartSession_while_one_is_Started_performs_no_writes()
    {
        var (project, task, firstSession) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

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
            $"/Tasks/Details/{task.Id}?handler=StartSession",
            token,
            [new("NewSession.Role", nameof(AgentSessionRole.Implementer))]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var sessionCount = await verifyDbContext.AgentSessions.CountAsync(candidate => candidate.TaskId == task.Id);
        Assert.Equal(1, sessionCount);

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore, refreshedProject.ContextRevision);

        var refreshedTask = await verifyDbContext.Tasks.SingleAsync(candidate => candidate.Id == task.Id);
        Assert.Equal(taskUpdatedBefore, refreshedTask.UpdatedUtc);

        var refreshedFirstSession = await verifyDbContext.AgentSessions.SingleAsync(candidate => candidate.Id == firstSession.Id);
        Assert.Equal(AgentSessionStatus.Started, refreshedFirstSession.Status);
    }

    [Fact]
    public async Task Valid_cancellation_creates_one_linked_active_handoff_and_terminates_atomically()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Reviewer);

        int revisionBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            revisionBefore = await dbContext.Projects
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ContextRevision)
                .SingleAsync();
        }

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CancelSession",
            token,
            BuildCancellationFields(session.Id, "Dogfood cancellation before independent review"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var refreshedSession = await verifyDbContext.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Cancelled, refreshedSession.Status);
        Assert.NotNull(refreshedSession.CompletedUtc);

        var handoffEntries = await verifyDbContext.ContextEntries
            .Where(entry => entry.SourceSessionId == session.Id)
            .ToListAsync();

        var handoff = Assert.Single(handoffEntries);
        Assert.Equal(ContextEntryKind.Handoff, handoff.Kind);
        Assert.Equal("Reviewer session cancelled", handoff.Title);
        Assert.Equal("Dogfood cancellation before independent review", handoff.Content);
        Assert.Equal(ContextEntryState.Active, handoff.State);
        Assert.Equal(project.Id, handoff.ProjectId);
        Assert.Equal(task.Id, handoff.TaskId);

        var refreshedTask = await verifyDbContext.Tasks.SingleAsync(candidate => candidate.Id == task.Id);
        Assert.Equal(refreshedSession.CompletedUtc, refreshedTask.UpdatedUtc);

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore + 1, refreshedProject.ContextRevision);

        using var packetClient = factory.CreateClient();
        var packetResponse = await packetClient.GetAsync($"/tasks/{task.Id}/sessions/{session.Id}/assignment.md");
        Assert.Equal(HttpStatusCode.Conflict, packetResponse.StatusCode);
    }

    [Fact]
    public async Task Missing_cancellation_reason_performs_no_writes()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = BuildCancellationFields(session.Id, string.Empty);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CancelSession",
            token,
            fields);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertSessionUnchangedAsync(session.Id);
    }

    [Fact]
    public async Task Oversized_cancellation_reason_performs_no_writes()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var fields = BuildCancellationFields(session.Id, new string('x', 2_001));

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CancelSession",
            token,
            fields);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertSessionUnchangedAsync(session.Id);
    }

    [Fact]
    public async Task Cross_task_cancellation_returns_not_found_with_no_writes()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var otherTask = await SeedTaskAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{otherTask.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{otherTask.Id}?handler=CancelSession",
            token,
            BuildCancellationFields(session.Id, "Cross task cancellation attempt."));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionUnchangedAsync(session.Id);
    }

    [Fact]
    public async Task Replaying_cancellation_for_a_non_started_session_performs_no_writes()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, firstHtml) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var firstToken = AntiforgeryHttpClient.ExtractAntiforgeryToken(firstHtml);
        var firstResponse = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CancelSession",
            firstToken,
            BuildCancellationFields(session.Id, "First cancellation."));
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
            $"/Tasks/Details/{task.Id}?handler=CancelSession",
            replayToken,
            BuildCancellationFields(session.Id, "Replay cancellation."));

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entryCount = await verifyDbContext.ContextEntries.CountAsync(entry => entry.SourceSessionId == session.Id);
        Assert.Equal(1, entryCount);

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionAfterFirst, refreshedProject.ContextRevision);
    }

    [Fact]
    public async Task Posted_cancellation_reason_survives_validation_failure()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var oversizedReason = new string('y', 2_001);
        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=CancelSession",
            token,
            BuildCancellationFields(session.Id, oversizedReason));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reloadedHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains(oversizedReason, reloadedHtml);
    }

    [Fact]
    public async Task Task_details_shows_lifecycle_controls_only_for_Started_sessions()
    {
        var (_, task, startedSession) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        Guid cancelledSessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var cancelledSession = new AgentSession
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Role = AgentSessionRole.Reviewer,
                Status = AgentSessionStatus.Cancelled,
                ContextRevisionRead = 0,
                StartedUtc = DateTime.UtcNow.AddHours(-2),
                CompletedUtc = DateTime.UtcNow.AddHours(-1)
            };
            dbContext.AgentSessions.Add(cancelledSession);
            await dbContext.SaveChangesAsync();
            cancelledSessionId = cancelledSession.Id;
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/Tasks/Details/{task.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"value=\"{startedSession.Id}\"", html);
        Assert.Contains("Ended", html);
        Assert.DoesNotContain($"cancel-reason-{cancelledSessionId}", html);
    }

    private static List<KeyValuePair<string, string>> BuildCancellationFields(Guid sessionId, string reason)
    {
        return
        [
            new("SessionCancellation.SessionId", sessionId.ToString()),
            new("SessionCancellation.Reason", reason)
        ];
    }

    private async Task AssertSessionUnchangedAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entryCount = await dbContext.ContextEntries.CountAsync(entry => entry.SourceSessionId == sessionId);
        Assert.Equal(0, entryCount);

        var session = await dbContext.AgentSessions.SingleAsync(candidate => candidate.Id == sessionId);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Null(session.CompletedUtc);
    }

    private async Task<FamiliarProject> SeedProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for SessionLifecycleTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    private async Task<FamiliarTask> SeedTaskAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for SessionLifecycleTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(AgentSessionRole role)
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        };

        dbContext.AgentSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }
}
