using System.Net.Http;
using System.Diagnostics;
using FindFamiliar.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Runner;

/// <summary>
/// End-to-end coverage of the real <see cref="RunnerEngine"/> and <see cref="AdapterProcessExecutor"/>
/// against the real machine API (through the shared in-process TestServer) and the real,
/// separately-built <c>FindFamiliar.FakeAdapter</c> executable — launched as a genuine child
/// process, not simulated. The mode env var (<c>FAKE_ADAPTER_MODE</c>) selects fixture behavior;
/// tests in this class run sequentially (the collection disables parallelization) so mutating it
/// around each run is safe.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RunnerProcessEndToEndTests(FindFamiliarWebApplicationFactory factory)
{
    private static readonly string FakeAdapterPath = ResolveExecutablePath("FindFamiliar.FakeAdapter");

    [Fact]
    public async Task Success_round_trip_captures_four_entries_and_completes_the_session()
    {
        var (project, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("success", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.Success, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == session.Id).ToListAsync();

        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.Equal(project.Id, e.ProjectId));
        Assert.Contains(entries, e => e.Kind == ContextEntryKind.Plan);

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Completed, refreshedSession.Status);
    }

    [Fact]
    public async Task Adapter_receives_the_versioned_stdin_document_over_a_real_pipe()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Implementer);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("success", () => RunEngineAsync(arguments));
        Assert.Equal(RunnerExitCode.Success, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var rawOutput = await dbContext.ContextEntries
            .Where(e => e.SourceSessionId == session.Id && e.Kind == ContextEntryKind.RawOutput)
            .Select(e => e.Content)
            .SingleAsync();

        // The fixture echoes the stdin byte count it actually read; a non-zero count proves the
        // runner delivered the real versioned JSON document over the child's stdin pipe.
        Assert.DoesNotContain("Received 0 stdin bytes", rawOutput);
    }

    [Fact]
    public async Task Child_environment_does_not_contain_the_familiar_token()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("echo-env", () => RunEngineAsync(arguments));
        Assert.Equal(RunnerExitCode.Success, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var rawOutput = await dbContext.ContextEntries
            .Where(e => e.SourceSessionId == session.Id && e.Kind == ContextEntryKind.RawOutput)
            .Select(e => e.Content)
            .SingleAsync();

        Assert.Contains("token-present:False", rawOutput);
    }

    [Fact]
    public async Task Bounded_stderr_does_not_deadlock_and_stdout_still_captured()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("stderr-noise", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.Success, exitCode);
    }

    [Fact]
    public async Task Timeout_kills_the_adapter_and_cancels_durably_with_one_handoff()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(2));

        var exitCode = await WithFakeAdapterModeAsync("timeout", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task External_cancellation_kills_the_adapter_process_before_returning()
    {
        var directory = Directory.CreateTempSubdirectory("familiar-runner-cancel").FullName;
        var pidFile = Path.Combine(directory, "adapter.pid");

        try
        {
            using var cancellation = new CancellationTokenSource();
            var execution = new AdapterProcessExecutor().RunAsync(
                FakeAdapterPath,
                [],
                "{}",
                TimeSpan.FromMinutes(5),
                cancellation.Token,
                environmentOverrides: new Dictionary<string, string>
                {
                    ["FAKE_ADAPTER_MODE"] = "timeout",
                    ["FAKE_ADAPTER_PID_FILE"] = pidFile
                });

            using var startedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var processId = 0;
            while (!File.Exists(pidFile)
                || !int.TryParse(
                    await File.ReadAllTextAsync(pidFile, startedTimeout.Token),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out processId))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), startedTimeout.Token);
            }

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

            Assert.False(IsProcessRunning(processId));
        }
        finally
        {
            TemporaryDirectoryCleanup.Delete(directory);
        }
    }

    [Fact]
    public async Task Adapter_that_never_reads_stdin_still_times_out_and_cancels_durably()
    {
        // Regression coverage: the timeout must bound the stdin write itself, not only the
        // later wait-for-exit. Assignment Markdown can be far larger than a typical OS pipe
        // buffer, so an adapter that never drains stdin would otherwise block the write forever
        // and the process-tree kill would never run.
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(2));

        var exitCode = await WithFakeAdapterModeAsync("stall-stdin", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task Non_zero_exit_cancels_durably_with_one_handoff()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("nonzero", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task Malformed_json_cancels_durably_with_one_handoff()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("malformed", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task Multiple_json_documents_cancels_durably_with_one_handoff()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("multiple-json", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task Missing_result_fields_cancels_durably_with_one_handoff()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("missing-fields", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task Oversized_stdout_cancels_durably_with_one_handoff()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        var exitCode = await WithFakeAdapterModeAsync("oversized", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    [Fact]
    public async Task Nonexistent_adapter_executable_is_a_launch_failure_that_cancels_durably()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = new RunnerArguments(
            new Uri("http://localhost/"),
            task.Id,
            session.Id,
            FindFamiliarWebApplicationFactory.RunnerBridgeTestToken,
            Path.Combine(AppContext.BaseDirectory, "definitely-does-not-exist-adapter"),
            [],
            TimeSpan.FromSeconds(10));

        // Wrapped so a workspace is stated: the run must reach the launch attempt and fail there,
        // rather than being refused earlier for having no workspace, which would assert nothing about
        // launch failure. The fake adapter mode is irrelevant — this executable does not exist.
        var exitCode = await WithFakeAdapterModeAsync("success", () => RunEngineAsync(arguments));

        Assert.Equal(RunnerExitCode.CancelledAfterAdapterFailure, exitCode);
        await AssertDurablyCancelledWithOneHandoffAsync(session.Id);
    }

    /// <summary>
    /// The other half of the 2026-08-07 README incident, asserted end to end.
    ///
    /// The explicit invocation path supplied no project mapping, so the adapter inherited whatever
    /// workspace variables the operator had exported — which is how a Reviewer came to inspect the
    /// live checkout while the Implementer worked in the linked worktree. A session that cannot say
    /// where it is standing must not start, and must not reach the adapter at all.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_resolvable_workspace_is_refused_before_the_adapter_runs()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Reviewer);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        // Deliberately not wrapped: no workspace is exported.
        var exitCode = await RunEngineAsync(arguments);

        Assert.Equal(RunnerExitCode.AssignmentInvalid, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // Refused before anything ran, so the session is untouched: no result, no durable cancellation.
        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(AgentSessionStatus.Started, refreshedSession.Status);
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
    }

    [Fact]
    public async Task Ambiguous_result_submission_failure_does_not_auto_cancel()
    {
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);
        var arguments = BuildArguments(task.Id, session.Id, TimeSpan.FromSeconds(15));

        using var innerHandler = factory.Server.CreateHandler();
        using var throwingHandler = new ThrowOnResultRequestHandler(innerHandler);
        using var httpClient = new HttpClient(throwingHandler) { BaseAddress = factory.Server.BaseAddress };
        var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), TextWriter.Null);

        var exitCode = await WithFakeAdapterModeAsync("success", () => engine.RunAsync(arguments, CancellationToken.None));

        Assert.Equal(RunnerExitCode.ResultSubmissionAmbiguous, exitCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == session.Id);

        // Ambiguity must never trigger an automatic cancellation — capture may already have
        // committed on the server even though the runner never saw the response.
        Assert.Equal(AgentSessionStatus.Started, refreshedSession.Status);
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync(e => e.SourceSessionId == session.Id));
    }

    private async Task AssertDurablyCancelledWithOneHandoffAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var refreshedSession = await dbContext.AgentSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Equal(AgentSessionStatus.Cancelled, refreshedSession.Status);

        var entries = await dbContext.ContextEntries.Where(e => e.SourceSessionId == sessionId).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(ContextEntryKind.Handoff, entries[0].Kind);
    }

    private async Task<RunnerExitCode> RunEngineAsync(RunnerArguments arguments)
    {
        using var httpClient = factory.CreateClient();
        var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), TextWriter.Null);
        return await engine.RunAsync(arguments, CancellationToken.None);
    }

    /// <summary>
    /// Selects the fake adapter's behaviour, and states a workspace for the run.
    ///
    /// The workspace variables are not decoration. Since Slice 0 the runner refuses to launch a
    /// session whose authorized workspace it cannot name, because a session that inherited whatever
    /// the operator happened to export is how an Implementer and a Reviewer came to stand in
    /// different trees. An explicit invocation must therefore export them, and this harness exports
    /// them exactly as a real one would — these tests cover adapter process mechanics, and the
    /// workspace contract itself is asserted in <c>WorkspaceContractTests</c>.
    /// </summary>
    private static async Task<T> WithFakeAdapterModeAsync<T>(string mode, Func<Task<T>> action)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["FAKE_ADAPTER_MODE"] = mode,
            ["FAMILIAR_CLAUDE_WORKTREE"] = WorkspaceRoot,
            ["FAMILIAR_CLAUDE_ALLOWED_ROOT"] = WorkspaceRoot,
            ["FAMILIAR_CLAUDE_MODE"] = "read-only"
        };

        var previous = variables.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            return await action();
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>
    /// An absolute path is all the contract needs; whether it exists is the adapter's question, and
    /// the fake adapter does not touch the filesystem.
    /// </summary>
    private static readonly string WorkspaceRoot =
        Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", "runner-workspace");

    private static RunnerArguments BuildArguments(Guid taskId, Guid sessionId, TimeSpan timeout) => new(
        new Uri("http://localhost/"),
        taskId,
        sessionId,
        FindFamiliarWebApplicationFactory.RunnerBridgeTestToken,
        FakeAdapterPath,
        [],
        timeout);

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

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for RunnerProcessEndToEndTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for RunnerProcessEndToEndTests.",
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

    private sealed class ThrowOnResultRequestHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && request.RequestUri.AbsolutePath.EndsWith("/result", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Simulated transport failure after the result request was sent.");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
