using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FindFamiliar.Adapter.Claude;
using FindFamiliar.Runner;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.ClaudeAdapter;

/// <summary>
/// Spawns the real compiled Claude adapter as a genuine child OS process, pointed at the
/// deterministic fake Claude runtime — never the paid live provider. Covers only what a real
/// process can prove: direct argument delivery with no shell, environment scrubbing, working
/// directory, bounded pipes, timeout with process-tree termination, and stable exit categories.
/// </summary>
public sealed class ClaudeAdapterProcessTests
{
    private static readonly string AdapterPath = ResolveExecutablePath("FindFamiliar.Adapter.Claude");
    private static readonly string FakeClaudePath = ResolveExecutablePath("FindFamiliar.FakeClaudeRuntime");

    [Fact]
    public async Task Valid_invocation_produces_one_bounded_protocol_result()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync("success", worktree, ValidInvocationJson());

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);

        var result = JsonSerializer.Deserialize<AdapterResult>(run.Stdout, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        Assert.Equal(RunnerProtocol.ContractVersion, result.ContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    [Fact]
    public async Task Arguments_reach_claude_as_distinct_argv_elements()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync("echo-argv", worktree, ValidInvocationJson());

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
        // The fixture joins argv with '|', so seeing each flag and its value as separate
        // elements proves no shell parsing or whitespace re-splitting happened in the chain.
        Assert.Contains("-p|--output-format|json", run.Stdout);
        Assert.Contains("--permission-mode|plan", run.Stdout);
    }

    [Fact]
    public async Task Shell_metacharacters_in_the_assignment_are_inert_data()
    {
        using var worktree = new TemporaryWorktree();
        var canary = Path.Combine(worktree.Path, "pwned.txt");

        var hostile = $"; touch {canary}; echo $(whoami) `id` && rm -rf / | cat > {canary}";
        var invocation = ValidInvocationJson(assignment: hostile);

        var run = await RunAdapterAsync("echo-stdin", worktree, invocation);

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
        // The metacharacters must arrive verbatim as prompt text...
        Assert.Contains("rm -rf", run.Stdout);
        // ...and nothing may have executed them.
        Assert.False(File.Exists(canary), "Shell metacharacters in the assignment were executed.");
    }

    [Fact]
    public async Task Familiar_runner_token_never_reaches_the_claude_child()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync(
            "echo-env",
            worktree,
            ValidInvocationJson(),
            extraEnvironment: new Dictionary<string, string>
            {
                [RunnerArguments.TokenVariable] = "super-secret-token-value"
            });

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
        Assert.Contains("token-present:False", run.Stdout);
        Assert.DoesNotContain("super-secret-token-value", run.Stdout);
        Assert.DoesNotContain("super-secret-token-value", run.Stderr);
    }

    [Fact]
    public async Task Claude_runs_with_the_configured_worktree_as_its_working_directory()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync("echo-cwd", worktree, ValidInvocationJson());

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
        Assert.Contains(Path.GetFileName(worktree.Path), run.Stdout);
    }

    [Fact]
    public async Task Read_only_run_does_not_mutate_the_worktree()
    {
        using var worktree = new TemporaryWorktree();
        var before = worktree.Snapshot();

        var run = await RunAdapterAsync("success", worktree, ValidInvocationJson());

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
        Assert.Equal(before, worktree.Snapshot());
    }

    [Theory]
    [InlineData("nonzero", ClaudeAdapterExitCode.RuntimeNonZeroExit)]
    [InlineData("malformed", ClaudeAdapterExitCode.RuntimeOutputInvalid)]
    [InlineData("is-error", ClaudeAdapterExitCode.RuntimeOutputInvalid)]
    [InlineData("oversized", ClaudeAdapterExitCode.RuntimeOutputInvalid)]
    [InlineData("permission-denial", ClaudeAdapterExitCode.PermissionDenialReported)]
    public async Task Provider_failures_map_to_stable_categories_with_no_partial_stdout(string mode, ClaudeAdapterExitCode expected)
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync(mode, worktree, ValidInvocationJson());

        Assert.Equal((int)expected, run.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout), "A failed run must not write a partial result document.");
    }

    [Fact]
    public async Task Stderr_flood_does_not_deadlock_and_provider_stderr_is_never_echoed()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync("stderr-noise", worktree, ValidInvocationJson());

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
        Assert.DoesNotContain("fake-claude-secret-marker", run.Stderr);
        Assert.DoesNotContain("fake-claude-secret-marker", run.Stdout);
    }

    [Fact]
    public async Task Timeout_terminates_the_provider_process_tree()
    {
        using var worktree = new TemporaryWorktree();

        var stopwatch = Stopwatch.StartNew();
        var run = await RunAdapterAsync(
            "timeout",
            worktree,
            ValidInvocationJson(),
            extraEnvironment: new Dictionary<string, string>
            {
                [ClaudeAdapterConfiguration.TimeoutVariable] = "5"
            });
        stopwatch.Stop();

        Assert.Equal((int)ClaudeAdapterExitCode.RuntimeTimeout, run.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), "The adapter should have enforced its own timeout.");
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout));
    }

    [Fact]
    public async Task Worktree_outside_the_allowed_root_is_rejected_before_launching_claude()
    {
        using var worktree = new TemporaryWorktree();
        using var outside = new TemporaryWorktree();

        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment => environment[ClaudeAdapterConfiguration.WorktreeVariable] = outside.Path);

        Assert.Equal((int)ClaudeAdapterExitCode.WorktreeRejected, run.ExitCode);
    }

    [Fact]
    public async Task Sibling_prefix_directory_does_not_satisfy_the_allowed_root()
    {
        using var worktree = new TemporaryWorktree();

        // "<root>-evil" shares a textual prefix with the allowed root but is not inside it.
        var sibling = worktree.Path + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var run = await RunAdapterAsync(
                "success",
                worktree,
                ValidInvocationJson(),
                overrideEnvironment: environment =>
                {
                    environment[ClaudeAdapterConfiguration.AllowedRootVariable] = worktree.Path;
                    environment[ClaudeAdapterConfiguration.WorktreeVariable] = sibling;
                });

            Assert.Equal((int)ClaudeAdapterExitCode.WorktreeRejected, run.ExitCode);
        }
        finally
        {
            TemporaryDirectoryCleanup.Delete(sibling);
        }
    }

    [Fact]
    public async Task Relative_worktree_configuration_is_rejected()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment => environment[ClaudeAdapterConfiguration.WorktreeVariable] = "relative/worktree");

        Assert.Equal((int)ClaudeAdapterExitCode.ConfigurationInvalid, run.ExitCode);
    }

    [Fact]
    public async Task Missing_runtime_path_configuration_is_rejected()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment => environment.Remove(ClaudeAdapterConfiguration.RuntimePathVariable));

        Assert.Equal((int)ClaudeAdapterExitCode.ConfigurationInvalid, run.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ truncated")]
    [InlineData("""{"contractVersion":2,"taskId":"11111111-1111-1111-1111-111111111111","sessionId":"22222222-2222-2222-2222-222222222222","role":"Planner","rolePrompt":"p","assignmentMarkdown":"a"}""")]
    public async Task Invalid_stdin_is_rejected_without_launching_claude(string stdin)
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync("success", worktree, stdin);

        Assert.Equal((int)ClaudeAdapterExitCode.InvocationInvalid, run.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout));
    }

    [Fact]
    public async Task Oversized_stdin_is_rejected_by_the_real_process()
    {
        using var worktree = new TemporaryWorktree();

        // Bounded at the stream, not after buffering: the adapter must refuse this without
        // reading it all into memory.
        var oversized = new string('x', InvocationValidator.MaxStdinBytes + 4096);

        var run = await RunAdapterAsync("success", worktree, oversized);

        Assert.Equal((int)ClaudeAdapterExitCode.InvocationInvalid, run.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout));
    }

    [Fact]
    public async Task Extra_arguments_cannot_widen_the_read_only_tool_boundary()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync(
            "echo-argv",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment =>
                environment[ClaudeAdapterConfiguration.ExtraArgumentsVariable] = """["--tools","Bash"]""");

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);

        // The CLI resolves repeated flags last-wins, so the policy's --tools must appear after the
        // operator extra rather than before it — otherwise ["--tools","Bash"] would win.
        var argv = run.Stdout;
        var extraIndex = argv.IndexOf("--tools|Bash", StringComparison.Ordinal);
        var policyIndex = argv.LastIndexOf("--tools|", StringComparison.Ordinal);

        Assert.True(extraIndex >= 0, "The operator extra should still be passed through.");
        Assert.True(policyIndex > extraIndex, "The read-only policy flags must come after operator extras.");
    }

    [Fact]
    public async Task A_path_with_spaces_and_quotes_arrives_as_one_exact_argv_element()
    {
        using var worktree = new TemporaryWorktree("repo with spaces and 'quotes'");

        var run = await RunAdapterAsync(
            "echo-argv",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment =>
                environment[ClaudeAdapterConfiguration.ExtraArgumentsVariable] =
                    JsonSerializer.Serialize(new[] { "--model", worktree.Path }));

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);

        var result = JsonSerializer.Deserialize<AdapterResult>(run.Stdout, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // The fixture separates argv elements with '|', so the whole path — spaces and quotes
        // included — appearing between two separators proves it was never re-split.
        Assert.Contains($"--model|{worktree.Path}|", result.RawOutput);
    }

    [Fact]
    public async Task Non_ascii_assignment_text_survives_the_stdin_round_trip()
    {
        using var worktree = new TemporaryWorktree();

        // The adapter decodes its stdin as UTF-8, so every writer in the chain must encode as
        // UTF-8. On Windows an unset StandardInputEncoding defaults to the console's OEM
        // codepage, which would silently mangle these characters.
        const string nonAscii = "Curly \u201Cquotes\u201D, an em-dash \u2014, accents \u00E9\u00E8, CJK \u65E5\u672C\u8A9E, emoji \U0001F9EA";

        var run = await RunAdapterAsync("echo-stdin", worktree, ValidInvocationJson(assignment: nonAscii));

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);

        var result = JsonSerializer.Deserialize<AdapterResult>(run.Stdout, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(nonAscii, result.RawOutput);
    }

    [Fact]
    public async Task Adapter_rejects_any_command_line_argument()
    {
        using var worktree = new TemporaryWorktree();

        var run = await RunAdapterAsync("success", worktree, ValidInvocationJson(), adapterArguments: ["--unexpected"]);

        Assert.Equal((int)ClaudeAdapterExitCode.ConfigurationInvalid, run.ExitCode);
    }

    [Fact]
    public async Task Edit_mode_rejects_a_target_that_is_not_a_clean_worktree()
    {
        using var worktree = new TemporaryWorktree();

        // A plain directory is not a git worktree at all.
        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment => environment[ClaudeAdapterConfiguration.ModeVariable] = "edit-worktree");

        Assert.Equal((int)ClaudeAdapterExitCode.WorktreeNotClean, run.ExitCode);
    }

    [Fact]
    public async Task Edit_mode_rejects_a_dirty_git_worktree()
    {
        using var worktree = new TemporaryWorktree();
        InitializeGitRepository(worktree.Path);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "uncommitted.txt"), "dirty");

        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment => environment[ClaudeAdapterConfiguration.ModeVariable] = "edit-worktree");

        Assert.Equal((int)ClaudeAdapterExitCode.WorktreeNotClean, run.ExitCode);
    }

    [Fact]
    public async Task Edit_mode_rejects_a_primary_checkout_even_when_clean()
    {
        using var worktree = new TemporaryWorktree();
        InitializeGitRepository(worktree.Path);

        // A clean primary checkout is not disposable: editing it would touch the developer's
        // real working copy.
        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment => environment[ClaudeAdapterConfiguration.ModeVariable] = "edit-worktree");

        Assert.Equal((int)ClaudeAdapterExitCode.WorktreeNotClean, run.ExitCode);
    }

    [Fact]
    public async Task Edit_mode_rejects_a_subdirectory_of_a_clean_repository()
    {
        using var worktree = new TemporaryWorktree();
        InitializeGitRepository(worktree.Path);

        var nested = Path.Combine(worktree.Path, "src");
        Directory.CreateDirectory(nested);

        // git answers --is-inside-work-tree and status --porcelain identically here, so this is
        // the case a naive check would wrongly accept.
        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment =>
            {
                environment[ClaudeAdapterConfiguration.ModeVariable] = "edit-worktree";
                environment[ClaudeAdapterConfiguration.WorktreeVariable] = nested;
            });

        Assert.Equal((int)ClaudeAdapterExitCode.WorktreeNotClean, run.ExitCode);
    }

    [Fact]
    public async Task Edit_mode_accepts_a_clean_linked_disposable_worktree()
    {
        using var worktree = new TemporaryWorktree();
        InitializeGitRepository(worktree.Path);

        var linked = Path.Combine(worktree.RootPath, "disposable");
        RunGit(worktree.Path, "worktree", "add", linked);

        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment =>
            {
                environment[ClaudeAdapterConfiguration.ModeVariable] = "edit-worktree";
                environment[ClaudeAdapterConfiguration.WorktreeVariable] = linked;
            });

        Assert.Equal((int)ClaudeAdapterExitCode.Success, run.ExitCode);
    }

    [Fact]
    public async Task Edit_mode_rejects_a_dirty_linked_worktree()
    {
        using var worktree = new TemporaryWorktree();
        InitializeGitRepository(worktree.Path);

        var linked = Path.Combine(worktree.RootPath, "disposable-dirty");
        RunGit(worktree.Path, "worktree", "add", linked);
        await File.WriteAllTextAsync(Path.Combine(linked, "uncommitted.txt"), "dirty");

        var run = await RunAdapterAsync(
            "success",
            worktree,
            ValidInvocationJson(),
            overrideEnvironment: environment =>
            {
                environment[ClaudeAdapterConfiguration.ModeVariable] = "edit-worktree";
                environment[ClaudeAdapterConfiguration.WorktreeVariable] = linked;
            });

        Assert.Equal((int)ClaudeAdapterExitCode.WorktreeNotClean, run.ExitCode);
    }

    // ---------- harness ----------

    private static string ValidInvocationJson(string? assignment = null) => JsonSerializer.Serialize(
        new AdapterInvocation(
            RunnerProtocol.ContractVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Planner",
            "You are acting as the Planner.",
            assignment ?? "# Assignment\n\nSummarize the repository layout."),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAdapterAsync(
        string fakeClaudeMode,
        TemporaryWorktree worktree,
        string stdin,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        Action<IDictionary<string, string?>>? overrideEnvironment = null,
        IReadOnlyList<string>? adapterArguments = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AdapterPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in adapterArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["FAKE_CLAUDE_MODE"] = fakeClaudeMode;
        startInfo.Environment[ClaudeAdapterConfiguration.RuntimePathVariable] = FakeClaudePath;
        startInfo.Environment[ClaudeAdapterConfiguration.WorktreeVariable] = worktree.Path;
        startInfo.Environment[ClaudeAdapterConfiguration.AllowedRootVariable] = worktree.RootPath;
        startInfo.Environment[ClaudeAdapterConfiguration.ModeVariable] = "read-only";
        startInfo.Environment[ClaudeAdapterConfiguration.TimeoutVariable] = "30";

        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        overrideEnvironment?.Invoke(startInfo.Environment);

        using var process = Process.Start(startInfo)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.StandardInput.WriteAsync(stdin);
        }
        catch (IOException)
        {
            // Expected for the oversized case: the adapter stops reading and exits once its cap
            // is hit, so the rest of the write lands on a closed pipe. The exit code is the
            // assertion, not whether every byte was accepted.
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void InitializeGitRepository(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "fixture@example.invalid");
        RunGit(path, "config", "user.name", "Fixture");
        RunGit(path, "add", ".");
        RunGit(path, "commit", "-m", "fixture baseline", "--allow-empty");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
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

    /// <summary>A disposable allowed-root/worktree pair on the real filesystem.</summary>
    private sealed class TemporaryWorktree : IDisposable
    {
        private readonly DirectoryInfo _root;

        public TemporaryWorktree(string leafName = "repo")
        {
            _root = Directory.CreateTempSubdirectory("familiar-claude-adapter");
            Path = System.IO.Path.Combine(_root.FullName, leafName);
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "fixture worktree");
        }

        public string RootPath => _root.FullName;

        public string Path { get; }

        public string Snapshot()
        {
            var entries = Directory
                .EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(entry => File.Exists(entry)
                    ? $"{entry}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry)))}"
                    : entry);

            return string.Join('\n', entries);
        }

        public void Dispose() => TemporaryDirectoryCleanup.Delete(_root.FullName);
    }
}
