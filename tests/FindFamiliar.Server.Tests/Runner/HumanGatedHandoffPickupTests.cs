using FindFamiliar.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using FindFamiliar.Server.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Runner;

/// <summary>
/// The Sprint 09 definition of done, proved end to end without a live provider.
///
/// A Planner session runs and completes. Familiar records the Implementer step it proposes. A worker
/// that is fully capable of running that step polls and finds nothing — because the gate is a human
/// decision, not a capability list. A human approves through the real HTTP pipeline, and only then
/// does the Implementer session exist and get claimed.
///
/// The worker is configured with a project mapping and its capabilities. No task ID and no session ID
/// is ever handed to it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class HumanGatedHandoffPickupTests(FindFamiliarWebApplicationFactory factory)
{
    private static readonly string FakeAdapterPath = ResolveExecutablePath("FindFamiliar.FakeAdapter");

    [Fact]
    public async Task An_approved_handoff_starts_the_next_role_and_the_worker_executes_it()
    {
        var project = await SeedProjectAsync($"Handoff Pickup {Guid.NewGuid():N}");
        var revisionBefore = await CurrentRevisionAsync(project.Id);

        var (taskId, plannerSessionId) = await DescribeAndApproveAsync(
            project,
            $"Plan and implement the next slice of {project.Name}");

        // Task creation and session start.
        Assert.Equal(revisionBefore + 2, await CurrentRevisionAsync(project.Id));

        // The worker can run both roles. That is deliberate: it is what makes the idle poll below
        // mean something.
        var configuration = BuildConfiguration(
            project.Id,
            $"workstation-handoff-{Guid.NewGuid():N}"[..40],
            ["Planner", "Implementer"]);

        Assert.Equal(
            WorkerPollOutcome.Executed,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration)));

        // Result capture.
        Assert.Equal(revisionBefore + 3, await CurrentRevisionAsync(project.Id));

        Guid handoffId;
        Guid handoffToken;

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

            var planner = await dbContext.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == plannerSessionId);
            Assert.Equal(AgentSessionStatus.Completed, planner.Status);

            // One session still, and one proposal waiting on a human.
            Assert.Single(await dbContext.AgentSessions.AsNoTracking().Where(s => s.TaskId == taskId).ToListAsync());

            var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.TaskId == taskId);
            Assert.Equal(SessionHandoffStatus.Pending, handoff.Status);
            Assert.Equal(AgentSessionRole.Implementer, handoff.ProposedRole);
            Assert.Equal(SessionHandoffKind.NextRole, handoff.Kind);
            Assert.Equal(plannerSessionId, handoff.SourceSessionId);

            handoffId = handoff.Id;
            handoffToken = handoff.ConcurrencyToken;
        }

        // THE ASSERTION THIS WHOLE SPRINT EXISTS FOR.
        //
        // The worker holds Implementer capability, has a mapping for this project, and an Implementer
        // step is proposed on it — and it still finds nothing to do. Nothing advances without a human
        // click. If handoff staging ever started a session on its own, this line fails.
        Assert.Equal(
            WorkerPollOutcome.Idle,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration)));

        Assert.Equal(revisionBefore + 3, await CurrentRevisionAsync(project.Id));

        // The human decides, through the real page and a real antiforgery token.
        await ApproveHandoffAsync(taskId, handoffId, handoffToken);

        // Session start, and nothing else.
        Assert.Equal(revisionBefore + 4, await CurrentRevisionAsync(project.Id));

        Assert.Equal(
            WorkerPollOutcome.Executed,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration)));

        Assert.Equal(revisionBefore + 5, await CurrentRevisionAsync(project.Id));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

            var sessions = await dbContext.AgentSessions
                .AsNoTracking()
                .Where(s => s.TaskId == taskId)
                .OrderBy(s => s.StartedUtc)
                .ToListAsync();

            Assert.Equal(2, sessions.Count);
            Assert.Equal(AgentSessionRole.Planner, sessions[0].Role);
            Assert.Equal(AgentSessionRole.Implementer, sessions[1].Role);
            Assert.All(sessions, session => Assert.Equal(AgentSessionStatus.Completed, session.Status));

            // The chained session read context that includes everything the Planner produced.
            Assert.True(sessions[1].ContextRevisionRead > sessions[0].ContextRevisionRead);

            // Four durable entries per session, through the untouched ADR-0003 capture path.
            Assert.Equal(8, await dbContext.ContextEntries.CountAsync(entry => entry.TaskId == taskId));
            Assert.Equal(
                1,
                await dbContext.ContextEntries.CountAsync(entry =>
                    entry.SourceSessionId == sessions[1].Id && entry.Kind == ContextEntryKind.Implementation));

            var approved = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == handoffId);
            Assert.Equal(SessionHandoffStatus.Approved, approved.Status);
            Assert.Equal(sessions[1].Id, approved.CreatedSessionId);
            Assert.NotNull(approved.DecidedUtc);

            // The chain continues, and is still gated.
            var next = await dbContext.SessionHandoffs
                .AsNoTracking()
                .SingleAsync(h => h.TaskId == taskId && h.Status == SessionHandoffStatus.Pending);
            Assert.Equal(AgentSessionRole.Reviewer, next.ProposedRole);

            // Nothing completed the task.
            var task = await dbContext.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
            Assert.NotEqual(FindFamiliar.Server.Domain.TaskStatus.Completed, task.Status);
        }
    }

    /// <summary>
    /// A worker without the proposed role is a separate failure mode from an unapproved step, and it
    /// must stay separate: approving a step whose role no worker declares leaves an unclaimed Started
    /// session, which the operator has to notice.
    /// </summary>
    [Fact]
    public async Task An_approved_handoff_whose_role_no_worker_declares_is_left_unclaimed()
    {
        var project = await SeedProjectAsync($"Handoff Uncapable {Guid.NewGuid():N}");

        var (taskId, _) = await DescribeAndApproveAsync(project, $"Plan the next slice of {project.Name}");

        var plannerOnly = BuildConfiguration(
            project.Id,
            $"workstation-planner-{Guid.NewGuid():N}"[..40],
            ["Planner"]);

        Assert.Equal(
            WorkerPollOutcome.Executed,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(plannerOnly)));

        Guid handoffId;
        Guid handoffToken;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.TaskId == taskId);
            handoffId = handoff.Id;
            handoffToken = handoff.ConcurrencyToken;
        }

        await ApproveHandoffAsync(taskId, handoffId, handoffToken);

        // The Implementer session exists and is Started, but this worker cannot claim it.
        Assert.Equal(
            WorkerPollOutcome.Idle,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(plannerOnly)));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var implementer = await dbContext.AgentSessions
                .AsNoTracking()
                .SingleAsync(s => s.TaskId == taskId && s.Role == AgentSessionRole.Implementer);

            Assert.Equal(AgentSessionStatus.Started, implementer.Status);
            Assert.Null(implementer.ClaimedByWorkerId);
        }
    }

    [Fact]
    public async Task A_declined_handoff_offers_the_worker_nothing()
    {
        var project = await SeedProjectAsync($"Handoff Declined {Guid.NewGuid():N}");

        var (taskId, _) = await DescribeAndApproveAsync(project, $"Plan the next slice of {project.Name}");

        var configuration = BuildConfiguration(
            project.Id,
            $"workstation-declined-{Guid.NewGuid():N}"[..40],
            ["Planner", "Implementer"]);

        Assert.Equal(
            WorkerPollOutcome.Executed,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration)));

        var revisionAfterCapture = await CurrentRevisionAsync(project.Id);

        Guid handoffId;
        Guid handoffToken;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.TaskId == taskId);
            handoffId = handoff.Id;
            handoffToken = handoff.ConcurrencyToken;
        }

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var detailsUrl = $"/Tasks/Details/{taskId}";
        var (_, html) = await afClient.GetPageAsync(detailsUrl);
        var declined = await afClient.PostFormAsync(
            $"{detailsUrl}?handler=DeclineHandoff",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [
                new("HandoffDecision.HandoffId", handoffId.ToString()),
                new("HandoffDecision.ExpectedConcurrencyToken", handoffToken.ToString())
            ]);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, declined.StatusCode);

        Assert.Equal(
            WorkerPollOutcome.Idle,
            await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration)));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

            Assert.Single(await dbContext.AgentSessions.AsNoTracking().Where(s => s.TaskId == taskId).ToListAsync());

            var handoff = await dbContext.SessionHandoffs.AsNoTracking().SingleAsync(h => h.Id == handoffId);
            Assert.Equal(SessionHandoffStatus.Declined, handoff.Status);
            Assert.Null(handoff.CreatedSessionId);
        }

        Assert.Equal(revisionAfterCapture, await CurrentRevisionAsync(project.Id));
    }

    private async Task ApproveHandoffAsync(Guid taskId, Guid handoffId, Guid concurrencyToken)
    {
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var detailsUrl = $"/Tasks/Details/{taskId}";

        var (_, html) = await afClient.GetPageAsync(detailsUrl);

        // The proposal is visible on the page the human reads before deciding.
        Assert.Contains("Next step", html, StringComparison.Ordinal);

        var approved = await afClient.PostFormAsync(
            $"{detailsUrl}?handler=ApproveHandoff",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(html),
            [
                new("HandoffDecision.HandoffId", handoffId.ToString()),
                new("HandoffDecision.ExpectedConcurrencyToken", concurrencyToken.ToString())
            ]);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, approved.StatusCode);
    }

    /// <summary>Drives the real Talk pages to get one approved task and Planner session.</summary>
    private async Task<(Guid TaskId, Guid SessionId)> DescribeAndApproveAsync(FamiliarProject project, string request)
    {
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, indexHtml) = await afClient.GetPageAsync("/Talk");
        var started = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(indexHtml),
            [new("NewRequest.Request", request)]);

        var detailsUrl = started.Headers.Location!.ToString();
        var conversationId = Guid.Parse(detailsUrl.Split('/')[^1]);

        var (_, detailsHtml) = await afClient.GetPageAsync(detailsUrl);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var token = await dbContext.WorkProposals
            .AsNoTracking()
            .Where(proposal => proposal.ConversationId == conversationId)
            .Select(proposal => proposal.ConcurrencyToken)
            .SingleAsync();

        var approved = await afClient.PostFormAsync(
            $"{detailsUrl}?handler=Approve",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(detailsHtml),
            [new("ActionConcurrencyToken", token.ToString())]);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, approved.StatusCode);

        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        Assert.Equal(ConversationStatus.Approved, conversation.Status);

        return (conversation.ApprovedTaskId!.Value, conversation.ApprovedSessionId!.Value);
    }

    private async Task<WorkerPollOutcome> PollOnceAsync(WorkerConfiguration configuration)
    {
        using var httpClient = factory.CreateClient();
        var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), TextWriter.Null);
        var loop = new WorkerLoop(httpClient, engine, configuration, TextWriter.Null, TimeProvider.System);

        return await loop.PollOnceAsync(CancellationToken.None);
    }

    private static WorkerConfiguration BuildConfiguration(
        Guid projectId,
        string workerKey,
        IReadOnlyList<string> capabilities) => new(
        new Uri("http://localhost/"),
        workerKey,
        workerKey,
        capabilities,
        FindFamiliarWebApplicationFactory.RunnerBridgeTestToken,
        FakeAdapterPath,
        [],
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
        600,
        [new WorkerProjectMapping(projectId, AppContext.BaseDirectory, AppContext.BaseDirectory, "read-only")]);

    private static async Task<T> WithFakeAdapterModeAsync<T>(string mode, Func<Task<T>> action)
    {
        const string variable = "FAKE_ADAPTER_MODE";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, mode);

        try
        {
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private async Task<FamiliarProject> SeedProjectAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = "Seeded for HumanGatedHandoffPickupTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
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

    private static string ResolveExecutablePath(string projectName)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{projectName}.exe" : projectName;
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }
}
