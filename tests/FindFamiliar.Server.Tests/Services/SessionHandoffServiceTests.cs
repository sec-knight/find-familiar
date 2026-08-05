using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The role-progression rule (ADR-0010). It is a pure function of role and terminal status, and must
/// stay that way: deriving the next step from a summary or a review verdict would let a worker's own
/// output advance work without human sign-off, which ADR-0005 rejected.
///
/// It must also be total. Staging runs inside the result-capture transaction, so a throw here would
/// fail the capture of work that already happened.
/// </summary>
public sealed class SessionHandoffServiceTests
{
    [Theory]
    [InlineData(AgentSessionRole.Planner, AgentSessionRole.Implementer)]
    [InlineData(AgentSessionRole.Implementer, AgentSessionRole.Reviewer)]
    public void A_completed_session_proposes_the_next_role(AgentSessionRole completed, AgentSessionRole expected)
    {
        var proposal = SessionHandoffService.Propose(completed, AgentSessionStatus.Completed);

        Assert.NotNull(proposal);
        Assert.Equal(expected, proposal.Value.Role);
        Assert.Equal(SessionHandoffKind.NextRole, proposal.Value.Kind);
    }

    /// <summary>
    /// The chain ends here. What happens to a reviewed task is a human decision about the task
    /// itself, which ADR-0003 and ADR-0005 both keep out of the software's hands.
    /// </summary>
    [Fact]
    public void A_completed_reviewer_proposes_nothing()
    {
        Assert.Null(SessionHandoffService.Propose(AgentSessionRole.Reviewer, AgentSessionStatus.Completed));
    }

    [Theory]
    [InlineData(AgentSessionRole.Planner)]
    [InlineData(AgentSessionRole.Implementer)]
    [InlineData(AgentSessionRole.Reviewer)]
    public void A_cancelled_session_proposes_a_retry_of_the_same_role(AgentSessionRole role)
    {
        var proposal = SessionHandoffService.Propose(role, AgentSessionStatus.Cancelled);

        Assert.NotNull(proposal);
        Assert.Equal(role, proposal.Value.Role);
        Assert.Equal(SessionHandoffKind.RetrySameRole, proposal.Value.Kind);
    }

    /// <summary>
    /// A Started session is not terminal, so it proposes nothing rather than throwing. Totality is
    /// the property that keeps a staging bug from failing result capture.
    /// </summary>
    [Fact]
    public void Every_role_and_status_combination_is_answered_without_throwing()
    {
        foreach (var role in Enum.GetValues<AgentSessionRole>())
        {
            foreach (var status in Enum.GetValues<AgentSessionStatus>())
            {
                var proposal = SessionHandoffService.Propose(role, status);

                if (status == AgentSessionStatus.Started)
                {
                    Assert.Null(proposal);
                }
            }
        }
    }
}
