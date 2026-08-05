using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Providers;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The deterministic summary is the page's floor: it is what a person reads when no reasoning
/// provider is configured, and it is the yardstick a provider's answer is measured against.
///
/// So it is held to a stricter standard than a model reply would be. Every sentence must be
/// supported by a field of the snapshot that is actually populated — the discipline
/// <see cref="FamiliarSummaryComposer"/> established for a single task, applied to a project.
/// </summary>
public sealed class FamiliarSummaryWriterTests
{
    [Fact]
    public void It_says_which_project_this_is()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith());

        Assert.Contains("Find Familiar", summary.ProjectStatement, StringComparison.Ordinal);
        Assert.Contains("Active", summary.ProjectStatement, StringComparison.Ordinal);
        Assert.Contains("41", summary.ProjectStatement, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing needs a human, so there is no sentence saying anything does — and equally none saying
    /// something was resolved, which is a claim about an event the snapshot does not record.
    /// </summary>
    [Fact]
    public void A_project_with_nothing_to_do_makes_no_claim_that_something_needs_doing()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(
            Task("Finished work", TaskDisplayState.Succeeded, TaskDisplayReasonCode.MarkedCompleteByHuman,
                "You marked this task complete.", needsAttention: false)));

        Assert.Null(summary.AttentionStatement);
        Assert.Empty(summary.AttentionDetails);
        Assert.Null(summary.BlockedStatement);
        Assert.Empty(summary.NextSteps);
        Assert.Contains("Nothing is running", summary.ActivityStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void It_names_what_needs_a_human_using_the_recorded_reason()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(
            Task("Windows worker setup", TaskDisplayState.NeedsAttention, TaskDisplayReasonCode.AwaitingHumanApproval,
                "Waiting for your approval to start the Reviewer session.", needsAttention: true,
                recommendation: "Approve Reviewer."),
            Task("Quiet work", TaskDisplayState.NotStarted, TaskDisplayReasonCode.NeverStarted,
                "No session has run yet.", needsAttention: false)));

        Assert.NotNull(summary.AttentionStatement);
        var detail = Assert.Single(summary.AttentionDetails);
        Assert.Contains("Windows worker setup", detail, StringComparison.Ordinal);
        Assert.Contains("Waiting for your approval", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void It_reports_what_is_blocked_without_guessing_why()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(
            Task("Cloudflare tunnel", TaskDisplayState.Blocked, TaskDisplayReasonCode.NoWorkerForRole,
                "Waiting for a worker that can run Implementer. No enabled worker declares that role.",
                needsAttention: true,
                recommendation: "Add the missing role to a worker's capabilities.")));

        Assert.NotNull(summary.BlockedStatement);
        var detail = Assert.Single(summary.BlockedDetails);
        Assert.Contains("Cloudflare tunnel", detail, StringComparison.Ordinal);
        Assert.Contains("No enabled worker declares that role", detail, StringComparison.Ordinal);

        // The reason is quoted from the projection. Nothing is added about why no worker exists,
        // because the server has no record of that.
        Assert.DoesNotContain("because", summary.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("probably", summary.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("likely", summary.Render(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_recommends_at_most_three_next_steps()
    {
        var tasks = Enumerable.Range(0, 8)
            .Select(index => Task(
                $"Task {index}",
                TaskDisplayState.NeedsAttention,
                TaskDisplayReasonCode.NoNextStepProposed,
                "The Planner session finished and no next step is currently proposed.",
                needsAttention: true,
                recommendation: $"Decide what happens next on task {index}."))
            .ToArray();

        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(tasks));

        Assert.Equal(FamiliarSummaryWriter.MaxNextSteps, summary.NextSteps.Count);
        Assert.Equal(3, FamiliarSummaryWriter.MaxNextSteps);
        Assert.Equal(FamiliarSummaryWriter.MaxNamedTasks, summary.AttentionDetails.Count);
    }

    /// <summary>
    /// A task whose display state carries no recommendation gets no invented one. The writer has no
    /// rules of its own about what should happen next; it repeats what the Demiplane recorded.
    /// </summary>
    [Fact]
    public void A_task_with_no_recorded_recommendation_produces_no_next_step()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(
            Task("Unrecommended", TaskDisplayState.NeedsAttention, TaskDisplayReasonCode.MultipleStartedSessions,
                "Multiple sessions are recorded as started for this task.", needsAttention: true,
                recommendation: null)));

        Assert.NotNull(summary.AttentionStatement);
        Assert.Empty(summary.NextSteps);
    }

    [Fact]
    public void The_unknowns_are_the_snapshots_limitations_and_nothing_else()
    {
        var snapshot = SnapshotWith() with
        {
            Limitations = ["Showing 20 of 47 tasks.", "Worker capabilities are self-reported and are not verified."]
        };

        var summary = FamiliarSummaryWriter.Compose(snapshot);

        Assert.Equal(snapshot.Limitations, summary.Unknowns);
    }

    /// <summary>
    /// A refused snapshot still has to produce a usable read-out: this is exactly the case where the
    /// page has no provider answer to fall back on.
    /// </summary>
    [Fact]
    public void An_over_budget_snapshot_still_produces_a_summary_and_says_so()
    {
        var snapshot = SnapshotWith(
            Task("Big work", TaskDisplayState.Running, TaskDisplayReasonCode.SessionRunning,
                "A Planner session is running.", needsAttention: false)) with
        {
            IsWithinBudget = false,
            Limitations = ["This project is larger than the snapshot budget and is not sent to a reasoning provider."]
        };

        var summary = FamiliarSummaryWriter.Compose(snapshot);

        Assert.Contains("Find Familiar", summary.Render(), StringComparison.Ordinal);
        Assert.Contains("1 task is running", summary.ActivityStatement, StringComparison.Ordinal);
        Assert.Contains("not sent to a reasoning provider", summary.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary describes state, never events. "I started", "I created", "I approved" would all be
    /// claims about actions no code path in this slice can take.
    /// </summary>
    [Fact]
    public void It_never_claims_that_an_action_occurred()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(
            Task("Live work", TaskDisplayState.Running, TaskDisplayReasonCode.SessionRunning,
                "A Planner session is running.", needsAttention: false)));

        var rendered = summary.Render();

        foreach (var claim in new[] { "I have", "I've", "I started", "I created", "I approved", "on your behalf" })
        {
            Assert.DoesNotContain(claim, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// No handoff row means no decision was recorded. The summary must not fill that silence.
    /// </summary>
    [Fact]
    public void Absence_of_a_handoff_produces_no_statement_about_a_decision()
    {
        var summary = FamiliarSummaryWriter.Compose(SnapshotWith(
            Task("Undecided", TaskDisplayState.NeedsAttention, TaskDisplayReasonCode.NoNextStepProposed,
                "The Planner session finished and no next step is currently proposed.", needsAttention: true)));

        var rendered = summary.Render();

        foreach (var invented in new[] { "approved", "declined", "rejected", "was decided" })
        {
            Assert.DoesNotContain(invented, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    private static SnapshotTask Task(
        string title,
        TaskDisplayState state,
        TaskDisplayReasonCode reasonCode,
        string reasonText,
        bool needsAttention,
        string? recommendation = null) =>
        new(
            Guid.NewGuid(),
            title,
            state,
            reasonCode,
            reasonText,
            needsAttention,
            CurrentRole: null,
            Provider: null,
            HasPendingHandoff: false,
            recommendation);

    private static ProjectSnapshot SnapshotWith(params SnapshotTask[] tasks)
    {
        var counts = tasks
            .GroupBy(task => task.DisplayState)
            .Select(group => new SnapshotTaskStateCount(group.Key, group.Count()))
            .OrderBy(count => count.State)
            .ToList();

        return new ProjectSnapshot(
            Guid.NewGuid(),
            "Find Familiar",
            "Preserve context between people, projects, and AI.",
            ProjectPurposeTruncated: false,
            ProjectStatus.Active,
            ContextRevision: 41,
            tasks,
            Sessions: [],
            PendingHandoffs: [],
            ContextEntries: [],
            new SnapshotHealth(
                tasks.Length,
                counts,
                tasks.Count(task => task.NeedsHumanAttention),
                tasks.Any(task => task.DisplayState is TaskDisplayState.Running or TaskDisplayState.Waiting)),
            Providers:
            [
                new SnapshotProviderReadiness(
                    "Claude",
                    ProviderCapacityStatus.Unknown,
                    ProviderCapacityConfidence.None,
                    Detail: null)
            ],
            new SnapshotWorkforce(1, [AgentSessionRole.Planner], 1, 0, 0),
            Limitations: [],
            EstimatedCharacters: 1_000,
            IsWithinBudget: true,
            ObservedAt);
    }
}
