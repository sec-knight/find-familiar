using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The display-state rules behind the Demiplane.
///
/// These matter because the state a task shows is the whole basis on which a human decides what to do
/// next. A wrong state is worse than no state: "Waiting" on a task that is actually stuck, or
/// "Failed" on one that merely ran out of provider allowance, sends someone to fix the wrong thing.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class DemiplaneProjectionServiceTests
{
    [Fact]
    public async Task A_task_with_no_sessions_is_not_started()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedTaskAsync(dbContext, project, "Untouched work");

        var task = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.NotStarted, task.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.NeverStarted, task.ReasonCode);
        Assert.False(task.NeedsHumanAttention);
        Assert.Empty(task.Chain);
    }

    [Fact]
    public async Task A_claimed_session_is_running()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Live work");
        var worker = await SeedWorkerAsync(dbContext, "Planner");

        var session = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started);
        session.ClaimedByWorkerId = worker.Id;
        session.ClaimedUtc = DateTime.UtcNow;
        session.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(20);
        session.ClaimId = Guid.NewGuid();
        dbContext.Add(session);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Running, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.SessionRunning, projected.ReasonCode);
        Assert.False(projected.NeedsHumanAttention);
        Assert.Equal(session.Id, projected.CurrentSessionId);
    }

    /// <summary>
    /// Unclaimed but claimable is ordinary waiting. The distinction from the next test is the whole
    /// point of asking the worker table anything at all.
    /// </summary>
    [Fact]
    public async Task An_unclaimed_session_a_worker_could_take_is_waiting()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Queued work");
        await SeedWorkerAsync(dbContext, "Planner", "Implementer");

        dbContext.Add(NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Waiting, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.AwaitingWorkerPickup, projected.ReasonCode);
        Assert.Contains("Waiting for an available Implementer", projected.ReasonText, StringComparison.Ordinal);
        Assert.False(projected.NeedsHumanAttention);

        // The server knows the role is declared; it cannot know the worker has a local mapping for
        // this project, so it must not promise pickup. Someone told "nothing to do" would wait
        // indefinitely alongside a session that will never be claimed.
        var recommendation = projected.Summary.RecommendedNextAction!;
        Assert.DoesNotContain("Nothing to do", recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shortly", recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local mapping for this project", recommendation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operational trap ADR-0010 named: a session no worker can claim blocks its whole task,
    /// because a task may hold only one Started session. It must ask for a human, not look like
    /// ordinary queueing.
    /// </summary>
    [Fact]
    public async Task An_unclaimed_session_no_worker_can_run_is_blocked_and_asks_for_a_human()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Stuck work");
        await SeedWorkerAsync(dbContext, "Planner");

        dbContext.Add(NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Blocked, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.NoWorkerForRole, projected.ReasonCode);
        Assert.True(projected.NeedsHumanAttention);
        Assert.Contains("No enabled worker declares that role", projected.ReasonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_worker_does_not_count_as_capable()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Disabled-worker work");
        var worker = await SeedWorkerAsync(dbContext, "Implementer");
        worker.Enabled = false;
        dbContext.Add(NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Blocked, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.NoWorkerForRole, projected.ReasonCode);
    }

    [Fact]
    public async Task An_expired_lease_is_waiting_and_recoverable()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Abandoned work");
        var worker = await SeedWorkerAsync(dbContext, "Planner");

        var session = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started);
        session.ClaimedByWorkerId = worker.Id;
        session.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
        session.ClaimId = Guid.NewGuid();
        dbContext.Add(session);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Waiting, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.LeaseExpired, projected.ReasonCode);
        Assert.False(projected.NeedsHumanAttention);
    }

    /// <summary>The Sprint 09 gate, seen from the Demiplane.</summary>
    [Fact]
    public async Task A_pending_handoff_needs_attention_and_carries_its_token()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Proposed work");

        var planner = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        var handoff = NewHandoff(task.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        dbContext.AddRange(planner, handoff);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.NeedsAttention, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.AwaitingHumanApproval, projected.ReasonCode);
        Assert.True(projected.NeedsHumanAttention);
        Assert.Equal(handoff.Id, projected.PendingHandoffId);
        Assert.Equal(handoff.ConcurrencyToken, projected.PendingHandoffToken);
        Assert.Equal(AgentSessionRole.Implementer, projected.ProposedRole);

        // The proposed step appears in the chain as explicitly not started.
        var proposed = Assert.Single(projected.Chain, step => step.IsProposed);
        Assert.Null(proposed.SessionId);
        Assert.Null(proposed.Status);
    }

    [Fact]
    public async Task A_completed_reviewer_asks_a_human_to_decide_rather_than_completing_the_task()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Reviewed work");

        dbContext.Add(NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Completed));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.NeedsAttention, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.AwaitingHumanDecisionAfterReview, projected.ReasonCode);
        Assert.NotEqual(TaskStatus.Completed, projected.TaskStatus);

        // Nothing was declined, so nothing may say so.
        Assert.DoesNotContain("declin", projected.ReasonText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Reviewer that proposed a step which a human declined is not the same as a Reviewer that
    /// proposed nothing, and the generic "Completing this task is your decision" wording must not
    /// swallow the decline.
    ///
    /// Sprint 10's review found the Reviewer branch returning before the recorded-decision switch was
    /// consulted, so a real persisted decline on a Reviewer session was invisible on the page.
    /// </summary>
    [Fact]
    public async Task A_declined_proposal_from_a_reviewer_is_reported_as_declined()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Reviewed then declined");

        var reviewer = NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Completed);
        dbContext.Add(reviewer);

        var handoff = NewHandoff(task.Id, reviewer.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        handoff.Status = SessionHandoffStatus.Declined;
        handoff.DecidedUtc = DateTime.UtcNow.AddMinutes(-1);
        dbContext.Add(handoff);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.NeedsAttention, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.ProposedStepDeclined, projected.ReasonCode);
        Assert.True(projected.NeedsHumanAttention);
        Assert.Contains("declined", projected.ReasonText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Implementer", projected.ReasonText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decline above is reported because a Declined row exists, not because a Reviewer finished.
    /// An Approved decision on the same Reviewer session must not be described as a decline.
    /// </summary>
    [Fact]
    public async Task An_approved_proposal_from_a_reviewer_is_not_described_as_declined()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Reviewed then approved");

        var reviewer = NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Completed);
        dbContext.Add(reviewer);

        var handoff = NewHandoff(task.Id, reviewer.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        handoff.Status = SessionHandoffStatus.Approved;
        handoff.DecidedUtc = DateTime.UtcNow.AddMinutes(-1);
        dbContext.Add(handoff);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayReasonCode.ProposedStepAlreadyDecided, projected.ReasonCode);
        Assert.NotEqual(TaskDisplayReasonCode.ProposedStepDeclined, projected.ReasonCode);
        Assert.DoesNotContain("declin", projected.ReasonText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A decision recorded against an earlier session must never be attributed to a later Reviewer.
    /// Decisions stay scoped to SourceSessionId, so the Reviewer here has no applicable decision and
    /// falls back to its own generic wording.
    /// </summary>
    [Fact]
    public async Task A_decline_on_an_earlier_session_is_not_attributed_to_a_later_reviewer()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Declined early, reviewed later");

        var planner = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        planner.StartedUtc = DateTime.UtcNow.AddHours(-3);
        planner.CompletedUtc = DateTime.UtcNow.AddHours(-2);
        dbContext.Add(planner);

        var reviewer = NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Completed);
        reviewer.StartedUtc = DateTime.UtcNow.AddHours(-1);
        reviewer.CompletedUtc = DateTime.UtcNow.AddMinutes(-10);
        dbContext.Add(reviewer);

        // The decline belongs to the Planner, not the Reviewer that finished afterwards.
        var handoff = NewHandoff(task.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        handoff.Status = SessionHandoffStatus.Declined;
        handoff.DecidedUtc = DateTime.UtcNow.AddHours(-2);
        dbContext.Add(handoff);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayReasonCode.AwaitingHumanDecisionAfterReview, projected.ReasonCode);
        Assert.DoesNotContain("declin", projected.ReasonText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Reviewer with a still-pending proposal is a decision waiting on a human, and outranks the
    /// Reviewer's own completion wording.
    /// </summary>
    [Fact]
    public async Task A_pending_proposal_from_a_reviewer_still_asks_for_approval()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Reviewed, awaiting approval");

        var reviewer = NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Completed);
        dbContext.Add(reviewer);
        dbContext.Add(NewHandoff(task.Id, reviewer.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.NeedsAttention, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.AwaitingHumanApproval, projected.ReasonCode);
        Assert.True(projected.NeedsHumanAttention);
        Assert.NotNull(projected.PendingHandoffId);
    }

    [Theory]
    [InlineData(TaskStatus.Completed, TaskDisplayState.Succeeded, TaskDisplayReasonCode.MarkedCompleteByHuman)]
    [InlineData(TaskStatus.Blocked, TaskDisplayState.Blocked, TaskDisplayReasonCode.MarkedBlockedByHuman)]
    public async Task A_human_decision_about_the_task_outranks_session_history(
        TaskStatus status,
        TaskDisplayState expectedState,
        TaskDisplayReasonCode expectedReason)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Decided work");
        task.Status = status;
        dbContext.Add(NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(expectedState, projected.DisplayState);
        Assert.Equal(expectedReason, projected.ReasonCode);
    }

    /// <summary>
    /// A human's own cancellation is Cancelled, not Failed, and their words are carried through
    /// verbatim rather than classified.
    /// </summary>
    [Fact]
    public async Task A_human_cancellation_is_cancelled_and_shows_the_reason_verbatim()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Abandoned work");

        var session = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Cancelled);
        dbContext.Add(session);
        dbContext.Add(NewCancellationEntry(project.Id, task.Id, session.Id, "I changed my mind about the approach."));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Cancelled, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.CancelledByHuman, projected.ReasonCode);
        Assert.False(projected.NeedsHumanAttention);
        Assert.Equal("I changed my mind about the approach.", projected.Summary.OutcomeDetail);
    }

    /// <summary>
    /// The runner's own fixed diagnostic strings are the only failure signal we recognise, and each
    /// maps to a category a human can act on.
    /// </summary>
    [Theory]
    [InlineData("Runner cancelled: adapter-launch-failed.", TaskDisplayReasonCode.ProviderRuntimeLaunchFailed)]
    [InlineData("Runner cancelled: adapter-timeout.", TaskDisplayReasonCode.ProviderRunTimedOut)]
    [InlineData("Runner cancelled: adapter-non-zero-exit.", TaskDisplayReasonCode.ProviderRequestFailed)]
    [InlineData("Runner cancelled: adapter-output-malformed.", TaskDisplayReasonCode.ProviderResponseUnusable)]
    [InlineData("Runner cancelled: adapter-output-oversized.", TaskDisplayReasonCode.ProviderResponseUnusable)]
    [InlineData("Runner cancelled: adapter-output-invalid.", TaskDisplayReasonCode.ProviderResponseUnusable)]
    public async Task A_machine_recorded_cancellation_is_a_failure_with_a_category(
        string reason,
        TaskDisplayReasonCode expected)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Failed work");

        var session = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Cancelled);
        dbContext.Add(session);
        dbContext.Add(NewCancellationEntry(project.Id, task.Id, session.Id, reason));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Failed, projected.DisplayState);
        Assert.Equal(expected, projected.ReasonCode);
        Assert.True(projected.NeedsHumanAttention);
    }

    /// <summary>
    /// An unrecognised machine category is still a failure, but we do not invent a cause for it.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_machine_cancellation_degrades_to_unknown_not_a_guess()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Novel failure");

        var session = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Cancelled);
        dbContext.Add(session);
        dbContext.Add(NewCancellationEntry(project.Id, task.Id, session.Id, "Runner cancelled: something-new."));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.Failed, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.Unknown, projected.ReasonCode);
        Assert.Contains("does not recognise", projected.ReasonText, StringComparison.Ordinal);
    }

    /// <summary>
    /// No failure the projection can render may claim the provider ran out of capacity.
    ///
    /// The runner records no capacity condition, and the adapter cannot tell a usage-limit rejection
    /// apart from any other non-zero exit, so exhaustion is not a fact this application possesses.
    /// Sprint 10's review removed the WaitingForProviderCapacity code that carried that claim; this
    /// test guards the property the code was standing in for, and does so on the text a human
    /// actually reads rather than on an enum member that can simply be deleted again.
    /// </summary>
    [Theory]
    [InlineData("Runner cancelled: adapter-launch-failed.")]
    [InlineData("Runner cancelled: adapter-timeout.")]
    [InlineData("Runner cancelled: adapter-non-zero-exit.")]
    [InlineData("Runner cancelled: adapter-output-malformed.")]
    [InlineData("Runner cancelled: adapter-output-oversized.")]
    [InlineData("Runner cancelled: adapter-output-invalid.")]
    [InlineData("Runner cancelled: something-new.")]
    public async Task No_rendered_failure_claims_the_provider_ran_out_of_capacity(string reason)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, $"Capacity claim {Guid.NewGuid():N}");

        var session = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Cancelled);
        dbContext.Add(session);
        dbContext.Add(NewCancellationEntry(project.Id, task.Id, session.Id, reason));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        var rendered = string.Join(
            " ",
            projected.ReasonText,
            projected.Summary.WhatHappened,
            projected.Summary.CurrentState,
            projected.Summary.WhyWaiting,
            projected.Summary.NeedsAttention,
            projected.Summary.RecommendedNextAction);

        // Affirmative claims only. ProviderRequestFailed deliberately says a usage limit "would also
        // appear here" and that the adapter "cannot yet tell exhaustion apart from other provider
        // errors" — that is the honest disclaimer, not a capacity claim, and it must keep saying it.
        foreach (var claim in new[]
        {
            "no capacity",
            "capacity left",
            "out of capacity",
            "ran out",
            "allowance",
            "quota"
        })
        {
            Assert.DoesNotContain(claim, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The production-shaped case, and the regression this suite previously lacked entirely.
    ///
    /// Every task that completed a Planner before Sprint 09 existed looks exactly like this: a
    /// completed session, no handoff row, nothing running — because ADR-0010's migration deliberately
    /// backfills no handoffs. Two such tasks exist in the real database.
    ///
    /// The projection must report only what is observable. Absence of a proposal is not evidence that
    /// anyone decided anything, and saying otherwise invents a decision the user never made.
    /// </summary>
    [Fact]
    public async Task A_completed_planner_with_no_handoff_reports_no_proposal_and_claims_no_decision()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Historical work");

        dbContext.Add(NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayReasonCode.NoNextStepProposed, projected.ReasonCode);
        Assert.Equal(
            "The Planner session finished and no next step is currently proposed.",
            projected.ReasonText);

        // No decision vocabulary anywhere the user can read it.
        var everythingShown = string.Join(
            " ",
            projected.ReasonText,
            projected.Summary.WhatHappened,
            projected.Summary.CurrentState,
            projected.Summary.WhyWaiting,
            projected.Summary.NeedsAttention,
            projected.Summary.RecommendedNextAction,
            projected.Summary.OutcomeDetail);

        foreach (var word in new[] { "declined", "rejected", "approved", "decided", "denied" })
        {
            Assert.DoesNotContain(word, everythingShown, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The other half: when a decline really was recorded, say so. The fix must not make the page
    /// silent about decisions that genuinely happened.
    /// </summary>
    [Fact]
    public async Task A_persisted_declined_handoff_is_reported_as_declined()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Declined work");

        var planner = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        var handoff = NewHandoff(task.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        handoff.Status = SessionHandoffStatus.Declined;
        handoff.DecidedUtc = DateTime.UtcNow;
        dbContext.AddRange(planner, handoff);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayReasonCode.ProposedStepDeclined, projected.ReasonCode);
        Assert.Contains("you declined", projected.ReasonText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Implementer", projected.ReasonText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A decline recorded against an earlier session says nothing about the one that just finished.
    /// Scoping the lookup to the latest terminal session is what keeps that straight.
    /// </summary>
    [Fact]
    public async Task A_decline_on_an_earlier_session_is_not_attributed_to_a_later_one()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Two-session work");

        var planner = NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        planner.StartedUtc = DateTime.UtcNow.AddHours(-3);
        planner.CompletedUtc = DateTime.UtcNow.AddHours(-2);

        var plannerDecision = NewHandoff(task.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        plannerDecision.Status = SessionHandoffStatus.Approved;

        // The approved Implementer ran and finished. Nothing has been decided about it yet.
        var implementer = NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Completed);
        implementer.StartedUtc = DateTime.UtcNow.AddHours(-1);
        implementer.CompletedUtc = DateTime.UtcNow.AddMinutes(-10);

        dbContext.AddRange(planner, plannerDecision, implementer);
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayReasonCode.NoNextStepProposed, projected.ReasonCode);
        Assert.Contains("Implementer session finished", projected.ReasonText, StringComparison.Ordinal);
        Assert.DoesNotContain("declined", projected.ReasonText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tasks_from_another_project_never_appear()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var mine = await SeedProjectAsync(dbContext);
        var theirs = await SeedProjectAsync(dbContext);

        await SeedTaskAsync(dbContext, mine, "Mine");
        await SeedTaskAsync(dbContext, theirs, "Theirs");

        var projection = await ProjectAsync(dbContext, mine.Id);

        var task = Assert.Single(projection!.Tasks);
        Assert.Equal("Mine", task.Title);
    }

    [Fact]
    public async Task An_empty_project_renders_safely()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        var projection = await ProjectAsync(dbContext, project.Id);

        Assert.NotNull(projection);
        Assert.Empty(projection.Tasks);
        Assert.Empty(projection.NeedsAttention);
        Assert.False(projection.HasActiveWork);
    }

    [Fact]
    public async Task An_unknown_project_is_null_rather_than_an_empty_plane()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        Assert.Null(await ProjectAsync(dbContext, Guid.NewGuid()));
    }

    /// <summary>Work that needs a human sorts to the top, ahead of everything settled.</summary>
    [Fact]
    public async Task Tasks_needing_a_human_are_ordered_first()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        var done = await SeedTaskAsync(dbContext, project, "Done");
        done.Status = TaskStatus.Completed;

        await SeedTaskAsync(dbContext, project, "Fresh");

        var needsHuman = await SeedTaskAsync(dbContext, project, "Decide me");
        var planner = NewSession(needsHuman.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        dbContext.AddRange(planner, NewHandoff(needsHuman.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole));
        await dbContext.SaveChangesAsync();

        var projection = await ProjectAsync(dbContext, project.Id);

        Assert.Equal("Decide me", projection!.Tasks[0].Title);
        Assert.True(projection.Tasks[0].NeedsHumanAttention);
    }

    /// <summary>
    /// More than one Started session is unreachable through the application since ADR-0010's index,
    /// and it must be surfaced as wrong.
    /// </summary>
    [Fact]
    public async Task Multiple_started_sessions_are_surfaced_as_needing_attention()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        // Dropping the index is how the test produces the state; it is not a claim about how a real
        // database would reach it. The projection cannot tell the difference, which is the point of
        // No_cause_is_asserted_for_multiple_started_sessions below.
        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_AgentSessions_TaskId_Started\";");

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Corrupt work");

        dbContext.AddRange(
            NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started),
            NewSession(task.Id, AgentSessionRole.Reviewer, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        Assert.Equal(TaskDisplayState.NeedsAttention, projected.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.MultipleStartedSessions, projected.ReasonCode);
        Assert.True(projected.NeedsHumanAttention);
    }

    /// <summary>
    /// The multiple-Started explanation must report what was observed and stop there.
    ///
    /// It previously read "This database predates the uniqueness index." The projection has no
    /// evidence for that: it reads AgentSessions alone, never the migration history or the schema.
    /// The Sprint 09 migration also normalises duplicates before creating the index, so a database
    /// that genuinely predated it would have been repaired at startup — making the asserted cause
    /// the least likely one. Any replacement guess (a dropped index, a restore, direct writes,
    /// corruption) would be the same mistake wearing different words.
    /// </summary>
    [Fact]
    public async Task No_cause_is_asserted_for_multiple_started_sessions()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_AgentSessions_TaskId_Started\";");

        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project, "Unexplained work");

        dbContext.AddRange(
            NewSession(task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started),
            NewSession(task.Id, AgentSessionRole.Implementer, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();

        var projected = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        var rendered = string.Join(
            " ",
            projected.ReasonText,
            projected.Summary.WhatHappened,
            projected.Summary.CurrentState,
            projected.Summary.WhyWaiting,
            projected.Summary.NeedsAttention,
            projected.Summary.RecommendedNextAction);

        // It reports the observation.
        Assert.Contains("Multiple sessions are recorded as started", projected.ReasonText, StringComparison.Ordinal);

        // It says the state should not be reachable.
        Assert.Contains("should not be reachable", projected.ReasonText, StringComparison.Ordinal);

        // And it names no cause — neither the old claim nor any substitute for it.
        foreach (var inferredCause in new[]
        {
            "predates",
            "migration",
            "migrated",
            "index",
            "restore",
            "restored",
            "backup",
            "corrupt",
            "direct database",
            "manually",
            "out-of-band",
            "older version",
            "downgrade"
        })
        {
            Assert.DoesNotContain(inferredCause, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Every state must give a human something to read. A bare enum is not an explanation.</summary>
    [Fact]
    public async Task Every_projected_task_carries_a_non_empty_reason_and_summary()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedWorkerAsync(dbContext, "Planner");

        var fresh = await SeedTaskAsync(dbContext, project, "Fresh");
        var running = await SeedTaskAsync(dbContext, project, "Running");
        var failed = await SeedTaskAsync(dbContext, project, "Failed");
        var done = await SeedTaskAsync(dbContext, project, "Done");
        done.Status = TaskStatus.Completed;

        dbContext.Add(NewSession(running.Id, AgentSessionRole.Planner, AgentSessionStatus.Started));

        var failedSession = NewSession(failed.Id, AgentSessionRole.Planner, AgentSessionStatus.Cancelled);
        dbContext.Add(failedSession);
        dbContext.Add(NewCancellationEntry(project.Id, failed.Id, failedSession.Id, "Runner cancelled: adapter-timeout."));
        await dbContext.SaveChangesAsync();
        _ = fresh;

        var projection = await ProjectAsync(dbContext, project.Id);

        Assert.Equal(4, projection!.Tasks.Count);
        Assert.All(projection.Tasks, task =>
        {
            Assert.False(string.IsNullOrWhiteSpace(task.ReasonText));
            Assert.False(string.IsNullOrWhiteSpace(task.Summary.WhatHappened));
            Assert.False(string.IsNullOrWhiteSpace(task.Summary.CurrentState));
        });
    }

    // ---------------------------------------------------------------- helpers

    private static Task<DemiplaneProjection?> ProjectAsync(FamiliarDbContext dbContext, Guid projectId)
    {
        dbContext.ChangeTracker.Clear();
        var service = new DemiplaneProjectionService(
            dbContext,
            new StubProviderCapacityService([]),
            TimeProvider.System);

        return service.GetProjectionAsync(projectId);
    }

    internal sealed class StubProviderCapacityService(IReadOnlyList<ProviderCapacitySnapshot> snapshots)
        : IProviderCapacityService
    {
        public Task<IReadOnlyList<ProviderCapacitySnapshot>> GetAllAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(snapshots);
    }

    internal static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Demiplane project {Guid.NewGuid():N}",
            Purpose = "Seeded for DemiplaneProjectionServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    internal static async Task<FamiliarTask> SeedTaskAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        string title)
    {
        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = title,
            RequestedOutcome = $"Seeded outcome for {title}.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }

    private static async Task<Worker> SeedWorkerAsync(FamiliarDbContext dbContext, params string[] roles)
    {
        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            WorkerKey = $"worker-{Guid.NewGuid():N}",
            DisplayName = "Test worker",
            Enabled = true,
            Capabilities = string.Join(",", roles),
            RegisteredUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow
        };

        dbContext.Add(worker);
        await dbContext.SaveChangesAsync();
        return worker;
    }

    internal static AgentSession NewSession(Guid taskId, AgentSessionRole role, AgentSessionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        Role = role,
        Status = status,
        ContextRevisionRead = 1,
        StartedUtc = DateTime.UtcNow.AddMinutes(-30),
        CompletedUtc = status == AgentSessionStatus.Started ? null : DateTime.UtcNow.AddMinutes(-5)
    };

    internal static SessionHandoff NewHandoff(
        Guid taskId,
        Guid sourceSessionId,
        AgentSessionRole proposedRole,
        SessionHandoffKind kind) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        SourceSessionId = sourceSessionId,
        SourceOutcome = kind == SessionHandoffKind.RetrySameRole
            ? AgentSessionStatus.Cancelled
            : AgentSessionStatus.Completed,
        ProposedRole = proposedRole,
        Kind = kind,
        Status = SessionHandoffStatus.Pending,
        ObservedContextRevision = 1,
        ConcurrencyToken = Guid.NewGuid(),
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static ContextEntry NewCancellationEntry(
        Guid projectId,
        Guid taskId,
        Guid sessionId,
        string reason) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        TaskId = taskId,
        SourceSessionId = sessionId,
        Kind = ContextEntryKind.Handoff,
        Title = "Planner session cancelled",
        Content = reason,
        State = ContextEntryState.Active,
        CreatedUtc = DateTime.UtcNow
    };
}
