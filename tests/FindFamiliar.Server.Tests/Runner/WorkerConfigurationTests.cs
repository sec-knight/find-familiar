using System.Collections;
using System.Text.Json;
using FindFamiliar.Runner;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Runner;

/// <summary>
/// The worker's machine-local configuration loader. The rules that matter here are boundary rules:
/// the repository mapping never leaves this file, the credential never enters it, and a writing mode
/// is reachable only where the operator asked for it and only for the one role that writes.
/// </summary>
public sealed class WorkerConfigurationTests
{
    private const string Token = "worker-config-test-token-not-a-real-secret";

    [Fact]
    public void Valid_configuration_loads_with_its_project_mappings()
    {
        using var directory = new TemporaryConfigDirectory();
        var projectId = Guid.NewGuid();
        var path = directory.WriteConfig(new
        {
            baseUrl = "https://familiar.example.test",
            workerKey = "workstation-01",
            displayName = "Workstation 01",
            capabilities = new[] { "Planner" },
            adapterPath = AbsolutePath("adapter"),
            adapterTimeoutSeconds = 600,
            pollSeconds = 15,
            maxPollSeconds = 120,
            heartbeatSeconds = 60,
            projects = new[]
            {
                new
                {
                    projectId = projectId.ToString(),
                    worktree = AbsolutePath("repo"),
                    allowedRoot = AbsolutePath(""),
                    mode = "read-only"
                }
            }
        });

        var configuration = WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null);

        Assert.NotNull(configuration);
        Assert.Equal("workstation-01", configuration.WorkerKey);
        Assert.Equal(Token, configuration.FamiliarToken);
        Assert.Equal(TimeSpan.FromSeconds(15), configuration.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.MaxPollInterval);
        Assert.Equal([projectId], configuration.ProjectIds);

        var mapping = configuration.FindProject(projectId);
        Assert.NotNull(mapping);
        Assert.Equal("read-only", mapping.Mode);

