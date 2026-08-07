namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// What a repository looks like at one moment, as read from git and nothing else.
///
/// Every list here comes from <c>git ls-files</c> or <c>git log</c>, so it describes <b>tracked</b>
/// content only. That is the correction that matters: a filesystem walk pulls in <c>bin/</c>,
/// <c>obj/</c>, <c>node_modules</c> and SQLite files, and the resulting "repository state" describes
/// the build output of one machine rather than the repository. Asking git means the answer already
/// honours <c>.gitignore</c> and is the same on every checkout.
/// </summary>
/// <param name="TwoLevelPaths">
/// The distinct first two path segments of every tracked file — <c>src/FindFamiliar.Server</c> rather
/// than each of its ninety files. This is the section that tells a reader the <i>shape</i> of the
/// repository in a few dozen lines, which is why it survives trimming that the exhaustive list does
/// not.
///
/// Derived here from <see cref="TrackedPaths"/> rather than by piping git through <c>cut</c> and
/// <c>sort</c>: same set, one process instead of three, no shell, and a deterministic ordinal sort
/// that does not change with the machine's locale.
/// </param>
public sealed record RepositoryState(
    string Branch,
    string HeadSha,
    IReadOnlyList<string> TrackedPaths,
    IReadOnlyList<string> TwoLevelPaths,
    IReadOnlyList<string> RecentCommits)
{
    /// <summary>Commits carried by <c>git log -20 --oneline</c>.</summary>
    public const int RecentCommitCount = 20;

    /// <summary>
    /// The two-level view of a set of tracked paths, ordinal-sorted and distinct.
    /// </summary>
    public static IReadOnlyList<string> TwoLevelView(IEnumerable<string> trackedPaths)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in trackedPaths)
        {
            var segments = path.Split('/');
            prefixes.Add(segments.Length <= 2 ? path : string.Join('/', segments[0], segments[1]));
        }

        return [.. prefixes];
    }
}
