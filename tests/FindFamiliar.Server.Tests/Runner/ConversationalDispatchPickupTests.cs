using FindFamiliar.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using FindFamiliar.Server.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Runner;

/// <summary>
/// The Sprint 08 definition of done, proved end to end without a live provider.
///
/// A user describes work in ordinary language, reviews the proposal, and approves it through the
/// real HTTP pipeline. From that point nothing conversational is involved: the real
/// <see cref="WorkerLoop"/> discovers the session through the real machine API and executes it with
/// the real, separately-built FakeAdapter child process.
///
/// The worker is configured with a project mapping and nothing else. No task ID and no session ID
/// is ever handed to it — if the approved session were not an ordinary session on the ordinary
/// queue, this test could not pass.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ConversationalDispatchPickupTests(FindFamiliarWebApplicationFactory factory)
{
    private static readonly string FakeAdapterPath = ResolveExecutablePath("FindFamiliar.FakeAdapter");

    [Fact]
    public async Task An_approved_conversation_is_discovered_and_executed_by_the_existing_worker()
    {
        var project = await SeedProjectAsync($"Conversational Pickup {Guid.NewGuid():N}");
        var revisionBefore = await CurrentRevisionAsync(project.Id);

        var (conversationId, taskId, sessionId) = await DescribeAndApproveAsync(
            project,
            $"Plan the next slice of {project.Name}\nKeep the follow-up work small and reviewable.");

        Assert.Equal(revisionBefore + 2, await CurrentRevisionAsync(project.Id));

        // The worker knows only which projects it has a local mapping for.
        var configuration = BuildConfiguration(project.Id, $"workstation-talk-{Guid.NewGuid():N}"[..40]);

        var outcome = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Executed, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // Exactly one atomic result, through the untouched ADR-0003 capture path.
        var entries = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.SourceSessionId == sessionId)
            .ToListAsync();
        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, entry => entry.Kind == ContextEntryKind.Plan);

        var session = await dbContext.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(AgentSessionStatus.Completed, session.Status);
        Assert.Equal(taskId, session.TaskId);
        Assert.NotNull(session.ClaimedByWorkerId);

        // Only the capture increment on top of the two the approval caused.
        Assert.Equal(revisionBefore + 3, await CurrentRevisionAsync(project.Id));

        // No later role started on its own: Planner is still the only session that exists.
        var sessions = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(candidate => candidate.TaskId == taskId)
            .ToListAsync();
        Assert.Single(sessions);
        Assert.Equal(AgentSessionRole.Planner, sessions[0].Role);

        // What exists instead is a proposal awaiting a human. Sprint 09 turns the assertion above
        // from "nothing happened" into "consent is pending": the next step is recorded, visible and
        // inert until someone approves it. Staging it moved no revision, which is why the +3 above
        // is still exact.
        var handoff = await dbContext.SessionHandoffs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.TaskId == taskId);
        Assert.Equal(SessionHandoffStatus.Pending, handoff.Status);
        Assert.Equal(SessionHandoffKind.NextRole, handoff.Kind);
        Assert.Equal(AgentSessionRole.Implementer, handoff.ProposedRole);
        Assert.Equal(sessionId, handoff.SourceSessionId);
        Assert.Null(handoff.CreatedSessionId);
        Assert.Null(handoff.DecidedUtc);

        // The conversation displays the result but was never consulted to authorize execution.
        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        Assert.Equal(ConversationStatus.Approved, conversation.Status);
        Assert.Equal(taskId, conversation.ApprovedTaskId);
        Assert.Equal(sessionId, conversation.ApprovedSessionId);
    }

    [Fact]
    public async Task Polling_again_after_completion_finds_no_work_and_captures_no_second_result()
    {
        var project = await SeedProjectAsync($"Conversational Replay {Guid.NewGuid():N}");
        var (_, _, sessionId) = await DescribeAndApproveAsync(project, $"Plan the rollout for {project.Name}");

        var configuration = BuildConfiguration(project.Id, $"workstation-replay-{Guid.NewGuid():N}"[..40]);

        var first = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));
        Assert.Equal(WorkerPollOutcome.Executed, first);

        var revisionAfterFirst = await CurrentRevisionAsync(project.Id);

        // The completed session is no longer Started, so it is never claimable again — the same
        // replay rejection Sprint 07 already enforces, reached from a conversational dispatch.
        var second = await WithFakeAdapterModeAsync("nonzero", () => PollOnceAsync(configuration));
        Assert.Equal(WorkerPollOutcome.Idle, second);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(4, await dbContext.ContextEntries.CountAsync(entry => entry.SourceSessionId == sessionId));
        Assert.Equal(revisionAfterFirst, await CurrentRevisionAsync(project.Id));
    }

    [Fact]
    public async Task A_conversation_that_was_never_approved_offers_the_worker_nothing()
    {
        var project = await SeedProjectAsync($"Conversational Unapproved {Guid.NewGuid():N}");
        var revisionBefore = await CurrentRevisionAsync(project.Id);

        // Describe the work, then stop. This is the state the user sees before deciding.
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, indexHtml) = await afClient.GetPageAsync("/Talk");
        await afClient.PostFormAsync(
            "/Talk?handler=Start",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(indexHtml),
            [new("NewRequest.Request", $"Plan something for {project.Name}")]);

        var configuration = BuildConfiguration(project.Id, $"workstation-unapproved-{Guid.NewGuid():N}"[..40]);
        var outcome = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Idle, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(0, await dbContext.Tasks.CountAsync(task => task.ProjectId == project.Id));
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(project.Id));
    }

    /// <summary>
    /// Drives the real Talk pages: describe the work, then approve it. Returns the identifiers only
    /// so the assertions can find the rows — they are never given to the worker.
    /// </summary>
    private async Task<(Guid ConversationId, Guid TaskId, Guid SessionId)> DescribeAndApproveAsync(
        FamiliarProject project,
        string request)
    {
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, indexHtml) = await afClient.GetPageAsync("/Talk");
        var started = await afClient.PostFormAsync(
            "/Talk?handler=Start",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(indexHtml),
            [new("NewRequest.Request", request)]);

        var detailsUrl = started.Headers.Location!.ToString();
        var conversationId = Guid.Parse(detailsUrl.Split('/')[^1]);

        // Before approval there is nothing to run.
        Assert.Equal(0, await CountTasksAsync(project.Id));

        var (_, detailsHtml) = await afClient.GetPageAsync(detailsUrl);
        var approved = await afClient.PostFormAsync(
            $"{detailsUrl}?handler=Approve",
            AntiforgeryHttpClient.ExtractAntiforgeryToken(detailsHtml),
            [new("ActionConcurrencyToken", (await CurrentTokenAsync(conversationId)).ToString())]);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, approved.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);

        Assert.Equal(ConversationStatus.Approved, conversation.Status);
        return (conversationId, conversation.ApprovedTaskId!.Value, conversation.ApprovedSessionId!.Value);
    }

    private async Task<WorkerPollOutcome> PollOnceAsync(WorkerConfiguration configuration)
    {
        using var httpClient = factory.CreateClient();
        var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), TextWriter.Null);
        var loop = new WorkerLoop(httpClient, engine, configuration, TextWriter.Null, TimeProvider.System);

        return await loop.PollOnceAsync(CancellationToken.None);
    }

    private static WorkerConfiguration BuildConfiguration(Guid projectId, string workerKey) => new(
        new Uri("http://localhost/"),
        workerKey,
        workerKey,
        ["Planner"],
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

    private static string ResolveExecutablePath(string projectName)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{projectName}.exe" : projectName;
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Expected the built executable '{fileName}' next to the test assembly.",
                path);
        }

        return path;
    }

    private async Task<Guid> CurrentTokenAsync(Guid conversationId)
    {
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
}
