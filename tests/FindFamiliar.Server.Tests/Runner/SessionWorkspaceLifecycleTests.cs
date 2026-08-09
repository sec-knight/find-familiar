using System.Diagnostics;
using FindFamiliar.Runner;

namespace FindFamiliar.Server.Tests.Runner;

public sealed class SessionWorkspaceLifecycleTests
{
    [Fact]
    public async Task Fresh_edit_leases_are_clean_isolated_and_reclaimed_after_success()
    {
        using var fixture = GitFixture.Create();
        var lifecycle = new SessionWorkspaceLifecycle(TextWriter.Null);
        var mapping = fixture.Mapping();

        var first = await lifecycle.AcquireAsync(mapping, Guid.NewGuid(), Guid.NewGuid(), "Implementer");
        var second = await lifecycle.AcquireAsync(mapping, Guid.NewGuid(), Guid.NewGuid(), "Implementer");

        Assert.NotEqual(first.Worktree, second.Worktree);
        Assert.Equal("edit-worktree", first.Mode);
        Assert.Equal(string.Empty, RunGit(first.Worktree, "status", "--porcelain", "--untracked-files=all"));

        var firstCleanup = await lifecycle.ReleaseAsync(first);
        var secondCleanup = await lifecycle.ReleaseAsync(second);

        Assert.True(firstCleanup.Cleaned);
        Assert.True(secondCleanup.Cleaned);
        Assert.False(Directory.Exists(first.Worktree));
        Assert.False(Directory.Exists(second.Worktree));
        Assert.Empty(Directory.EnumerateDirectories(fixture.AllowedRoot, "familiar-session-*"));
    }

    [Fact]
    public async Task Dirty_failed_workspace_is_quarantined_and_cannot_poison_the_next_session()
    {
        using var fixture = GitFixture.Create();
        var lifecycle = new SessionWorkspaceLifecycle(TextWriter.Null);
        var mapping = fixture.Mapping();

        var failed = await lifecycle.AcquireAsync(mapping, Guid.NewGuid(), Guid.NewGuid(), "Implementer");
        await File.WriteAllTextAsync(Path.Combine(failed.Worktree, "meaningful-change.txt"), "preserve me");

        var cleanup = await lifecycle.ReleaseAsync(failed);

        Assert.True(cleanup.Quarantined);
        Assert.True(cleanup.Preserved);
        Assert.False(Directory.Exists(failed.Worktree));
        var quarantined = Directory.EnumerateDirectories(
            Path.Combine(fixture.AllowedRoot, "quarantine"), "familiar-session-*").Single();
        Assert.Equal("preserve me", await File.ReadAllTextAsync(Path.Combine(quarantined, "meaningful-change.txt")));

        var later = await lifecycle.AcquireAsync(mapping, Guid.NewGuid(), Guid.NewGuid(), "Implementer");
        Assert.Equal(string.Empty, RunGit(later.Worktree, "status", "--porcelain", "--untracked-files=all"));
        var laterCleanup = await lifecycle.ReleaseAsync(later);
        Assert.True(laterCleanup.Cleaned);
    }

    [Fact]
    public async Task Dirty_canonical_baseline_fails_preflight_before_a_session_worktree_is_created()
    {
        using var fixture = GitFixture.Create();
        await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "uncommitted.txt"), "do not copy");
        var lifecycle = new SessionWorkspaceLifecycle(TextWriter.Null);

        var exception = await Assert.ThrowsAsync<WorkspacePreparationException>(() => lifecycle.AcquireAsync(
            fixture.Mapping(), Guid.NewGuid(), Guid.NewGuid(), "Implementer"));

        Assert.Equal("WorktreeNotClean", exception.Diagnostic.Category);
        Assert.False(exception.Diagnostic.ProviderLaunched);
        Assert.Empty(Directory.EnumerateDirectories(fixture.AllowedRoot, "familiar-session-*"));
    }

    [Fact]
    public async Task Read_only_sessions_get_a_current_snapshot_without_edit_cleanliness_gating()
    {
        using var fixture = GitFixture.Create();
        var lifecycle = new SessionWorkspaceLifecycle(TextWriter.Null);
        var mapping = fixture.Mapping();

        var lease = await lifecycle.AcquireAsync(mapping, Guid.NewGuid(), Guid.NewGuid(), "Planner");

        Assert.Equal("read-only", lease.Mode);
        Assert.NotEqual(fixture.SourceRoot, lease.Worktree);
        Assert.True(File.Exists(Path.Combine(lease.Worktree, "README.md")));
        var cleanup = await lifecycle.ReleaseAsync(lease);
        Assert.True(cleanup.Cleaned);
    }

    private sealed class GitFixture : IDisposable
    {
        public string Root { get; }
        public string SourceRoot { get; }
        public string AllowedRoot { get; }

        private GitFixture()
        {
            Root = Directory.CreateTempSubdirectory("familiar-workspace-lifecycle").FullName;
            SourceRoot = Directory.CreateDirectory(Path.Combine(Root, "source")).FullName;
            AllowedRoot = Directory.CreateDirectory(Path.Combine(Root, "leases")).FullName;

            RunGit(SourceRoot, "init", "--quiet", "-b", "main");
            RunGit(SourceRoot, "config", "user.email", "tests@example.invalid");
            RunGit(SourceRoot, "config", "user.name", "Find Familiar Tests");
            File.WriteAllText(Path.Combine(SourceRoot, "README.md"), "baseline\n");
            RunGit(SourceRoot, "add", "README.md");
            RunGit(SourceRoot, "commit", "--quiet", "-m", "baseline");
        }

        public static GitFixture Create() => new();

        public WorkerProjectMapping Mapping() => new(
            Guid.NewGuid(),
            Path.Combine(AllowedRoot, "legacy-shared"),
            AllowedRoot,
            WorkerProjectMapping.EditWorktreeMode,
            SourceRoot);

        public void Dispose() => TryDelete(Root);

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // The test assertion already established the lifecycle result; cleanup is best effort
                // for Git's short-lived administrative handles on the host.
            }
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
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

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }
}
