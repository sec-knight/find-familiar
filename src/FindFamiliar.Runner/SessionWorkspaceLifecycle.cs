using System.Diagnostics;
using System.Text.Json;

namespace FindFamiliar.Runner;

public sealed record SessionWorkspaceLease(
    Guid TaskId,
    Guid SessionId,
    Guid ProjectId,
    string Role,
    string Worktree,
    string AllowedRoot,
    string Mode,
    string? ProjectPath,
    string? LeaseFile,
    bool IsEphemeral)
{
    public WorkspaceContract Contract => new(Worktree, AllowedRoot, Mode, ProjectPath);

    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FAMILIAR_CLAUDE_WORKTREE"] = Worktree,
        ["FAMILIAR_CLAUDE_ALLOWED_ROOT"] = AllowedRoot,
        ["FAMILIAR_CLAUDE_MODE"] = Mode
    };
}

public sealed record WorkspaceCleanupResult(bool Cleaned, bool Quarantined, bool Preserved, string Outcome);

public sealed class WorkspacePreparationException(
    string message,
    RunnerFailureDiagnostic diagnostic) : Exception(message)
{
    public RunnerFailureDiagnostic Diagnostic { get; } = diagnostic;
}

public interface ISessionWorkspaceLifecycle
{
    Task<SessionWorkspaceLease> AcquireAsync(
        WorkerProjectMapping mapping,
        Guid taskId,
        Guid sessionId,
        string role,
        CancellationToken cancellationToken = default);

    Task<WorkspaceCleanupResult> ReleaseAsync(
        SessionWorkspaceLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gives each mapped session a fresh detached worktree from the canonical checkout. Clean leases are
/// removed on every terminal path; dirty leases are moved into a named quarantine and never deleted.
/// A small sidecar records ownership so a worker restart can reclaim clean abandoned leases without
/// guessing about an arbitrary directory.
/// </summary>
public sealed class SessionWorkspaceLifecycle(TextWriter diagnostics) : ISessionWorkspaceLifecycle
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AbandonedLeaseAge = TimeSpan.FromHours(1);

    public async Task<SessionWorkspaceLease> AcquireAsync(
        WorkerProjectMapping mapping,
        Guid taskId,
        Guid sessionId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mapping.ProjectPath))
        {
            // Explicit test/legacy read-only mappings may intentionally point at a non-Git directory.
            // They cannot opt into editing, and preserve the old read-only contract until an operator
            // supplies a canonical projectPath.
            if (mapping.ResolveMode(role) == WorkerProjectMapping.EditWorktreeMode)
            {
                throw PreparationFailure(
                    "Edit sessions require a canonical projectPath for an ephemeral worktree.");
            }

            return new SessionWorkspaceLease(
                taskId,
                sessionId,
                mapping.ProjectId,
                role,
                mapping.Worktree,
                mapping.AllowedRoot,
                mapping.ResolveMode(role),
                mapping.ProjectPath,
                null,
                false);
        }

        var source = Path.GetFullPath(mapping.ProjectPath);
        var allowedRoot = Path.GetFullPath(mapping.AllowedRoot);
        if (!Directory.Exists(source) || !Directory.Exists(allowedRoot))
        {
            throw PreparationFailure("The configured workspace source or allowed root does not exist.");
        }

        await ReclaimAbandonedAsync(mapping, cancellationToken);

        var sourceRoot = await GitAsync(source, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (sourceRoot.ExitCode != 0 || string.IsNullOrWhiteSpace(sourceRoot.Stdout))
        {
            throw PreparationFailure("The configured projectPath is not a Git checkout.");
        }

        var canonicalSource = Path.GetFullPath(sourceRoot.Stdout.Trim());
        if (!PathsEqual(canonicalSource, source))
        {
            throw PreparationFailure("The configured projectPath did not resolve to its Git root.");
        }

        var status = await GitAsync(source, ["status", "--porcelain", "--untracked-files=all"], cancellationToken);
        if (status.ExitCode != 0 || !string.IsNullOrEmpty(status.Stdout))
        {
            throw PreparationFailure("The canonical project checkout is not clean; no session workspace was created.");
        }

        var worktree = Path.Combine(allowedRoot, $"familiar-session-{sessionId:N}");
        EnsureContained(allowedRoot, worktree);
        if (Directory.Exists(worktree) || File.Exists(worktree))
        {
            throw PreparationFailure("A workspace with this session ownership already exists.");
        }

        var add = await GitAsync(source, ["worktree", "add", "--detach", "--quiet", worktree, "HEAD"], cancellationToken);
        if (add.ExitCode != 0)
        {
            throw PreparationFailure("Git could not create the isolated session worktree.");
        }

        var leaseFile = LeaseFileFor(worktree);
        var lease = new SessionWorkspaceLease(
            taskId,
            sessionId,
            mapping.ProjectId,
            role,
            worktree,
            allowedRoot,
            mapping.ResolveMode(role),
            source,
            leaseFile,
            true);

        await WriteLeaseAsync(lease, "active", cancellationToken);
        diagnostics.WriteLine($"workspace: isolated lease acquired (session={sessionId}, role={role}).");
        return lease;
    }

