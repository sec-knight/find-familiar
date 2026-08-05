using FindFamiliar.Server.Domain;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services.Demiplane;

/// <summary>
/// Writes the Familiar's account of a task in plain language.
///
/// Every sentence is assembled from persisted facts: which sessions exist, what roles they had, what
/// state they reached, and the durable cancellation reason our own runner recorded. Nothing is
/// inferred from a summary artifact, a raw output, or a review verdict — those are model-authored, and
/// ADR-0005 rejected treating them as workflow signal.
///
/// Where the data cannot support a statement the field is null and the page shows an explicit
/// "not recorded" rather than a plausible sentence. Build and test outcomes are the clearest example:
/// this project persists no structured build or test result, so the Familiar never claims one.
/// </summary>
public static class FamiliarSummaryComposer
{
    public static FamiliarSummary Compose(
        FamiliarTask task,
        IReadOnlyList<AgentSession> ordered,
        AgentSession? latestTerminal,
        AgentSession? current,
        SessionHandoff? pendingHandoff,
        TaskDisplayState state,
        TaskDisplayReasonCode reasonCode,
        string reasonText,
        string? outcomeDetail)
    {
        return new FamiliarSummary(
            WhatHappened: DescribeHistory(ordered, latestTerminal),
            CurrentState: DescribeCurrent(state, current, task),
            WhyWaiting: state is TaskDisplayState.Waiting or TaskDisplayState.Blocked or TaskDisplayState.NeedsAttention
                ? reasonText
                : null,
            NeedsAttention: DescribeAttention(state, reasonCode, pendingHandoff),
            RecommendedNextAction: RecommendNext(state, reasonCode, pendingHandoff, latestTerminal),
            OutcomeDetail: outcomeDetail);
    }

    private static string DescribeHistory(IReadOnlyList<AgentSession> ordered, AgentSession? latestTerminal)
    {
        if (ordered.Count == 0)
        {
            return "Nothing has run for this task yet.";
        }

        var completed = ordered.Count(session => session.Status == AgentSessionStatus.Completed);
        var cancelled = ordered.Count(session => session.Status == AgentSessionStatus.Cancelled);

        var roles = string.Join(
            ", ",
            ordered
                .Where(session => session.Status == AgentSessionStatus.Completed)
                .Select(session => session.Role.ToString()));

        var parts = new List<string>();

        parts.Add(completed switch
        {
            0 => "No session has completed.",
            1 => $"One session completed: {roles}.",
            _ => $"{completed} sessions completed: {roles}."
        });

        if (cancelled > 0)
        {
            parts.Add(cancelled == 1
                ? "One session ended early."
                : $"{cancelled} sessions ended early.");
        }

        if (latestTerminal is not null)
        {
            parts.Add($"The most recent was a {latestTerminal.Role} session.");
        }

        return string.Join(" ", parts);
    }

    private static string DescribeCurrent(TaskDisplayState state, AgentSession? current, FamiliarTask task) =>
        state switch
        {
            TaskDisplayState.Running when current is not null =>
                $"A {current.Role} session is running now.",

            TaskDisplayState.Running => "A session is running now.",
            TaskDisplayState.NotStarted => "Not started.",
            TaskDisplayState.Succeeded => "Complete.",
            TaskDisplayState.Blocked => "Blocked.",
            TaskDisplayState.Failed => "Stopped by a failure.",
            TaskDisplayState.Cancelled => "Stopped deliberately.",
            TaskDisplayState.NeedsAttention => "Waiting for you.",
            TaskDisplayState.Waiting => "Waiting.",
            _ => task.Status.ToString()
        };

    private static string? DescribeAttention(
        TaskDisplayState state,
        TaskDisplayReasonCode reasonCode,
        SessionHandoff? pendingHandoff) =>
        (state, reasonCode) switch
        {
            (_, TaskDisplayReasonCode.AwaitingHumanApproval) when pendingHandoff is not null =>
                $"Approve or decline the proposed {pendingHandoff.ProposedRole} session.",

            (_, TaskDisplayReasonCode.AwaitingHumanDecisionAfterReview) =>
                "Decide whether this task is complete.",

            (_, TaskDisplayReasonCode.NoWorkerForRole) =>
                "Enable a worker that declares this role, or cancel the session so the task is not stuck.",

            (_, TaskDisplayReasonCode.ProposedStepDeclined) =>
                "Decide what should happen next: start a session yourself, or complete the task.",

            (_, TaskDisplayReasonCode.MultipleStartedSessions) =>
                "Inspect this task directly. This state should not be reachable.",

            (TaskDisplayState.Failed, _) =>
                "Review the failure and decide whether to retry.",

            _ => null
        };

    private static string? RecommendNext(
        TaskDisplayState state,
        TaskDisplayReasonCode reasonCode,
        SessionHandoff? pendingHandoff,
        AgentSession? latestTerminal) =>
        (state, reasonCode) switch
        {
            (_, TaskDisplayReasonCode.AwaitingHumanApproval) when pendingHandoff is not null =>
                $"Approve {pendingHandoff.ProposedRole}.",

            (_, TaskDisplayReasonCode.AwaitingHumanDecisionAfterReview) =>
                "Mark the task complete, or record what still needs doing.",

            (_, TaskDisplayReasonCode.NoWorkerForRole) =>
                "Add the missing role to a worker's capabilities.",

            (_, TaskDisplayReasonCode.AwaitingWorkerPickup) =>
                "Nothing to do — a worker should claim this shortly.",

            (_, TaskDisplayReasonCode.LeaseExpired) =>
                "Nothing to do — the session becomes claimable again on its own.",

            (_, TaskDisplayReasonCode.SessionRunning) =>
                "Wait for the session to finish.",

            (_, TaskDisplayReasonCode.NeverStarted) =>
                "Start a Planner session when you are ready.",

            (_, TaskDisplayReasonCode.WaitingForProviderCapacity) =>
                "Wait for the provider allowance to reset, or route the continuation to another provider.",

            (TaskDisplayState.Failed, _) when latestTerminal is not null =>
                $"Retry the {latestTerminal.Role} session, or cancel the task.",

            (TaskDisplayState.Cancelled, _) when latestTerminal is not null =>
                $"Start a new {latestTerminal.Role} session if the work is still wanted.",

            (TaskDisplayState.Succeeded, _) => null,
            (TaskDisplayState.Blocked, TaskDisplayReasonCode.MarkedBlockedByHuman) =>
                "Unblock the task when whatever it is waiting on is resolved.",

            _ => null
        };
}
