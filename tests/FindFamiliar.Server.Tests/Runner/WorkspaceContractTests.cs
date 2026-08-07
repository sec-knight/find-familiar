using FindFamiliar.Runner;

namespace FindFamiliar.Server.Tests.Runner;

/// <summary>
/// Slice 0: the execution contract must make the reachable workspace unambiguous.
///
/// These tests are written against the failure that actually happened, not against a paraphrase of
/// it. On 2026-08-07 two trivial README tasks produced this: the Implementer, scoped to the linked
/// worktree, made the edit and reported honestly that it could not reach the path the plan named; the
/// Reviewer, whose scope came from ambient environment, inspected the live checkout, correctly found
/// nothing, and requested changes. Every component was truthful and the human was told correct work
/// had failed.
///
/// So the property under test is not "a path is rewritten". It is that <b>two roles on one task
/// cannot end up describing different files without that being visible</b>.
/// </summary>
public sealed class WorkspaceContractTests
{
    private const string Workspace = "/srv/familiar/worktrees/familiar-sessions";
    private const string AllowedRoot = "/srv/familiar/worktrees";
    private const string LiveCheckout = "/srv/familiar/apps/FindFamiliar";

    private static WorkspaceContract Contract(string mode = "read-only", string? projectPath = LiveCheckout) =>
        new(Workspace, AllowedRoot, mode, projectPath);

    // ---------------------------------------------------------------- 1. the workspace is stated

