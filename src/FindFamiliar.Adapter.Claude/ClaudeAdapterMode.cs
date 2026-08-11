namespace FindFamiliar.Adapter.Claude;

/// <summary>The smallest useful permission policy (Sprint 06.5 spec section 3).</summary>
public enum ClaudeAdapterMode
{
    /// <summary>Claude may inspect files only; no tool that can mutate the worktree is exposed.</summary>
    ReadOnly,

    /// <summary>
    /// Claude may edit files in an exact clean disposable worktree. No commit, push, merge, tag,
    /// package publication, deployment, or credential access is exposed.
    /// </summary>
    EditWorktree,

    /// <summary>
    /// Claude may operate the host this adapter runs on: run commands and change files as the
    /// worker's own OS user (ADR-0021).
    ///
    /// This is the one mode whose name does not describe a filesystem boundary, because it does not
    /// have one. It exists for work about the machine itself — a stopped unit, a failing disk, a
    /// worker that will not come back — where the two other modes cannot express the task at all.
    /// The honest limit is the worker's user account, and it is stated rather than dressed up as
    /// containment.
    /// </summary>
    LocalMaintenance
}
