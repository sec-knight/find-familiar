using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using DemiplaneModel = FindFamiliar.Server.Pages.DemiplaneModel;
using TaskDetailsModel = FindFamiliar.Server.Pages.Tasks.DetailsModel;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// What each decision outcome is allowed to tell a human.
///
/// Sprint 10's review found the generic <see cref="SessionHandoffDecisionStatus.Conflict"/> outcome
/// saying "Another change reached this step first". That status is the fall-through for foreign-key
/// violations, disk and I/O errors, and other EF or SQLite faults where no second actor exists — so
/// on those failures the sentence invents a competitor and sends the user looking for a decision
/// nobody made. It is the same defect the DatabaseBusy fix corrected, one layer down.
///
/// Race wording is now reserved for the statuses that establish a real competitor. Every outcome must
/// still say accurately whether anything changed, because that is what a user decides on next.
/// </summary>
public sealed class DecisionOutcomeWordingTests
{
    /// <summary>Phrases that assert a second actor reached the work first.</summary>
    private static readonly string[] CompetingActorClaims =
    [
        "another change",
        "another request",
        "reached this step first",
        "reached this first",
        "at the same time",
        "someone else",
        "somebody else"
    ];

    private static SessionHandoffDecisionOutcome Outcome(SessionHandoffDecisionStatus status) =>
        new(status, TaskId: Guid.NewGuid(), Role: AgentSessionRole.Implementer);

    // ------------------------------------------------------------ genuine races keep race wording