    [Fact]
    public void The_effective_assignment_identifies_the_authorized_workspace()
    {
        var augmented = Contract().Augment("Append a line to the README.");

        Assert.Contains("Workspace contract (authoritative)", augmented, StringComparison.Ordinal);
        Assert.Contains(Workspace, augmented, StringComparison.Ordinal);
        Assert.Contains("Authorized workspace root", augmented, StringComparison.Ordinal);

        // The original assignment survives intact underneath it.
        Assert.Contains("Append a line to the README.", augmented, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract is prepended, not merged. The assignment is untrusted content authored upstream,
    /// and it must not be able to appear above the rules that bound it.
    /// </summary>
    [Fact]
    public void The_contract_precedes_the_untrusted_assignment()
    {
        var augmented = Contract().Augment("Do the work.");

        Assert.True(
            augmented.IndexOf("Workspace contract", StringComparison.Ordinal)
            < augmented.IndexOf("Do the work.", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 2. the original failure

    /// <summary>
    /// The exact assignment text that broke: a README change named by its live-checkout absolute path.
    /// It must now arrive resolved to the workspace copy rather than left for each role to interpret.
    /// </summary>
    [Fact]
    public void A_readme_change_named_by_absolute_path_resolves_to_the_workspace_copy()
    {
        var assignment = $"Append the exact line `Sakura ~ Hello World` to {LiveCheckout}/README.md.";

        var references = Contract().InspectAssignment(assignment);
        var readme = Assert.Single(references);

        Assert.Equal($"{LiveCheckout}/README.md", readme.Original);
        Assert.Equal("README.md", readme.WorkspaceRelative);
        Assert.True(readme.IsTranslated);

        var augmented = Contract().Augment(assignment);

        Assert.Contains("`README.md`", augmented, StringComparison.Ordinal);
        Assert.Contains("relative to the workspace root", augmented, StringComparison.Ordinal);
    }

    /// <summary>A path already inside the workspace needs no explanation and must not get one.</summary>
    [Fact]
    public void A_path_already_inside_the_workspace_is_not_reported()
    {
        Assert.Empty(Contract().InspectAssignment($"Edit {Workspace}/README.md."));
    }

    /// <summary>Containment is whole-segment: a sibling directory is not inside the workspace.</summary>
    [Fact]
    public void A_lookalike_sibling_path_is_not_treated_as_inside_the_workspace()
    {
        var references = Contract(projectPath: null)
            .InspectAssignment("Edit /srv/familiar/worktrees/familiar-sessions-other/README.md.");

        Assert.Single(references);
    }

    // ---------------------------------------------------------------- 3. roles agree

    /// <summary>
    /// The invariant the incident violated. An Implementer writes and a Reviewer does not, but they
    /// must be told about the same tree — otherwise the Reviewer judges a file the Implementer never
    /// touched, which is exactly what happened.
    /// </summary>
    [Fact]
    public void An_implementer_and_a_reviewer_receive_the_same_workspace()
    {
        var mapping = new WorkerProjectMapping(
            Guid.NewGuid(), Workspace, AllowedRoot, WorkerProjectMapping.EditWorktreeMode, LiveCheckout);

        var implementer = mapping.ToWorkspaceContract("Implementer");
        var reviewer = mapping.ToWorkspaceContract("Reviewer");

        Assert.Equal(implementer.WorkspaceRoot, reviewer.WorkspaceRoot);
        Assert.Equal(implementer.LogicalProjectPath, reviewer.LogicalProjectPath);

        // Only the permission differs, and only in the direction that narrows.
        Assert.Equal(WorkerProjectMapping.EditWorktreeMode, implementer.Mode);
        Assert.Equal(WorkerProjectMapping.ReadOnlyMode, reviewer.Mode);

        var assignment = $"Append a line to {LiveCheckout}/README.md.";

        Assert.Equal(
            implementer.InspectAssignment(assignment).Single().WorkspaceRelative,
            reviewer.InspectAssignment(assignment).Single().WorkspaceRelative);
    }

    /// <summary>
    /// The contract a session is told must be built from the same values that bound it. A drift here
    /// would be the original bug wearing a new shape: an accurate-looking statement about a workspace
    /// the adapter is not actually using.
    /// </summary>
    [Fact]
    public void The_stated_workspace_matches_the_environment_that_bounds_the_adapter()
    {
        var mapping = new WorkerProjectMapping(
            Guid.NewGuid(), Workspace, AllowedRoot, WorkerProjectMapping.EditWorktreeMode, LiveCheckout);

        foreach (var role in new[] { "Planner", "Implementer", "Reviewer" })
        {
            var environment = mapping.ToAdapterEnvironment(role);
            var contract = mapping.ToWorkspaceContract(role);

            Assert.Equal(environment["FAMILIAR_CLAUDE_WORKTREE"], contract.WorkspaceRoot);
            Assert.Equal(environment["FAMILIAR_CLAUDE_ALLOWED_ROOT"], contract.AllowedRoot);
            Assert.Equal(environment["FAMILIAR_CLAUDE_MODE"], contract.Mode);
        }
    }

    // ---------------------------------------------------------------- 4. unreachable paths are flagged

    /// <summary>
    /// An unrelated absolute path cannot be translated, and must not be. Two paths ending in
    /// <c>README.md</c> may be different files; matching on the tail would invent a correspondence
    /// nobody configured. It is flagged instead, and the flag says the thing that prevents the
    /// incident: do not report the work missing on the strength of a path you cannot reach.
    /// </summary>
    [Fact]
    public void An_unreachable_absolute_path_is_flagged_rather_than_silently_rewritten()
    {
        var references = Contract().InspectAssignment("Update /etc/somewhere/else/README.md too.");
        var reference = Assert.Single(references);

        Assert.False(reference.IsTranslated);
        Assert.Null(reference.WorkspaceRelative);

        var augmented = Contract().Augment("Update /etc/somewhere/else/README.md too.");

        Assert.Contains("cannot be resolved into this workspace", augmented, StringComparison.Ordinal);
        Assert.Contains("report this path as unreachable", augmented, StringComparison.Ordinal);
        Assert.Contains("rather than reporting the work as missing", augmented, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a configured logical project path there is nothing to anchor a translation to, so even
    /// the live-checkout path is flagged rather than guessed. Configuration, not inference.
    /// </summary>
    [Fact]
    public void Without_a_configured_project_path_nothing_is_translated()
    {
        var reference = Assert.Single(
            Contract(projectPath: null).InspectAssignment($"Edit {LiveCheckout}/README.md."));

        Assert.False(reference.IsTranslated);
    }

    [Fact]
    public void Several_distinct_paths_are_each_classified_once()
    {
        var assignment = $"""
            Edit {LiveCheckout}/README.md and {LiveCheckout}/docs/guide.md.
            Mention {LiveCheckout}/README.md again, and also /opt/unrelated/file.txt.
            """;

        var references = Contract().InspectAssignment(assignment);

        Assert.Equal(3, references.Count);
        Assert.Equal(2, references.Count(reference => reference.IsTranslated));
        Assert.Single(references, reference => !reference.IsTranslated);
    }

    // ---------------------------------------------------------------- fail closed

    /// <summary>
    /// The other half of the incident: the explicit CLI path supplied no mapping, so the adapter
    /// inherited whatever the operator had exported. An unresolvable workspace is now a refusal.
    /// </summary>
    [Fact]
    public void A_missing_workspace_resolves_to_nothing_rather_than_a_default()
    {
        Assert.Null(WorkspaceContract.TryResolve(adapterEnvironment: null, ambientLookup: _ => null));
        Assert.Null(WorkspaceContract.TryResolve(null, name =>
            name == "FAMILIAR_CLAUDE_WORKTREE" ? "not-absolute" : null));
    }

    /// <summary>The supplied environment wins over ambient, so a stray export cannot redirect a session.</summary>
    [Fact]
    public void A_supplied_environment_takes_precedence_over_ambient_variables()
    {
        var resolved = WorkspaceContract.TryResolve(
            new Dictionary<string, string>
            {
                ["FAMILIAR_CLAUDE_WORKTREE"] = Workspace,
                ["FAMILIAR_CLAUDE_ALLOWED_ROOT"] = AllowedRoot,
                ["FAMILIAR_CLAUDE_MODE"] = "edit-worktree"
            },
            _ => LiveCheckout);

        Assert.NotNull(resolved);
        Assert.Equal(Workspace, resolved!.WorkspaceRoot);
        Assert.Equal("edit-worktree", resolved.Mode);
    }

    /// <summary>An explicit invocation with the adapter variables exported still gets a contract.</summary>
    [Fact]
    public void An_explicit_invocation_resolves_the_workspace_from_ambient_variables()
    {
        var resolved = WorkspaceContract.TryResolve(null, name => name switch
        {
            "FAMILIAR_CLAUDE_WORKTREE" => Workspace,
            "FAMILIAR_CLAUDE_ALLOWED_ROOT" => AllowedRoot,
            "FAMILIAR_CLAUDE_MODE" => "read-only",
            _ => null
        });

        Assert.NotNull(resolved);
        Assert.Equal(Workspace, resolved!.WorkspaceRoot);
    }
}
