using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Tests.Infrastructure;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The work queue and the Demiplane read the same rows to answer different questions — "what is the
/// next action across all projects" and "what is true about this project". Two derivations over one
/// dataset is exactly the drift ADR-0009 warned about, so the states they share are pinned together
/// here.
///
/// If someone changes one derivation and not the other, a task will say "waiting for your approval"
/// on one page and something else on the other, and these tests fail rather than the user finding out.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class DemiplaneWorkQueueConsistencyTests
{
    [Fact]
    public async Task A_pending_handoff_is_an_approval_action_and_a_needs_attention_state()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await DemiplaneProjectionServiceTests.SeedProjectAsync(dbContext);
        var task = await DemiplaneProjectionServiceTests.SeedTaskAsync(dbContext, project, "Shared truth");

        var planner = DemiplaneProjectionServiceTests.NewSession(
            task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        dbContext.AddRange(
            planner,
            DemiplaneProjectionServiceTests.NewHandoff(
                task.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var queueItem = Assert.Single(await new WorkQueueService(dbContext).GetActiveQueueAsync());
        Assert.Equal(WorkQueueActionKind.ApproveHandoff, queueItem.ActionKind);

        var demiplaneTask = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);
        Assert.Equal(TaskDisplayState.NeedsAttention, demiplaneTask.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.AwaitingHumanApproval, demiplaneTask.ReasonCode);

        // Both agree on the role being proposed.
        Assert.Equal(queueItem.PendingHandoffRole, demiplaneTask.ProposedRole);
    }

    [Fact]
    public async Task A_started_session_is_continue_on_the_queue_and_running_or_waiting_on_the_plane()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await DemiplaneProjectionServiceTests.SeedProjectAsync(dbContext);
        var task = await DemiplaneProjectionServiceTests.SeedTaskAsync(dbContext, project, "In flight");

        dbContext.Add(DemiplaneProjectionServiceTests.NewSession(
            task.Id, AgentSessionRole.Planner, AgentSessionStatus.Started));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var queueItem = Assert.Single(await new WorkQueueService(dbContext).GetActiveQueueAsync());
        Assert.Equal(WorkQueueActionKind.ContinueSession, queueItem.ActionKind);

        var demiplaneTask = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        // The queue says "there is an active session"; the plane says why it is not progressing.
        // Both are true, and neither claims the task is finished or idle.
        Assert.Contains(
            demiplaneTask.DisplayState,
            new[] { TaskDisplayState.Running, TaskDisplayState.Waiting, TaskDisplayState.Blocked });
        Assert.Equal(queueItem.ActiveSessionRole, AgentSessionRole.Planner);
    }

    [Fact]
    public async Task A_task_with_no_sessions_is_start_planner_and_not_started()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await DemiplaneProjectionServiceTests.SeedProjectAsync(dbContext);
        await DemiplaneProjectionServiceTests.SeedTaskAsync(dbContext, project, "Untouched");
        dbContext.ChangeTracker.Clear();

        var queueItem = Assert.Single(await new WorkQueueService(dbContext).GetActiveQueueAsync());
        Assert.Equal(WorkQueueActionKind.StartPlanner, queueItem.ActionKind);

        var demiplaneTask = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);
        Assert.Equal(TaskDisplayState.NotStarted, demiplaneTask.DisplayState);
    }

    /// <summary>
    /// The queue lists only unfinished work; the Demiplane shows the whole project including what is
    /// done. That difference is deliberate, and asserting it stops someone "fixing" one to match.
    /// </summary>
    [Fact]
    public async Task A_completed_task_leaves_the_queue_but_stays_visible_on_the_plane()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await DemiplaneProjectionServiceTests.SeedProjectAsync(dbContext);
        var task = await DemiplaneProjectionServiceTests.SeedTaskAsync(dbContext, project, "Finished");
        task.Status = TaskStatus.Completed;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Empty(await new WorkQueueService(dbContext).GetActiveQueueAsync());

        var demiplaneTask = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);
        Assert.Equal(TaskDisplayState.Succeeded, demiplaneTask.DisplayState);
    }

    /// <summary>
    /// The case the two derivations previously disagreed on, and the gap this suite previously had.
    ///
    /// For a completed Planner with no handoff, WorkQueueService reports the neutral, accurate
    /// "Start an Implementer session." The Demiplane used to report that the proposed next step "was
    /// declined" — a decision nobody made, and one the queue never claimed.
    ///
    /// This asserts semantic agreement rather than matching enum values: the two pages describe one
    /// task, and neither may assert an event the other does not.
    /// </summary>
    [Fact]
    public async Task A_completed_planner_with_no_handoff_is_neutral_on_both_surfaces()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await DemiplaneProjectionServiceTests.SeedProjectAsync(dbContext);
        var task = await DemiplaneProjectionServiceTests.SeedTaskAsync(dbContext, project, "Historical");

        dbContext.Add(DemiplaneProjectionServiceTests.NewSession(
            task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var queueItem = Assert.Single(await new WorkQueueService(dbContext).GetActiveQueueAsync());
        var demiplaneTask = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        // The queue proposes a next role without claiming anything happened.
        Assert.Equal(WorkQueueActionKind.StartImplementer, queueItem.ActionKind);
        Assert.DoesNotContain("declin", queueItem.ActionLabel, StringComparison.OrdinalIgnoreCase);

        // The Demiplane must be equally neutral. If it invents a decline while the queue reports a
        // plain next action, the two are describing different histories of the same task.
        Assert.Equal(TaskDisplayReasonCode.NoNextStepProposed, demiplaneTask.ReasonCode);

        var demiplaneText = string.Join(
            " ",
            demiplaneTask.ReasonText,
            demiplaneTask.Summary.WhatHappened,
            demiplaneTask.Summary.CurrentState,
            demiplaneTask.Summary.WhyWaiting,
            demiplaneTask.Summary.NeedsAttention,
            demiplaneTask.Summary.RecommendedNextAction);

        foreach (var word in new[] { "declined", "rejected", "approved", "decided" })
        {
            Assert.DoesNotContain(word, demiplaneText, StringComparison.OrdinalIgnoreCase);
        }

        // Neither surface claims a proposal is outstanding, because none is.
        Assert.Null(queueItem.PendingHandoffRole);
        Assert.Null(demiplaneTask.PendingHandoffId);
        Assert.Null(demiplaneTask.ProposedRole);
    }

    /// <summary>
    /// The mirror: when a decline is persisted, both surfaces may reflect it and the Demiplane says so.
    /// </summary>
    [Fact]
    public async Task A_persisted_decline_is_reported_by_the_plane_and_invented_by_neither()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await DemiplaneProjectionServiceTests.SeedProjectAsync(dbContext);
        var task = await DemiplaneProjectionServiceTests.SeedTaskAsync(dbContext, project, "Declined");

        var planner = DemiplaneProjectionServiceTests.NewSession(
            task.Id, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        var handoff = DemiplaneProjectionServiceTests.NewHandoff(
            task.Id, planner.Id, AgentSessionRole.Implementer, SessionHandoffKind.NextRole);
        handoff.Status = SessionHandoffStatus.Declined;
        dbContext.AddRange(planner, handoff);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var queueItem = Assert.Single(await new WorkQueueService(dbContext).GetActiveQueueAsync());
        var demiplaneTask = Assert.Single((await ProjectAsync(dbContext, project.Id))!.Tasks);

        // The queue does not surface a declined handoff as pending work.
        Assert.Null(queueItem.PendingHandoffRole);
        Assert.NotEqual(WorkQueueActionKind.ApproveHandoff, queueItem.ActionKind);

        // The Demiplane reports the decision that genuinely exists.
        Assert.Equal(TaskDisplayReasonCode.ProposedStepDeclined, demiplaneTask.ReasonCode);
        Assert.Null(demiplaneTask.PendingHandoffId);
    }

    private static Task<DemiplaneProjection?> ProjectAsync(
        FindFamiliar.Server.Data.FamiliarDbContext dbContext,
        Guid projectId)
    {
        dbContext.ChangeTracker.Clear();
        return new DemiplaneProjectionService(
                dbContext,
                new DemiplaneProjectionServiceTests.StubProviderCapacityService([]),
                TimeProvider.System)
            .GetProjectionAsync(projectId);
    }
}