        var adapterEnvironment = mapping.ToAdapterEnvironment("Planner");
        Assert.Equal(AbsolutePath("repo"), adapterEnvironment["FAMILIAR_CLAUDE_WORKTREE"]);
        Assert.Equal("read-only", adapterEnvironment["FAMILIAR_CLAUDE_MODE"]);
        // The adapter environment carries repository configuration only — never the credential.
        Assert.DoesNotContain(RunnerArguments.TokenVariable, adapterEnvironment.Keys);
    }

    [Fact]
    public void Configuration_without_the_token_environment_variable_is_rejected()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(ValidConfigObject());

        Assert.Null(WorkerConfiguration.TryLoad(Environment(path, token: null), TextWriter.Null));
    }

    [Fact]
    public void Missing_configuration_path_or_file_is_rejected()
    {
        Assert.Null(WorkerConfiguration.TryLoad(new Hashtable(), TextWriter.Null));

        var missing = AbsolutePath($"does-not-exist-{Guid.NewGuid():N}.json");
        Assert.Null(WorkerConfiguration.TryLoad(Environment(missing, Token), TextWriter.Null));
    }

    [Fact]
    public void An_unknown_mode_is_rejected()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(ValidConfigObject(mode: "write-anywhere"));

        Assert.Null(WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null));
    }

    /// <summary>
    /// Opting a project in to edit-worktree does not make every session a writing one. A Planner is
    /// asked to plan and a Reviewer to review, so neither is granted file writes even here — only the
    /// Implementer, the one role whose job is to change files.
    /// </summary>
    [Theory]
    [InlineData("Planner", "read-only")]
    [InlineData("Reviewer", "read-only")]
    [InlineData("Implementer", "edit-worktree")]
    public void Edit_worktree_mode_applies_to_the_implementer_only(string role, string expectedMode)
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(ValidConfigObject(mode: "edit-worktree"));

        var configuration = WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null);

        var mapping = Assert.Single(configuration!.Projects);
        Assert.Equal(expectedMode, mapping.ResolveMode(role));
        Assert.Equal(expectedMode, mapping.ToAdapterEnvironment(role)["FAMILIAR_CLAUDE_MODE"]);
    }

    /// <summary>A read-only mapping stays read-only for every role, including the Implementer.</summary>
    [Theory]
    [InlineData("Planner")]
    [InlineData("Implementer")]
    [InlineData("Reviewer")]
    public void A_read_only_mapping_never_writes(string role)
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(ValidConfigObject(mode: "read-only"));

        var configuration = WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null);

        var mapping = Assert.Single(configuration!.Projects);
        Assert.Equal("read-only", mapping.ResolveMode(role));
    }

    [Fact]
    public void Configuration_with_no_project_mapping_is_rejected()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(new
        {
            baseUrl = "https://familiar.example.test",
            workerKey = "workstation-01",
            capabilities = new[] { "Planner" },
            adapterPath = AbsolutePath("adapter"),
            projects = Array.Empty<object>()
        });

        Assert.Null(WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null));
    }

    [Fact]
    public void Relative_repository_paths_are_rejected()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(ValidConfigObject(worktree: "relative/repo"));

        Assert.Null(WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null));
    }

    [Fact]
    public void Duplicate_project_mappings_are_rejected()
    {
        using var directory = new TemporaryConfigDirectory();
        var projectId = Guid.NewGuid().ToString();
        var mapping = new
        {
            projectId,
            worktree = AbsolutePath("repo"),
            allowedRoot = AbsolutePath(""),
            mode = "read-only"
        };

        var path = directory.WriteConfig(new
        {
            baseUrl = "https://familiar.example.test",
            workerKey = "workstation-01",
            capabilities = new[] { "Planner" },
            adapterPath = AbsolutePath("adapter"),
            projects = new[] { mapping, mapping }
        });

        Assert.Null(WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null));
    }

    [Fact]
    public void Malformed_configuration_is_rejected_without_echoing_its_contents()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        File.WriteAllText(path, "{ \"workerKey\": \"leaky-secret-value\", ");

        var diagnostics = new StringWriter();
        Assert.Null(WorkerConfiguration.TryLoad(Environment(path, Token), diagnostics));
        Assert.DoesNotContain("leaky-secret-value", diagnostics.ToString());
    }

    [Fact]
    public void Poll_intervals_are_clamped_so_a_worker_cannot_busy_loop()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(new
        {
            baseUrl = "https://familiar.example.test",
            workerKey = "workstation-01",
            capabilities = new[] { "Planner" },
            adapterPath = AbsolutePath("adapter"),
            pollSeconds = 0,
            maxPollSeconds = 1,
            projects = new[]
            {
                new
                {
                    projectId = Guid.NewGuid().ToString(),
                    worktree = AbsolutePath("repo"),
                    allowedRoot = AbsolutePath(""),
                    mode = "read-only"
                }
            }
        });

        var configuration = WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null);

        Assert.NotNull(configuration);
        Assert.True(configuration.PollInterval >= TimeSpan.FromSeconds(WorkerConfiguration.MinPollSeconds));
        Assert.True(configuration.MaxPollInterval >= configuration.PollInterval);
    }

    [Fact]
    public void Lease_defaults_to_longer_than_the_adapter_timeout()
    {
        using var directory = new TemporaryConfigDirectory();
        var path = directory.WriteConfig(new
        {
            baseUrl = "https://familiar.example.test",
            workerKey = "workstation-01",
            capabilities = new[] { "Planner" },
            adapterPath = AbsolutePath("adapter"),
            adapterTimeoutSeconds = 600,
            projects = new[]
            {
                new
                {
                    projectId = Guid.NewGuid().ToString(),
                    worktree = AbsolutePath("repo"),
                    allowedRoot = AbsolutePath(""),
                    mode = "read-only"
                }
            }
        });

        var configuration = WorkerConfiguration.TryLoad(Environment(path, Token), TextWriter.Null);

        Assert.NotNull(configuration);
        // A lease shorter than the run it covers would let a second worker re-claim work that is
        // still executing.
        Assert.True(configuration.LeaseSeconds > configuration.AdapterTimeout.TotalSeconds);
    }

    private static object ValidConfigObject(string mode = "read-only", string? worktree = null) => new
    {
        baseUrl = "https://familiar.example.test",
        workerKey = "workstation-01",
        capabilities = new[] { "Planner" },
        adapterPath = AbsolutePath("adapter"),
        projects = new[]
        {
            new
            {
                projectId = Guid.NewGuid().ToString(),
                worktree = worktree ?? AbsolutePath("repo"),
                allowedRoot = AbsolutePath(""),
                mode
            }
        }
    };

    private static string AbsolutePath(string relative) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", relative));

    private static Hashtable Environment(string? configPath, string? token)
    {
        var environment = new Hashtable();

        if (configPath is not null)
        {
            environment[WorkerConfiguration.ConfigVariable] = configPath;
        }

        if (token is not null)
        {
            environment[RunnerArguments.TokenVariable] = token;
        }

        return environment;
    }

    private sealed class TemporaryConfigDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FindFamiliar.Tests",
            $"worker-config-{Guid.NewGuid():N}");

        public TemporaryConfigDirectory() => Directory.CreateDirectory(Path);

        public string WriteConfig(object configuration)
        {
            var path = System.IO.Path.Combine(Path, "worker.json");
            File.WriteAllText(path, JsonSerializer.Serialize(configuration));
            return path;
        }

        public void Dispose() => TemporaryDirectoryCleanup.Delete(Path);
    }
}
