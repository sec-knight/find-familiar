using System.Text;
using System.Text.RegularExpressions;

namespace FindFamiliar.Runner;

/// <summary>
/// What a session is actually allowed to touch, stated to the session in its own execution packet.
///
/// <b>The failure this exists to end.</b> A plan named
/// <c>/srv/familiar/apps/FindFamiliar/README.md</c>. The Implementer's scope was the linked worktree,
/// so it edited the README there and honestly reported that it could not reach the named path. The
/// Reviewer, running from a different entry point, resolved its scope from ambient environment and
/// inspected the live checkout, where the line was correctly absent, and requested changes. Both were
/// truthful. Nothing in the assignment said which tree either of them was standing in, so neither
/// could see that they were describing different files, and the human was told correct work had
/// failed.
///
/// <b>Why this lives in the Runner.</b> The workspace is worker-local: which checkout, which linked
/// worktree, which root a given machine authorises. The server issues a logical assignment — project,
/// task, requested outcome — and must never learn about
/// <c>/srv/familiar/worktrees/familiar-sessions</c> or any other host path (ADR-0006). The Runner is
/// the first component that knows both the logical assignment and the physical workspace, so it is the
/// only place the two can be reconciled without moving host layout into the database.
///
/// It is also provider-neutral here rather than in the Claude adapter. The adapter already tells the
/// model its filesystem scope, but it tells only the model, only in that adapter's envelope, and only
/// as a boundary — not as a rule for reading the assignment's own path references. Putting the
/// reconciliation in the Runner means a second adapter inherits it instead of reimplementing it.
///
/// <b>What it does not do.</b> It never widens what a session may reach: the authorised root comes
/// from worker configuration and is restated here, never chosen here. Translation only ever maps a
/// path <em>into</em> the workspace; a path that cannot be mapped is flagged for the reader rather
/// than silently rewritten into something reachable.
/// </summary>
public sealed record WorkspaceContract(
    string WorkspaceRoot,
    string AllowedRoot,
    string Mode,
    string? LogicalProjectPath)
{
    /// <summary>
    /// How a path in the assignment relates to the workspace this session can actually reach.
    /// </summary>
    /// <param name="Original">The path exactly as the assignment wrote it.</param>
    /// <param name="WorkspaceRelative">
    /// The same resource expressed relative to the workspace root, when the path was under the
    /// configured logical project path and could therefore be mapped without guessing. Null when it
    /// could not be, which is the case a reader must be told about rather than have decided for them.
    /// </param>
    public sealed record PathReference(string Original, string? WorkspaceRelative)
    {
        public bool IsTranslated => WorkspaceRelative is not null;
    }

    /// <summary>
    /// Absolute POSIX or Windows paths appearing in assignment prose, code fences or Markdown links.
    /// Deliberately loose about what surrounds a path and strict about what a path may contain: the
    /// cost of noticing one path too many is a line of explanation, and the cost of missing one is the
    /// failure above.
    /// </summary>
    private static readonly Regex AbsolutePathPattern = new(
        @"(?<![A-Za-z0-9_./\\-])(?:/[A-Za-z0-9._-][A-Za-z0-9._/-]*|[A-Za-z]:[\\/][A-Za-z0-9._\\/-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the contract from the adapter environment the Runner is about to apply, so what the
    /// session is told is derived from the same values that will bound it rather than from a second
    /// source that could drift.
    ///
    /// Returns null when the workspace cannot be determined. That is a refusal, not a default: the
    /// previous behaviour of letting the adapter inherit whatever the operator happened to export is
    /// exactly how two sessions on one task came to stand in different trees.
    /// </summary>
    public static WorkspaceContract? TryResolve(
        IReadOnlyDictionary<string, string>? adapterEnvironment,
        Func<string, string?> ambientLookup,
        string? logicalProjectPath = null)
    {
        var workspace = Lookup("FAMILIAR_CLAUDE_WORKTREE");
        var allowedRoot = Lookup("FAMILIAR_CLAUDE_ALLOWED_ROOT");
        var mode = Lookup("FAMILIAR_CLAUDE_MODE");

        if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace))
        {
            return null;
        }

        return new WorkspaceContract(
            Normalise(workspace),
            string.IsNullOrWhiteSpace(allowedRoot) ? Normalise(workspace) : Normalise(allowedRoot),
            string.IsNullOrWhiteSpace(mode) ? "read-only" : mode.Trim(),
            string.IsNullOrWhiteSpace(logicalProjectPath) ? null : Normalise(logicalProjectPath));

        string? Lookup(string name) =>
            adapterEnvironment is not null && adapterEnvironment.TryGetValue(name, out var supplied)
                ? supplied
                : ambientLookup(name);
    }

    /// <summary>
    /// Classifies every absolute path the assignment mentions. A path already inside the workspace is
    /// not reported: it is reachable and needs no explanation.
    /// </summary>
    public IReadOnlyList<PathReference> InspectAssignment(string assignmentMarkdown)
    {
        if (string.IsNullOrEmpty(assignmentMarkdown))
        {
            return [];
        }

        var findings = new List<PathReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in AbsolutePathPattern.Matches(assignmentMarkdown))
        {
            var candidate = match.Value.TrimEnd('.', ',', ')', ':', ';');

            if (candidate.Length < 2 || !seen.Add(candidate))
            {
                continue;
            }

            var normalised = Normalise(candidate);

            if (IsUnder(normalised, WorkspaceRoot))
            {
                continue;
            }

            findings.Add(new PathReference(candidate, TryMapIntoWorkspace(normalised)));
        }

        return findings;
    }

    /// <summary>
    /// A path under the configured logical project path names the same resource this workspace holds a
    /// copy of, so it can be restated relative to the workspace without guessing. Anything else
    /// cannot: two unrelated absolute paths may end in <c>README.md</c> and mean different files, and
    /// matching on the tail would be inventing a correspondence nobody configured.
    /// </summary>
    private string? TryMapIntoWorkspace(string normalisedPath)
    {
        if (LogicalProjectPath is null || !IsUnder(normalisedPath, LogicalProjectPath))
        {
            return null;
        }

        var relative = normalisedPath[LogicalProjectPath.Length..].TrimStart('/');

        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>
    /// The section prepended to the assignment. It is the Runner's own words, placed above the
    /// assignment and outside it, because the assignment is untrusted content authored upstream and
    /// must not be able to restate the rules that bound it.
    /// </summary>
    public string Render(IReadOnlyList<PathReference> pathReferences)
    {
        var markdown = new StringBuilder();

        markdown.AppendLine("## Workspace contract (authoritative)");
        markdown.AppendLine();
        markdown.AppendLine($"- **Authorized workspace root:** `{WorkspaceRoot}`");
        markdown.AppendLine($"- **Permission mode:** {Mode}");
        markdown.AppendLine();
        markdown.AppendLine("Every path in the assignment below is to be resolved **relative to the authorized");
        markdown.AppendLine("workspace root**, whatever prefix the assignment writes it with. This workspace is the");
        markdown.AppendLine("project: judging or editing anything outside it is not a stricter reading of the");
        markdown.AppendLine("assignment, it is a different project.");
        markdown.AppendLine();
        markdown.AppendLine("Every role on this task receives this same contract, so an Implementer and a Reviewer");
        markdown.AppendLine("are always describing the same files. If you find yourself reporting that requested work");
        markdown.AppendLine("is missing, confirm you looked inside the workspace root above before concluding it.");

        if (pathReferences.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("### Path references in the assignment");
            markdown.AppendLine();
            markdown.AppendLine("The assignment names absolute paths outside this workspace. They are resolved as follows.");
            markdown.AppendLine("Do not read or write the original locations; they are not reachable from this session.");
            markdown.AppendLine();

            foreach (var reference in pathReferences)
            {
                markdown.AppendLine(reference.IsTranslated
                    ? $"- `{reference.Original}` → **`{reference.WorkspaceRelative}`** relative to the workspace root."
                    : $"- `{reference.Original}` → **cannot be resolved into this workspace.** Treat the assignment's "
                      + "intent as applying to the corresponding resource inside the workspace if one plainly exists, "
                      + "and otherwise report this path as unreachable rather than reporting the work as missing.");
            }
        }

        return markdown.ToString();
    }

    /// <summary>Prepends the contract to the assignment the provider will be shown.</summary>
    public string Augment(string assignmentMarkdown) =>
        Render(InspectAssignment(assignmentMarkdown))
        + Environment.NewLine
        + assignmentMarkdown;

    /// <summary>Whole-segment containment, so <c>/srv/a</c> never contains <c>/srv/ab</c>.</summary>
    private static bool IsUnder(string candidate, string root) =>
        candidate.Equals(root, StringComparison.Ordinal)
        || candidate.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal);

    private static string Normalise(string path)
    {
        var unified = path.Replace('\\', '/').TrimEnd('/');

        return unified.Length == 0 ? "/" : unified;
    }
}