    public async Task<WorkspaceCleanupResult> ReleaseAsync(
        SessionWorkspaceLease lease,
        CancellationToken cancellationToken = default)
    {
        if (!lease.IsEphemeral || lease.LeaseFile is null)
        {
            return new WorkspaceCleanupResult(false, false, false, "static");
        }

        if (!Directory.Exists(lease.Worktree))
        {
            DeleteLeaseFile(lease.LeaseFile);
            return new WorkspaceCleanupResult(false, false, false, "already-absent");
        }

        var status = await GitAsync(lease.Worktree, ["status", "--porcelain", "--untracked-files=all"], cancellationToken);
        if (status.ExitCode == 0 && string.IsNullOrEmpty(status.Stdout))
        {
            var removed = await GitAsync(lease.ProjectPath!, ["worktree", "remove", lease.Worktree], cancellationToken);
            if (removed.ExitCode == 0)
            {
                DeleteLeaseFile(lease.LeaseFile);
                diagnostics.WriteLine($"workspace: clean lease reclaimed (session={lease.SessionId}).");
                return new WorkspaceCleanupResult(true, false, false, "cleaned");
            }
        }

        var quarantined = await QuarantineAsync(lease, cancellationToken);
        if (quarantined)
        {
            diagnostics.WriteLine($"workspace: dirty lease quarantined for review (session={lease.SessionId}).");
            return new WorkspaceCleanupResult(false, true, true, "quarantined");
        }

        await WriteLeaseAsync(lease, "preserved", cancellationToken);
        diagnostics.WriteLine($"workspace: dirty lease preserved for review (session={lease.SessionId}).");
        return new WorkspaceCleanupResult(false, false, true, "preserved");
    }

    private async Task ReclaimAbandonedAsync(
        WorkerProjectMapping mapping,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(mapping.AllowedRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - AbandonedLeaseAge;
        foreach (var leaseFile in Directory.EnumerateFiles(root, "familiar-session-*.lease.json"))
        {
            SessionLeaseFile? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<SessionLeaseFile>(
                    await File.ReadAllTextAsync(leaseFile, cancellationToken), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (metadata is null
                || metadata.ProjectId != mapping.ProjectId
                || metadata.UpdatedUtc > cutoff
                || !PathsContained(root, metadata.Worktree))
            {
                continue;
            }

            var lease = new SessionWorkspaceLease(
                metadata.TaskId,
                metadata.SessionId,
                metadata.ProjectId,
                metadata.Role,
                metadata.Worktree,
                root,
                metadata.Mode,
                mapping.ProjectPath,
                leaseFile,
                true);

            diagnostics.WriteLine($"workspace: reclaiming abandoned lease (session={lease.SessionId}).");
            await ReleaseAsync(lease, cancellationToken);
        }
    }

    private async Task<bool> QuarantineAsync(
        SessionWorkspaceLease lease,
        CancellationToken cancellationToken)
    {
        var quarantineRoot = Path.Combine(lease.AllowedRoot, "quarantine");
        Directory.CreateDirectory(quarantineRoot);
        var target = Path.Combine(quarantineRoot, $"familiar-session-{lease.SessionId:N}");
        EnsureContained(lease.AllowedRoot, target);

        if (Directory.Exists(target))
        {
            return false;
        }

        var moved = await GitAsync(
            lease.ProjectPath!,
            ["worktree", "move", lease.Worktree, target],
            cancellationToken);
        if (moved.ExitCode != 0)
        {
            return false;
        }

        var movedLease = lease with { Worktree = target, LeaseFile = LeaseFileFor(target) };
        DeleteLeaseFile(lease.LeaseFile!);
        await WriteLeaseAsync(movedLease, "quarantined", cancellationToken);
        return true;
    }

    private static string LeaseFileFor(string worktree) => worktree + ".lease.json";

    private static void DeleteLeaseFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The workspace remains recoverable even if its local bookkeeping cannot be removed.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: never turn a cleanup bookkeeping failure into data loss.
        }
    }

    private static async Task WriteLeaseAsync(
        SessionWorkspaceLease lease,
        string state,
        CancellationToken cancellationToken)
    {
        if (lease.LeaseFile is null)
        {
            return;
        }

        var metadata = new SessionLeaseFile(
            lease.TaskId,
            lease.SessionId,
            lease.ProjectId,
            lease.Role,
            lease.Worktree,
            lease.Mode,
            state,
            DateTime.UtcNow);
        await File.WriteAllTextAsync(lease.LeaseFile, JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken);
    }

    private WorkspacePreparationException PreparationFailure(string detail) => new(
        detail,
        new RunnerFailureDiagnostic(
            "WorktreeNotClean",
            5,
            false,
            null,
            "The session workspace preflight failed before the provider was launched."));

    private static void EnsureContained(string root, string candidate)
    {
        if (!PathsContained(root, candidate))
        {
            throw new WorkspacePreparationException(
                "The session workspace path is outside the configured allowed root.",
                new RunnerFailureDiagnostic(
                    "WorktreeRejected", 4, false, null,
                    "The session workspace failed allowed-root containment before the provider was launched."));
        }
    }

    private static bool PathsContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    private static async Task<GitResult> GitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitResult(process.ExitCode, stdout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(-1, string.Empty);
        }
    }

    private sealed record GitResult(int ExitCode, string Stdout);

    private sealed record SessionLeaseFile(
        Guid TaskId,
        Guid SessionId,
        Guid ProjectId,
        string Role,
        string Worktree,
        string Mode,
        string State,
        DateTime UpdatedUtc);
}
