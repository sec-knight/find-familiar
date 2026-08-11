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

            // Sprint 07 automated read-only execution only. ADR-0010 allows a project to opt in to
            // writing, and even then only an Implementer session writes — see
            // WorkerProjectMapping.ResolveMode. ADR-0021 adds a third mode for maintaining the host
            // itself. Anything other than these three values is rejected at configuration load
            // rather than filtered later.
            var mode = string.IsNullOrWhiteSpace(project.Mode)
                ? WorkerProjectMapping.ReadOnlyMode
                : project.Mode.Trim();

            if (!WorkerProjectMapping.IsKnownMode(mode))
            {
                diagnostics.WriteLine(
                    $"worker: mode must be '{WorkerProjectMapping.ReadOnlyMode}', "
                    + $"'{WorkerProjectMapping.EditWorktreeMode}' or '{WorkerProjectMapping.LocalMaintenanceMode}'.");
                return null;
            }

            // A host-maintenance mapping is the one mode whose blast radius is the machine rather
            // than a directory, so the operator states that intent twice: once as the mode and once
            // as an explicit acknowledgement. A mode string arriving from a copied config file is
            // otherwise indistinguishable from one an operator chose deliberately.
            if (string.Equals(mode, WorkerProjectMapping.LocalMaintenanceMode, StringComparison.Ordinal)
                && project.AcknowledgeHostAccess is not true)
            {
                diagnostics.WriteLine(
                    $"worker: mode '{WorkerProjectMapping.LocalMaintenanceMode}' requires "
                    + "\"acknowledgeHostAccess\": true on the same project mapping.");
                return null;
            }

            if (projects.Any(existing => existing.ProjectId == projectId))
            {
                diagnostics.WriteLine("worker: duplicate projectId in project mappings.");
                return null;
            }

            // Optional, and validated when present: a relative projectPath could not anchor a
            // translation, so it is rejected at load rather than producing silent non-translation later.
            if (project.ProjectPath is not null
                && (string.IsNullOrWhiteSpace(project.ProjectPath) || !Path.IsPathFullyQualified(project.ProjectPath)))
            {
                diagnostics.WriteLine("worker: projectPath, when present, must be an absolute path.");
                return null;
            }

            projects.Add(new WorkerProjectMapping(
                projectId, project.Worktree, project.AllowedRoot, mode, project.ProjectPath));
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
        string? Mode,
        string? ProjectPath,
        bool? AcknowledgeHostAccess);
}

/// <summary>Machine-local mapping from a Familiar project to a repository on this host.</summary>
/// <param name="ProjectPath">
/// Optional. The canonical checkout this workspace mirrors — on this deployment, the live
/// <c>/srv/familiar/apps/FindFamiliar</c> that <see cref="Worktree"/> is a linked worktree of.
///
/// It exists for one purpose: when an assignment names an absolute path under it, the Runner can
/// restate that path relative to the workspace instead of leaving each role to guess. Without it such
/// a path is flagged as unreachable rather than translated, because two absolute paths ending in the
/// same filename may be different files and matching on the tail would invent a correspondence nobody
/// configured.
///
/// It never widens what a session may reach. Containment is still decided by <see cref="AllowedRoot"/>
/// in the adapter; this is a naming aid for reading the assignment, not a permission.
/// </param>
public sealed record WorkerProjectMapping(
    Guid ProjectId,
    string Worktree,
    string AllowedRoot,
    string Mode,
    string? ProjectPath = null)
{
    public const string ReadOnlyMode = "read-only";
    public const string EditWorktreeMode = "edit-worktree";

    /// <summary>
    /// Maintenance of the host this worker runs on, rather than of a repository checked out on it
    /// (ADR-0021).
    ///
    /// Every other mode answers "which files may this session change?" with a directory, and the
    /// directory is the boundary. This mode exists for work whose whole subject is the machine —
    /// restarting a unit, reading SMART data off a disk, finding out why a sibling worker stopped
    /// heartbeating — and for that work a directory boundary is not a weaker answer, it is an answer
    /// to a different question. There is no path containment that makes `systemctl restart` safe and
    /// no worktree whose cleanliness says anything about whether a service came back up.
    ///
    /// So this mode states the boundary it actually has instead of implying one it does not: the
    /// session runs as the worker's own OS user and can do what that user can do. The controls are
    /// the ones that survive that fact — a mapping that must name this mode explicitly and
    /// acknowledge it, a project that must be created for it, and a human approving the plan before
    /// any session is started at all.
    /// </summary>
    public const string LocalMaintenanceMode = "local-maintenance";

    /// <summary>The role whose sessions are allowed to write, when the mapping opts in.</summary>
    public const string WritingRole = "Implementer";

    public static bool IsKnownMode(string mode) =>
        string.Equals(mode, ReadOnlyMode, StringComparison.Ordinal)
        || string.Equals(mode, EditWorktreeMode, StringComparison.Ordinal)
        || string.Equals(mode, LocalMaintenanceMode, StringComparison.Ordinal);

    /// <summary>
    /// The mode this session actually runs in.
    ///
    /// Opting a project in to <see cref="EditWorktreeMode"/> does not make every session a writing
    /// one: a Planner is asked to plan and a Reviewer to review, so granting either of them file
    /// writes would widen the boundary for no benefit. Only an Implementer writes, and only where the
    /// operator asked for it.
    ///
    /// What edit mode permits is unchanged from ADR-0007 and enforced by the adapter, not here: a
    /// clean linked git worktree, whole-segment path containment with symlink resolution, and a tool
    /// list that deliberately excludes Bash so there is no path to git commit or push.
    ///
    /// <see cref="LocalMaintenanceMode"/> narrows by role the same way and for the same reason. A
    /// Planner asked to plan and a Reviewer asked to review do not need to run commands on the host
    /// to do it, so neither is given the ability; only the Implementer that a human approved
    /// actually acts on the machine.
    /// </summary>
    public string ResolveMode(string role) =>
        IsWritingMode && string.Equals(role, WritingRole, StringComparison.Ordinal)
            ? Mode
            : ReadOnlyMode;

    private bool IsWritingMode =>
        string.Equals(Mode, EditWorktreeMode, StringComparison.Ordinal)
        || string.Equals(Mode, LocalMaintenanceMode, StringComparison.Ordinal);

    /// <summary>
    /// The adapter environment for this project and role. These variable names are the adapter's
    /// existing administrator-controlled configuration surface (ADR-0007) — the worker supplies them
    /// per invocation instead of the operator exporting one fixed repository globally.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToAdapterEnvironment(string role) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FAMILIAR_CLAUDE_WORKTREE"] = Worktree,
        ["FAMILIAR_CLAUDE_ALLOWED_ROOT"] = AllowedRoot,
        ["FAMILIAR_CLAUDE_MODE"] = ResolveMode(role)
    };

    /// <summary>
    /// The workspace contract for this project and role.
    ///
    /// Built from the same values as <see cref="ToAdapterEnvironment"/> so that what a session is told
    /// and what actually bounds it cannot drift apart. Only the mode differs by role — every role on a
    /// task gets the same workspace root, which is what makes an Implementer and a Reviewer describe
    /// the same files.
    /// </summary>
    public WorkspaceContract ToWorkspaceContract(string role) =>
        new(Worktree, AllowedRoot, ResolveMode(role), ProjectPath);
}