    /// <summary>
    /// These statuses are only reachable by re-reading committed state and finding a decision that
    /// actually happened, so they may — and should — name it.
    /// </summary>
    [Theory]
    [InlineData(SessionHandoffDecisionStatus.AlreadyApproved, "already approved")]
    [InlineData(SessionHandoffDecisionStatus.AlreadyDeclined, "already declined")]
    [InlineData(SessionHandoffDecisionStatus.Superseded, "replaced by a newer one")]
    [InlineData(SessionHandoffDecisionStatus.SessionAlreadyStarted, "already has")]
    public void A_genuine_race_still_says_what_won(SessionHandoffDecisionStatus status, string expected)
    {
        Assert.Contains(expected, DemiplaneModel.Describe(Outcome(status)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expected, TaskDetailsModel.DescribeDecision(Outcome(status)), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------- the generic fall-through claims no competing actor

    [Fact]
    public void A_generic_conflict_claims_no_competing_actor_on_the_demiplane()
    {
        var message = DemiplaneModel.Describe(Outcome(SessionHandoffDecisionStatus.Conflict));

        Assert.Equal("This step could not be completed, and nothing was changed.", message);
        AssertNoCompetingActorClaim(message);
    }

    [Fact]
    public void A_generic_conflict_claims_no_competing_actor_on_the_task_page()
    {
        var message = TaskDetailsModel.DescribeDecision(Outcome(SessionHandoffDecisionStatus.Conflict));

        Assert.Equal("This step could not be completed, and nothing was changed.", message);
        AssertNoCompetingActorClaim(message);
    }

    /// <summary>
    /// Approve and decline reach the same fall-through for the same reasons, and neither knows more
    /// than the other about what failed.
    /// </summary>
    [Fact]
    public void The_generic_conflict_wording_does_not_depend_on_which_button_was_pressed()
    {
        Assert.Equal(
            DemiplaneModel.Describe(Outcome(SessionHandoffDecisionStatus.Conflict)),
            TaskDetailsModel.DescribeDecision(Outcome(SessionHandoffDecisionStatus.Conflict)));
    }

    // ------------------------------------------------------------------ busy stays clearly retryable

    [Fact]
    public void Database_busy_stays_explicitly_retryable_and_claims_no_competitor()
    {
        foreach (var message in new[]
        {
            DemiplaneModel.Describe(Outcome(SessionHandoffDecisionStatus.DatabaseBusy)),
            TaskDetailsModel.DescribeDecision(Outcome(SessionHandoffDecisionStatus.DatabaseBusy))
        })
        {
            Assert.Contains("busy", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("try again", message, StringComparison.OrdinalIgnoreCase);
            AssertNoCompetingActorClaim(message);
        }
    }

    // ------------------------------------------------- every outcome says whether anything changed

    /// <summary>
    /// A user's next move depends on whether their click did anything. Every non-success outcome must
    /// say so, and the two success outcomes must describe what they actually did.
    /// </summary>
    [Theory]
    [InlineData(SessionHandoffDecisionStatus.Declined)]
    [InlineData(SessionHandoffDecisionStatus.AlreadyApproved)]
    [InlineData(SessionHandoffDecisionStatus.AlreadyDeclined)]
    [InlineData(SessionHandoffDecisionStatus.Superseded)]
    [InlineData(SessionHandoffDecisionStatus.StaleHandoff)]
    [InlineData(SessionHandoffDecisionStatus.SessionAlreadyStarted)]
    [InlineData(SessionHandoffDecisionStatus.TaskClosed)]
    [InlineData(SessionHandoffDecisionStatus.ProjectInactive)]
    [InlineData(SessionHandoffDecisionStatus.DatabaseBusy)]
    [InlineData(SessionHandoffDecisionStatus.Conflict)]
    public void Every_non_approval_outcome_states_that_nothing_started_or_changed(
        SessionHandoffDecisionStatus status)
    {
        foreach (var message in new[]
        {
            DemiplaneModel.Describe(Outcome(status)),
            TaskDetailsModel.DescribeDecision(Outcome(status))
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(message));

            var statesTheEffect =
                message.Contains("nothing was started", StringComparison.OrdinalIgnoreCase)
                || message.Contains("nothing new was started", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no further work was started", StringComparison.OrdinalIgnoreCase)
                || message.Contains("nothing was changed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("nothing changed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already declined", StringComparison.OrdinalIgnoreCase)
                || message.Contains("review the current proposal", StringComparison.OrdinalIgnoreCase);

            Assert.True(statesTheEffect, $"'{status}' must say what it did or did not change: \"{message}\"");
        }
    }

    /// <summary>Approval is the one outcome that did change something, and says so.</summary>
    [Fact]
    public void Approval_reports_that_a_session_was_created()
    {
        var approved = new SessionHandoffDecisionOutcome(
            SessionHandoffDecisionStatus.Approved,
            SessionId: Guid.NewGuid(),
            TaskId: Guid.NewGuid(),
            Role: AgentSessionRole.Implementer);

        Assert.Contains("implementer", DemiplaneModel.Describe(approved), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("implementer", TaskDetailsModel.DescribeDecision(approved), StringComparison.OrdinalIgnoreCase);

        // It may say a worker *may* claim the session, never that one will.
        Assert.DoesNotContain("will be claimed", DemiplaneModel.Describe(approved), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No outcome on either surface may claim a competitor unless it is one of the statuses that
    /// established one. This is the property, stated once over the whole enum.
    /// </summary>
    [Fact]
    public void Only_race_establishing_statuses_may_claim_a_competitor()
    {
        var raceEstablishing = new[]
        {
            SessionHandoffDecisionStatus.AlreadyApproved,
            SessionHandoffDecisionStatus.AlreadyDeclined,
            SessionHandoffDecisionStatus.Superseded,
            SessionHandoffDecisionStatus.SessionAlreadyStarted
        };

        foreach (var status in Enum.GetValues<SessionHandoffDecisionStatus>())
        {
            if (raceEstablishing.Contains(status) || status == SessionHandoffDecisionStatus.NotFound)
            {
                continue;
            }

            AssertNoCompetingActorClaim(DemiplaneModel.Describe(Outcome(status)));
            AssertNoCompetingActorClaim(TaskDetailsModel.DescribeDecision(Outcome(status)));
        }
    }

    private static void AssertNoCompetingActorClaim(string message)
    {
        foreach (var claim in CompetingActorClaims)
        {
            Assert.DoesNotContain(claim, message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
