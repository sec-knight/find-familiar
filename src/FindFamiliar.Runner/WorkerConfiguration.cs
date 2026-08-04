using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FindFamiliar.Runner;

/// <summary>
/// The worker's machine-local administrator configuration, read from a JSON file whose path comes
/// from <see cref="ConfigVariable"/>. This file is the only place repository paths live: they are
/// never sent to Familiar and never committed (see the .gitignore entry and
/// docs/worker.example.json).
///
/// The Familiar bearer credential is deliberately *not* a field here — it comes from
/// <see cref="RunnerArguments.TokenVariable"/> only, so the config file never holds a secret.
/// </summary>
public sealed record WorkerConfiguration(
    Uri BaseUrl,
    string WorkerKey,
    string DisplayName,
    IReadOnlyList<string> Capabilities,
    string FamiliarToken,
    string AdapterPath,
    IReadOnlyList<string> AdapterArguments,
    TimeSpan AdapterTimeout,
    TimeSpan PollInterval,
    TimeSpan MaxPollInterval,
    TimeSpan HeartbeatInterval,
    int LeaseSeconds,
    IReadOnlyList<WorkerProjectMapping> Projects)
{
    public const string ConfigVariable = "FAMILIAR_WORKER_CONFIG";

    public const int MinPollSeconds = 5;
    public const int MaxPollSeconds = 3600;
    public const int DefaultPollSeconds = 15;
    public const int DefaultMaxPollSeconds = 120;
    public const int DefaultHeartbeatSeconds = 60;

    public static WorkerConfiguration? TryLoad(IDictionary environment, TextWriter diagnostics)
    {
        var configPath = environment[ConfigVariable] as string;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            diagnostics.WriteLine($"worker: {ConfigVariable} environment variable is required.");
            return null;
        }

        if (!Path.IsPathFullyQualified(configPath))
        {
            diagnostics.WriteLine($"worker: {ConfigVariable} must be an absolute path.");
            return null;
        }

        if (!File.Exists(configPath))
        {
            diagnostics.WriteLine($"worker: configuration file not found at the configured path.");
            return null;
        }

        WorkerConfigurationFile? file;
        try
        {
            file = JsonSerializer.Deserialize<WorkerConfigurationFile>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The path can name a file outside the repository; never echo its contents.
            diagnostics.WriteLine("worker: configuration file could not be read or parsed as JSON.");
            return null;
        }

        if (file is null)
        {
            diagnostics.WriteLine("worker: configuration file is empty.");
            return null;
        }

        var token = environment[RunnerArguments.TokenVariable] as string;
        if (string.IsNullOrWhiteSpace(token))
        {
            diagnostics.WriteLine($"worker: {RunnerArguments.TokenVariable} environment variable is required.");
            return null;
        }

        if (!Uri.TryCreate(file.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            diagnostics.WriteLine("worker: baseUrl must be an absolute URL.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(file.WorkerKey))
        {
            diagnostics.WriteLine("worker: workerKey is required.");
            return null;
        }

        if (file.Capabilities is not { Count: > 0 })
        {
            diagnostics.WriteLine("worker: at least one capability role is required.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(file.AdapterPath) || !Path.IsPathFullyQualified(file.AdapterPath))
        {
            diagnostics.WriteLine("worker: adapterPath is required and must be an absolute path.");
            return null;
        }

        if (file.Projects is not { Count: > 0 })
        {
            diagnostics.WriteLine("worker: at least one project repository mapping is required.");
            return null;
        }

        var projects = new List<WorkerProjectMapping>(file.Projects.Count);

        foreach (var project in file.Projects)
        {
            if (!Guid.TryParse(project.ProjectId, out var projectId) || projectId == Guid.Empty)
            {
                diagnostics.WriteLine("worker: every project mapping needs a valid projectId GUID.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(project.Worktree) || !Path.IsPathFullyQualified(project.Worktree))
            {
                diagnostics.WriteLine("worker: every project mapping needs an absolute worktree path.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(project.AllowedRoot) || !Path.IsPathFullyQualified(project.AllowedRoot))
            {
                diagnostics.WriteLine("worker: every project mapping needs an absolute allowedRoot path.");
                return null;
            }

            // Sprint 07 automates read-only execution only. An automatically claimed session must
            // never be able to select a writing mode, so this is rejected at configuration load
            // rather than filtered later.
            var mode = string.IsNullOrWhiteSpace(project.Mode) ? "read-only" : project.Mode.Trim();
            if (!string.Equals(mode, "read-only", StringComparison.Ordinal))
            {
                diagnostics.WriteLine("worker: automatic pickup supports mode 'read-only' only.");
                return null;
            }

            if (projects.Any(existing => existing.ProjectId == projectId))
            {
                diagnostics.WriteLine("worker: duplicate projectId in project mappings.");
                return null;
            }

            projects.Add(new WorkerProjectMapping(projectId, project.Worktree, project.AllowedRoot, mode));
        }

        var pollSeconds = Clamp(file.PollSeconds ?? DefaultPollSeconds);
        var maxPollSeconds = Clamp(file.MaxPollSeconds ?? DefaultMaxPollSeconds);
        if (maxPollSeconds < pollSeconds)
        {
            maxPollSeconds = pollSeconds;
        }

        var adapterTimeoutSeconds = Math.Clamp(
            file.AdapterTimeoutSeconds ?? RunnerArguments.DefaultTimeoutSeconds,
            RunnerArguments.MinTimeoutSeconds,
            RunnerArguments.MaxTimeoutSeconds);

        var leaseSeconds = Math.Clamp(file.LeaseSeconds ?? DefaultLeaseSecondsFor(adapterTimeoutSeconds), 30, 3600);

        return new WorkerConfiguration(
            baseUrl,
            file.WorkerKey.Trim(),
            string.IsNullOrWhiteSpace(file.DisplayName) ? file.WorkerKey.Trim() : file.DisplayName.Trim(),
            file.Capabilities,
            token,
            file.AdapterPath,
            file.AdapterArguments ?? [],
            TimeSpan.FromSeconds(adapterTimeoutSeconds),
            TimeSpan.FromSeconds(pollSeconds),
            TimeSpan.FromSeconds(maxPollSeconds),
            TimeSpan.FromSeconds(Clamp(file.HeartbeatSeconds ?? DefaultHeartbeatSeconds)),
            leaseSeconds,
            projects);
    }

    /// <summary>
    /// Conservative initial duration. The worker renews live claims during execution, so this is
    /// crash-recovery timing rather than a requirement to exceed every possible adapter run.
    /// </summary>
    private static int DefaultLeaseSecondsFor(int adapterTimeoutSeconds) =>
        Math.Min(3600, (adapterTimeoutSeconds * 2) + 300);

    private static int Clamp(int seconds) => Math.Clamp(seconds, MinPollSeconds, MaxPollSeconds);

    public IReadOnlyList<Guid> ProjectIds => Projects.Select(project => project.ProjectId).ToList();

    public WorkerProjectMapping? FindProject(Guid projectId) =>
        Projects.FirstOrDefault(project => project.ProjectId == projectId);

    private sealed record WorkerConfigurationFile(
        string? BaseUrl,
        string? WorkerKey,
        string? DisplayName,
        IReadOnlyList<string>? Capabilities,
        string? AdapterPath,
        IReadOnlyList<string>? AdapterArguments,
        int? AdapterTimeoutSeconds,
        int? PollSeconds,
        int? MaxPollSeconds,
        int? HeartbeatSeconds,
        int? LeaseSeconds,
        IReadOnlyList<WorkerProjectMappingFile>? Projects);

    private sealed record WorkerProjectMappingFile(
        string? ProjectId,
        string? Worktree,
        string? AllowedRoot,
        string? Mode);
}

/// <summary>Machine-local mapping from a Familiar project to a repository on this host.</summary>
public sealed record WorkerProjectMapping(Guid ProjectId, string Worktree, string AllowedRoot, string Mode)
{
    /// <summary>
    /// The adapter environment for this project. These variable names are the adapter's existing
    /// administrator-controlled configuration surface (ADR-0007) — the worker supplies them per
    /// invocation instead of the operator exporting one fixed repository globally.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToAdapterEnvironment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FAMILIAR_CLAUDE_WORKTREE"] = Worktree,
        ["FAMILIAR_CLAUDE_ALLOWED_ROOT"] = AllowedRoot,
        ["FAMILIAR_CLAUDE_MODE"] = Mode
    };
}
