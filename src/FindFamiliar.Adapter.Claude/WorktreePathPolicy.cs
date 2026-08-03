namespace FindFamiliar.Adapter.Claude;

public enum PathPolicyOutcome
{
    Allowed,
    NotFullyQualified,
    UncNotSupported,
    EscapesRoot,
    NotContained,
    DoesNotExist,
    SymlinkEscape,
    SymlinkLoop
}

/// <summary>
/// Decides whether an administrator-configured worktree may be handed to Claude as its entire
/// filesystem scope.
///
/// The comparison is deliberately split in two. <see cref="Normalize"/> and
/// <see cref="IsContained"/> are pure string logic with no filesystem access, so Windows-shaped
/// paths can be exercised on any host — <see cref="Path.GetFullPath(string)"/> is OS-native and
/// silently mis-parses a Windows path on Linux (it treats '\' as an ordinary filename character
/// and does not understand drive letters). <see cref="Evaluate"/> adds the real-filesystem
/// symlink/reparse resolution on top for production use.
/// </summary>
public static class WorktreePathPolicy
{
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Splits a rooted path into comparable segments, resolving '.' and '..' textually. Returns
    /// null when the input is not usable as an allowlist boundary: not fully qualified, a UNC
    /// path, or containing a '..' that would climb above the root.
    /// </summary>
    /// <remarks>
    /// A '..' above the root is rejected rather than clamped. The OS convention of silently
    /// ignoring the extra '..' would make "/a/../.." and "/a" compare equal, which is exactly the
    /// ambiguity an allowlist must not have.
    /// </remarks>
    public static IReadOnlyList<string>? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var unified = path.Replace('\\', '/');

        // UNC ("//server/share/...") has a two-part atomic prefix that generic segment comparison
        // would split incorrectly. It is out of scope for these two variables, so reject it
        // outright rather than special-case it.
        if (unified.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var isWindowsRooted = unified.Length >= 3
            && char.IsAsciiLetter(unified[0])
            && unified[1] == ':'
            && unified[2] == '/';
        var isUnixRooted = unified[0] == '/';

        if (!isWindowsRooted && !isUnixRooted)
        {
            return null;
        }

        var segments = new List<string>();
        if (isWindowsRooted)
        {
            // The drive letter is a segment so "C:\x" and "D:\x" can never be contained in each other.
            segments.Add(unified[..2]);
            unified = unified[3..];
        }

        foreach (var raw in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                var floor = isWindowsRooted ? 1 : 0;
                if (segments.Count <= floor)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(raw);
        }

        return segments;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the same as, or below, <paramref name="root"/>.
    /// Compares whole segments case-insensitively — never a prefix match on the joined string,
    /// which would let "/allowed-evil" pass a "/allowed" allowlist.
    /// </summary>
    public static bool IsContained(IReadOnlyList<string> root, IReadOnlyList<string> candidate)
    {
        if (candidate.Count < root.Count)
        {
            return false;
        }

        for (var i = 0; i < root.Count; i++)
        {
            if (!string.Equals(root[i], candidate[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Full production check: textual containment, then real-filesystem existence and
    /// symlink/reparse resolution of the worktree and every ancestor beneath the root.
    /// </summary>
    public static PathPolicyOutcome Evaluate(string allowedRoot, string worktree)
    {
        var rootSegments = Normalize(allowedRoot);
        var worktreeSegments = Normalize(worktree);

        if (rootSegments is null)
        {
            return ClassifyRejection(allowedRoot);
        }

        if (worktreeSegments is null)
        {
            return ClassifyRejection(worktree);
        }

        if (!IsContained(rootSegments, worktreeSegments))
        {
            return PathPolicyOutcome.NotContained;
        }

        if (!Directory.Exists(allowedRoot) || !Directory.Exists(worktree))
        {
            return PathPolicyOutcome.DoesNotExist;
        }

        var resolvedRoot = ResolveLinks(allowedRoot, out var rootLooped);
        if (rootLooped)
        {
            return PathPolicyOutcome.SymlinkLoop;
        }

        var resolvedWorktree = ResolveLinks(worktree, out var worktreeLooped);
        if (worktreeLooped)
        {
            return PathPolicyOutcome.SymlinkLoop;
        }

        var resolvedRootSegments = Normalize(resolvedRoot);
        var resolvedWorktreeSegments = Normalize(resolvedWorktree);

        if (resolvedRootSegments is null || resolvedWorktreeSegments is null)
        {
            return PathPolicyOutcome.SymlinkEscape;
        }

        // Re-check after resolution: a symlink anywhere along the path may point outside the root
        // even though the literal string was contained.
        return IsContained(resolvedRootSegments, resolvedWorktreeSegments)
            ? PathPolicyOutcome.Allowed
            : PathPolicyOutcome.SymlinkEscape;
    }

    private static PathPolicyOutcome ClassifyRejection(string path)
    {
        var unified = (path ?? string.Empty).Replace('\\', '/');
        if (unified.StartsWith("//", StringComparison.Ordinal))
        {
            return PathPolicyOutcome.UncNotSupported;
        }

        var rooted = unified.Length > 0
            && (unified[0] == '/'
                || (unified.Length >= 3 && char.IsAsciiLetter(unified[0]) && unified[1] == ':' && unified[2] == '/'));

        return rooted ? PathPolicyOutcome.EscapesRoot : PathPolicyOutcome.NotFullyQualified;
    }

    /// <summary>
    /// Resolves symlinks/reparse points on the path itself and on every ancestor directory.
    /// <see cref="Directory.ResolveLinkTarget"/> only inspects the exact path handed to it, so an
    /// ancestor link (".../allowed/link-dir/real-worktree") would otherwise go unnoticed.
    /// </summary>
    private static string ResolveLinks(string path, out bool looped)
    {
        looped = false;
        var current = path;

        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var target = SafeResolve(current);
            if (target is null)
            {
                break;
            }

            current = Path.IsPathFullyQualified(target)
                ? target
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, target));

            if (hop == MaxLinkHops - 1)
            {
                looped = true;
                return current;
            }
        }

        // Walk ancestors so a link on an intermediate directory is resolved too.
        var parent = Path.GetDirectoryName(current.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(parent) || parent == current)
        {
            return current;
        }

        var resolvedParent = ResolveLinks(parent, out looped);
        if (looped)
        {
            return current;
        }

        var leaf = Path.GetFileName(current.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(leaf) ? resolvedParent : Path.Combine(resolvedParent, leaf);
    }

    private static string? SafeResolve(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
