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
    EditWorktree
}
