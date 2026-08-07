namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// Which repository is snapshotted, for which project, and how often.
///
/// Off unless configured. This is the one component in the talk lane that runs a process on the host,
/// and a default that quietly shells out to git in whatever directory the server happens to have been
/// started from is not a default anybody chose.
/// </summary>
public sealed class RepositorySnapshotOptions
{
    public const string SectionName = "Familiar:RepositorySnapshot";

    /// <summary>Nothing runs and no entry is written unless this is true and a path is set.</summary>
    public bool Enabled { get; set; }

    /// <summary>The working tree to read. Must be an absolute path to a git repository.</summary>
    public string? RepositoryPath { get; set; }

    /// <summary>
    /// The project the snapshot entry belongs to. With none set and exactly one non-sensitive active
    /// project, that project is used — the same rule plan drafting follows. With several and none
    /// named, nothing is written, because choosing would be inventing intent.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// The timer's period.
    ///
    /// The timer is the backstop, not the mechanism: the post-commit hook is what makes a snapshot
    /// current, and this is what makes it current anyway on a machine where the hook was never
    /// installed, or was installed in one of several worktrees. Nothing here depends on a person
    /// remembering to log anything, which is the whole point.
    /// </summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// How long any one git invocation may take before it is abandoned.
    ///
    /// A git command that hangs — an index lock held by an editor, a filesystem that has gone away —
    /// must cost this snapshot and nothing else. The prior snapshot stays exactly as it was.
    /// </summary>
    public int GitTimeoutSeconds { get; set; } = 30;

    public bool IsConfigured() =>
        Enabled
        && !string.IsNullOrWhiteSpace(RepositoryPath)
        && Path.IsPathFullyQualified(RepositoryPath);

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));

    public TimeSpan GitTimeout => TimeSpan.FromSeconds(Math.Max(1, GitTimeoutSeconds));
}
