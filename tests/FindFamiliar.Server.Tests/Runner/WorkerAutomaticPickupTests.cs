using FindFamiliar.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Runner;

/// <summary>
/// The Sprint 07 definition of done, proved end to end without a live provider: a Started session
/// is discovered, claimed and executed by the real <see cref="WorkerLoop"/> through the real
/// machine API and the real, separately-built <c>FindFamiliar.FakeAdapter</c> child process — with
/// no task or session identifier supplied to the worker by hand.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class WorkerAutomaticPickupTests(FindFamiliarWebApplicationFactory factory)
{
    private static readonly string FakeAdapterPath = ResolveExecutablePath("FindFamiliar.FakeAdapter");

    [Fact]
    public async Task Started_session_is_discovered_claimed_and_executed_without_manual_identifiers()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var revisionBefore = await CurrentRevisionAsync(project.Id);

        // The worker is configured with a project mapping only — never a task or session ID.
        var configuration = BuildConfiguration(project.Id, "workstation-auto-01");

        var outcome = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Executed, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // Exactly one atomic result: the four ADR-0003 entries, once.
        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync();
        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, e => e.Kind == ContextEntryKind.Plan);
        Assert.All(entries, e => Assert.Equal(project.Id, e.ProjectId));

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Completed, refreshedSession.Status);
        Assert.Equal(task.Id, refreshedSession.TaskId);

        // The claim is recorded against the worker that executed it.
        var worker = await dbContext.Workers.SingleAsync(w => w.WorkerKey == "workstation-auto-01");
        Assert.Equal(worker.Id, refreshedSession.ClaimedByWorkerId);
        Assert.NotNull(worker.LastClaimUtc);

        // Context revision increments exactly once for the capture.
        Assert.Equal(revisionBefore + 1, await CurrentRevisionAsync(project.Id));
    }

    [Fact]
    public async Task Polling_again_after_completion_finds_no_work_and_never_relaunches_the_adapter()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var configuration = BuildConfiguration(project.Id, "workstation-auto-02");

        var first = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));
        Assert.Equal(WorkerPollOutcome.Executed, first);

        var revisionAfterFirst = await CurrentRevisionAsync(project.Id);

        // A completed session is no longer Started, so it is never claimable again. This is the
        // same replay rejection ADR-0003/ADR-0006 already enforce, reached through the claim path:
        // the adapter is never launched a second time because no claim is ever granted.
        var second = await WithFakeAdapterModeAsync("nonzero", () => PollOnceAsync(configuration));
        Assert.Equal(WorkerPollOutcome.Idle, second);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(4, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
        Assert.Equal(
            AgentSessionStatus.Completed,
            (await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id)).Status);
        Assert.Equal(revisionAfterFirst, await CurrentRevisionAsync(project.Id));
    }

    [Fact]
    public async Task Two_workers_polling_the_same_session_produce_exactly_one_result()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var revisionBefore = await CurrentRevisionAsync(project.Id);

        var first = BuildConfiguration(project.Id, "workstation-race-a");
        var second = BuildConfiguration(project.Id, "workstation-race-b");

        var outcomes = await WithFakeAdapterModeAsync("success", async () =>
        {
            var a = await PollOnceAsync(first);
            var b = await PollOnceAsync(second);
            return (a, b);
        });

        // The first worker executes and completes the session; the second finds nothing.
        Assert.Equal(WorkerPollOutcome.Executed, outcomes.Item1);
        Assert.Equal(WorkerPollOutcome.Idle, outcomes.Item2);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(4, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
        Assert.Equal(revisionBefore + 1, await CurrentRevisionAsync(project.Id));
    }

    [Fact]
    public async Task Worker_without_a_mapping_for_the_project_never_receives_the_session()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        // Mapped to some other project entirely.
        var configuration = BuildConfiguration(Guid.NewGuid(), "workstation-unmapped");

        var outcome = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Idle, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var refreshed = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Started, refreshed.Status);
        Assert.Null(refreshed.ClaimedByWorkerId);
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
        Assert.NotEqual(project.Id, Guid.Empty);
    }

    [Fact]
    public async Task Worker_without_the_required_capability_never_receives_the_session()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Implementer);
        var configuration = BuildConfiguration(project.Id, "workstation-planner-only") with
        {
            Capabilities = ["Planner"]
        };

        var outcome = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Idle, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Disabled_worker_is_refused_and_executes_nothing()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var configuration = BuildConfiguration(project.Id, "workstation-disabled");

        // Register first, then have an administrator disable it.
        await PollOnceAsync(configuration);

        using (var disableScope = factory.Services.CreateScope())
        {
            var dbContext = disableScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            var worker = await dbContext.Workers.SingleAsync(w => w.WorkerKey == "workstation-disabled");
            worker.Enabled = false;

            // Undo the claim the registration poll may have taken, so this test observes the
            // disabled path rather than an already-executed session.
            var claimed = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
            claimed.Status = AgentSessionStatus.Started;
            claimed.CompletedUtc = null;
            claimed.ClaimedByWorkerId = null;
            claimed.ClaimedUtc = null;
            claimed.ClaimExpiresUtc = null;
            claimed.ClaimId = null;
            await dbContext.SaveChangesAsync();
        }

        var outcome = await WithFakeAdapterModeAsync("success", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Rejected, outcome);

        using var scope = factory.Services.CreateScope();
        var verifyContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var refreshed = await verifyContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Started, refreshed.Status);
        Assert.Null(refreshed.ClaimedByWorkerId);
        Assert.NotEqual(project.Id, Guid.Empty);
    }

    [Fact]
    public async Task Adapter_failure_on_claimed_work_cancels_durably_and_frees_the_task()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var configuration = BuildConfiguration(project.Id, "workstation-failing");

        var outcome = await WithFakeAdapterModeAsync("nonzero", () => PollOnceAsync(configuration));

        Assert.Equal(WorkerPollOutcome.Executed, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // The existing durable-cancellation path is reused verbatim: one Handoff entry, no result.
        var refreshed = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Cancelled, refreshed.Status);

        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(ContextEntryKind.Handoff, entries[0].Kind);
        Assert.NotEqual(project.Id, Guid.Empty);
    }

    [Fact]
    public async Task Long_execution_heartbeats_and_renews_its_claim_before_completion()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var configuration = BuildConfiguration(project.Id, "workstation-maintained") with
        {
            AdapterTimeout = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            LeaseSeconds = 30
        };

        var outcome = await WithFakeAdapterModeAsync("delayed-success", () => PollOnceAsync(configuration));
        Assert.Equal(WorkerPollOutcome.Executed, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var worker = await dbContext.Workers.SingleAsync(candidate => candidate.WorkerKey == "workstation-maintained");
        var completed = await dbContext.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id);

        Assert.True(worker.LastHeartbeatUtc > completed.ClaimedUtc!.Value);
        Assert.True(completed.ClaimExpiresUtc > completed.ClaimedUtc!.Value.AddSeconds(30));
        Assert.Equal(AgentSessionStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task Worker_supplies_the_project_repository_environment_to_the_adapter()
    {
        var (project, _, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var worktree = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", $"worktree-{Guid.NewGuid():N}");
        var configuration = BuildConfiguration(project.Id, "workstation-env") with
        {
            Projects = [new WorkerProjectMapping(project.Id, worktree, worktree, "read-only")]
        };

        var outcome = await WithFakeAdapterModeAsync("echo-env", () => PollOnceAsync(configuration));
        Assert.Equal(WorkerPollOutcome.Executed, outcome);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var rawOutput = await dbContext.ContextEntries
            .Where(e => e.SourceSessionId == session.Id && e.Kind == ContextEntryKind.RawOutput)
            .Select(e => e.Content)
            .SingleAsync();

        // The Familiar credential must still never reach the adapter, even though the worker now
        // sets per-project environment values on the same child process.
        Assert.Contains("token-present:False", rawOutput);
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
        ["Planner", "Implementer", "Reviewer"],
        FindFamiliarWebApplicationFactory.RunnerBridgeTestToken,
        FakeAdapterPath,
        [],
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
        600,
        [new WorkerProjectMapping(projectId, AppContext.BaseDirectory, AppContext.BaseDirectory, "read-only")]);

    private async Task<int> CurrentRevisionAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return await dbContext.Projects.Where(p => p.Id == projectId).Select(p => p.ContextRevision).SingleAsync();
    }

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

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(
        AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Automatic pickup project {Guid.NewGuid():N}",
            Purpose = "Seeded for WorkerAutomaticPickupTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Automatic pickup task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for WorkerAutomaticPickupTests.",
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
}
