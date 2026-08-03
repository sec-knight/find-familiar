using FindFamiliar.Adapter.Claude;
using FindFamiliar.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.ClaudeAdapter;

/// <summary>
/// The whole provider chain as separate real processes: the real <see cref="RunnerEngine"/>
/// launches the real compiled Claude adapter, which launches the deterministic fake Claude
/// runtime, and the result is captured through the real machine API against the collection's
/// isolated temporary database. No live provider, credential, or network is involved.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ClaudeAdapterEndToEndTests(FindFamiliarWebApplicationFactory factory)
{
    private static readonly string AdapterPath = ResolveExecutablePath("FindFamiliar.Adapter.Claude");
    private static readonly string FakeClaudePath = ResolveExecutablePath("FindFamiliar.FakeClaudeRuntime");

    [Fact]
    public async Task Runner_through_claude_adapter_captures_exactly_one_atomic_result()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var revisionBefore = await CurrentRevisionAsync(project.Id);

        var exitCode = await WithClaudeEnvironmentAsync(
            "success",
            () => RunEngineAsync(task.Id, session.Id, TimeSpan.FromSeconds(60)));

        Assert.Equal(RunnerExitCode.Success, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var entries = await dbContext.ContextEntries
            .Where(e => e.SourceSessionId == session.Id)
            .ToListAsync();

        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry => Assert.Equal(project.Id, entry.ProjectId));
        Assert.Contains(entries, entry => entry.Kind == ContextEntryKind.Plan);

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Completed, refreshedSession.Status);

        var refreshedProject = await dbContext.Projects.SingleAsync(p => p.Id == project.Id);
        Assert.Equal(revisionBefore + 1, refreshedProject.ContextRevision);

        // The captured output is the fake fixture's, proving the chain ran end to end without a
        // live provider.
        var rawOutput = entries.Single(entry => entry.Kind == ContextEntryKind.RawOutput).Content;
        Assert.Contains("[fake-claude]", rawOutput);
    }

    [Fact]
    public async Task Replaying_a_terminal_session_writes_nothing_further()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Reviewer);

        var first = await WithClaudeEnvironmentAsync(
            "success",
            () => RunEngineAsync(task.Id, session.Id, TimeSpan.FromSeconds(60)));
        Assert.Equal(RunnerExitCode.Success, first);

        int entriesAfterFirst;
        int revisionAfterFirst;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            entriesAfterFirst = await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id);
            revisionAfterFirst = (await dbContext.Projects.SingleAsync(p => p.Id == project.Id)).ContextRevision;
        }

        var second = await WithClaudeEnvironmentAsync(
            "success",
            () => RunEngineAsync(task.Id, session.Id, TimeSpan.FromSeconds(60)));

        // The session is no longer Started, so the server must refuse the replay outright.
        Assert.NotEqual(RunnerExitCode.Success, second);

        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        Assert.Equal(entriesAfterFirst, await verifyContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
        Assert.Equal(revisionAfterFirst, (await verifyContext.Projects.SingleAsync(p => p.Id == project.Id)).ContextRevision);
    }

    [Fact]
    public async Task Provider_failure_cancels_the_session_durably_without_capturing_a_result()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        var exitCode = await WithClaudeEnvironmentAsync(
            "is-error",
            () => RunEngineAsync(task.Id, session.Id, TimeSpan.FromSeconds(60)));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Cancelled, refreshedSession.Status);

        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(ContextEntryKind.Handoff, entries[0].Kind);
    }

    private async Task<RunnerExitCode> RunEngineAsync(Guid taskId, Guid sessionId, TimeSpan timeout)
    {
        using var httpClient = factory.CreateClient();
        var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), TextWriter.Null);

        var arguments = new RunnerArguments(
            new Uri("http://localhost/"),
            taskId,
            sessionId,
            FindFamiliarWebApplicationFactory.RunnerBridgeTestToken,
            AdapterPath,
            [],
            timeout);

        return await engine.RunAsync(arguments, CancellationToken.None);
    }

    /// <summary>
    /// Sets the adapter's administrator configuration on this process so the adapter child
    /// inherits it, exactly as a real operator's machine-level configuration would be inherited.
    /// </summary>
    private static async Task<T> WithClaudeEnvironmentAsync<T>(string fakeClaudeMode, Func<Task<T>> action)
    {
        var root = Directory.CreateTempSubdirectory("familiar-claude-e2e");
        var worktree = Directory.CreateDirectory(Path.Combine(root.FullName, "repo"));

        var values = new Dictionary<string, string?>
        {
            ["FAKE_CLAUDE_MODE"] = fakeClaudeMode,
            [ClaudeAdapterConfiguration.RuntimePathVariable] = FakeClaudePath,
            [ClaudeAdapterConfiguration.WorktreeVariable] = worktree.FullName,
            [ClaudeAdapterConfiguration.AllowedRootVariable] = root.FullName,
            [ClaudeAdapterConfiguration.ModeVariable] = "read-only",
            [ClaudeAdapterConfiguration.TimeoutVariable] = "45"
        };

        var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

        foreach (var (key, value) in values)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            return await action();
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            try
            {
                root.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<int> CurrentRevisionAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        return (await dbContext.Projects.SingleAsync(p => p.Id == projectId)).ContextRevision;
    }

    private static string ResolveExecutablePath(string projectName)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{projectName}.exe" : projectName;
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Expected the built executable '{fileName}' next to the test assembly " +
                $"(add '{projectName}' as a ProjectReference so its apphost is copied to output).",
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
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for ClaudeAdapterEndToEndTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for ClaudeAdapterEndToEndTests.",
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
