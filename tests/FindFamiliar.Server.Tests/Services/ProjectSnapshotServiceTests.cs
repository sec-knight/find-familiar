using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The snapshot is the only thing a reasoning provider will ever see of a project. Two properties
/// therefore matter more than any field in it: what it contains belongs to this project and nothing
/// else, and what it leaves out is stated rather than silently missing.
///
/// These tests are written against the published constants rather than literal numbers, so a change
/// to a bound is a change to one line that every test then follows — and a change to the *policy*
/// (what gets dropped first, what a drop must announce) fails loudly.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProjectSnapshotServiceTests
{
    [Fact]
    public async Task A_project_that_does_not_exist_has_no_snapshot()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var result = await CreateService(dbContext).GetSnapshotAsync(Guid.NewGuid());

        Assert.Equal(ProjectSnapshotOutcome.ProjectNotFound, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    /// <summary>
    /// The isolation test, written first. A task, session, handoff or context entry belonging to
    /// another project reaching this snapshot would mean one project's records were sent to a
    /// provider while a human was asking about a different one.
    /// </summary>
    [Fact]
    public async Task A_snapshot_contains_only_the_records_of_the_project_asked_for()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var subject = await SeedProjectAsync(dbContext, "Subject project", "Subject purpose.");
        var other = await SeedProjectAsync(dbContext, "Other project", "Other purpose.");

        var subjectTask = await SeedTaskAsync(dbContext, subject, "Subject task");
        var otherTask = await SeedTaskAsync(dbContext, other, "OTHER-TASK-TITLE");

        var subjectSession = await SeedSessionAsync(dbContext, subjectTask, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        var otherSession = await SeedSessionAsync(dbContext, otherTask, AgentSessionRole.Planner, AgentSessionStatus.Completed);

        await SeedHandoffAsync(dbContext, subjectTask, subjectSession, AgentSessionRole.Implementer);
        await SeedHandoffAsync(dbContext, otherTask, otherSession, AgentSessionRole.Implementer);

        await SeedContextEntryAsync(dbContext, subject, subjectTask, "Subject entry", "Subject content.");
        await SeedContextEntryAsync(dbContext, other, otherTask, "OTHER-ENTRY-TITLE", "OTHER-ENTRY-CONTENT");

        var snapshot = await RequireSnapshotAsync(dbContext, subject.Id);

        Assert.Equal(subject.Id, snapshot.ProjectId);
        Assert.Equal("Subject project", snapshot.ProjectName);
        Assert.Equal(subjectTask.Id, Assert.Single(snapshot.Tasks).TaskId);
        Assert.Equal(subjectSession.Id, Assert.Single(snapshot.Sessions).SessionId);
        Assert.Equal(subjectTask.Id, Assert.Single(snapshot.PendingHandoffs).TaskId);
        Assert.Equal("Subject entry", Assert.Single(snapshot.ContextEntries).Title);

        // Serialized, because a leak through any nested field is the same leak.
        var serialized = Serialize(snapshot);
        Assert.DoesNotContain("OTHER-TASK-TITLE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER-ENTRY-TITLE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER-ENTRY-CONTENT", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherTask.Id.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherSession.Id.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ADR-0011 put every display-state rule in one place. The snapshot must consume that answer,
    /// not compute a second one: a projection saying "Blocked" and a snapshot saying "Waiting" about
    /// the same row is the drift that ADR forbids, and the provider would only ever see the wrong one.
    /// </summary>
    [Fact]
    public async Task Task_display_state_is_taken_from_the_demiplane_projection()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Stated by the projection");
        var task = await SeedTaskAsync(dbContext, project, "Quiet task");

        // The persisted row has no session at all, so re-derivation could only ever say NotStarted.
        var projection = ProjectionFor(
            project,
            TaskIn(task.Id, "Quiet task", TaskDisplayState.Blocked, TaskDisplayReasonCode.NoWorkerForRole,
                "Waiting for a worker that can run Implementer.", needsAttention: true,
                recommendation: "Add the missing role to a worker's capabilities."));

        var stub = new StubProjectionService(projection);
        var snapshot = await RequireSnapshotAsync(dbContext, project.Id, stub);

        Assert.Equal(1, stub.Calls);
        var snapshotTask = Assert.Single(snapshot.Tasks);
        Assert.Equal(TaskDisplayState.Blocked, snapshotTask.DisplayState);
        Assert.Equal(TaskDisplayReasonCode.NoWorkerForRole, snapshotTask.ReasonCode);
        Assert.Contains("Waiting for a worker", snapshotTask.ReasonText, StringComparison.Ordinal);
        Assert.True(snapshotTask.NeedsHumanAttention);
        Assert.Equal(1, snapshot.Health.CountOf(TaskDisplayState.Blocked));
    }

    /// <summary>
    /// A task with no handoff row has had no decision made about it. The snapshot must record that
    /// absence as absence — the moment it says "not approved" or "no decision was declined", it has
    /// invented an event for every task that predates Sprint 09.
    /// </summary>
    [Fact]
    public async Task Absence_of_a_handoff_is_not_reported_as_a_decision()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Quiet project");
        var task = await SeedTaskAsync(dbContext, project, "Finished with nothing proposed");
        await SeedSessionAsync(dbContext, task, AgentSessionRole.Planner, AgentSessionStatus.Completed);

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        Assert.Empty(snapshot.PendingHandoffs);

        var serialized = Serialize(snapshot);
        foreach (var invented in new[] { "approved", "declined", "rejected", "dismissed", "decided" })
        {
            Assert.DoesNotContain(invented, serialized, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("handoff", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Caps on tasks, sessions and handoffs, and on the project's purpose.
    ///
    /// Context entries are measured separately, and both tests assert they stayed within budget: a
    /// project big enough to trip the reduction policy would empty a collection for a reason that
    /// has nothing to do with the cap under test, and the assertion would still pass or fail for
    /// the wrong reason.
    /// </summary>
    [Fact]
    public async Task Task_session_and_handoff_collections_obey_their_documented_limits()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(
            dbContext,
            "Crowded project",
            new string('p', ProjectSnapshot.MaxProjectPurposeCharacters + 500));

        for (var index = 0; index < ProjectSnapshot.MaxTasks + 5; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2}");
            var session = await SeedSessionAsync(
                dbContext,
                task,
                AgentSessionRole.Planner,
                AgentSessionStatus.Completed,
                startedUtc: BaseTime.AddMinutes(index));

            await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);
        }

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        Assert.True(snapshot.IsWithinBudget);
        Assert.Equal(ProjectSnapshot.MaxTasks, snapshot.Tasks.Count);
        Assert.Equal(ProjectSnapshot.MaxSessions, snapshot.Sessions.Count);
        Assert.Equal(ProjectSnapshot.MaxPendingHandoffs, snapshot.PendingHandoffs.Count);
        Assert.Equal(ProjectSnapshot.MaxProjectPurposeCharacters, snapshot.ProjectPurpose.Length);
        Assert.True(snapshot.ProjectPurposeTruncated);

        // The health counts describe the whole project, not the twenty tasks that fitted.
        Assert.Equal(ProjectSnapshot.MaxTasks + 5, snapshot.Health.TotalTasks);

        // The most recent sessions, not an arbitrary ten.
        Assert.Equal(
            snapshot.Sessions.Select(session => session.StartedUtc).OrderByDescending(started => started).ToList(),
            snapshot.Sessions.Select(session => session.StartedUtc).ToList());
        Assert.Equal(
            BaseTime.AddMinutes(ProjectSnapshot.MaxTasks + 4),
            snapshot.Sessions[0].StartedUtc);
    }

    [Fact]
    public async Task Context_entries_are_capped_at_the_most_recent_and_excerpted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Talkative project");

        for (var index = 0; index < ProjectSnapshot.MaxContextEntries + 5; index++)
        {
            await SeedContextEntryAsync(
                dbContext,
                project,
                task: null,
                $"Entry {index:D2}",
                new string('c', ProjectSnapshot.MaxContextExcerptCharacters + 100),
                createdUtc: BaseTime.AddMinutes(index));
        }

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        Assert.True(snapshot.IsWithinBudget);
        Assert.Equal(ProjectSnapshot.MaxContextEntries, snapshot.ContextEntries.Count);

        foreach (var entry in snapshot.ContextEntries)
        {
            Assert.Equal(ProjectSnapshot.MaxContextExcerptCharacters, entry.Excerpt.Length);
            Assert.True(entry.ExcerptTruncated);
        }

        Assert.Equal(
            Enumerable.Range(0, ProjectSnapshot.MaxContextEntries)
                .Select(offset => $"Entry {ProjectSnapshot.MaxContextEntries + 4 - offset:D2}")
                .ToList(),
            snapshot.ContextEntries.Select(entry => entry.Title).ToList());
    }

    [Fact]
    public async Task Every_omitted_or_truncated_category_states_a_limitation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(
            dbContext,
            "Crowded project",
            new string('p', ProjectSnapshot.MaxProjectPurposeCharacters + 500));

        for (var index = 0; index < ProjectSnapshot.MaxTasks + 7; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2}");
            var session = await SeedSessionAsync(
                dbContext,
                task,
                AgentSessionRole.Planner,
                AgentSessionStatus.Completed,
                startedUtc: BaseTime.AddMinutes(index));

            await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);
        }

        for (var index = 0; index < ProjectSnapshot.MaxContextEntries + 3; index++)
        {
            await SeedContextEntryAsync(
                dbContext,
                project,
                task: null,
                $"Entry {index:D2}",
                // One entry long enough to be excerpted. Making them all long would put this project
                // over the character budget and change which limitation the test is measuring.
                index == ProjectSnapshot.MaxContextEntries + 2
                    ? new string('c', ProjectSnapshot.MaxContextExcerptCharacters + 10)
                    : "Short content.",
                createdUtc: BaseTime.AddMinutes(index));
        }

        await SeedContextEntryAsync(
            dbContext,
            project,
            task: null,
            "Superseded entry",
            "Replaced by a later entry.",
            state: ContextEntryState.Superseded);

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        // Nothing was dropped for size here, so every limitation below describes a documented bound.
        Assert.True(snapshot.IsWithinBudget);
        AssertLimitation(snapshot, $"Showing {ProjectSnapshot.MaxTasks} of {ProjectSnapshot.MaxTasks + 7} tasks");
        AssertLimitation(snapshot, $"Showing the {ProjectSnapshot.MaxSessions} most recent sessions");
        AssertLimitation(snapshot, $"Showing {ProjectSnapshot.MaxPendingHandoffs} of");
        AssertLimitation(snapshot, $"Showing the {ProjectSnapshot.MaxContextEntries} most recent");
        AssertLimitation(snapshot, $"first {ProjectSnapshot.MaxContextExcerptCharacters} characters");
        AssertLimitation(snapshot, $"first {ProjectSnapshot.MaxProjectPurposeCharacters:N0} characters");
        AssertLimitation(snapshot, "Superseded context entries are not included");
        AssertLimitation(snapshot, "self-reported");
    }

    /// <summary>
    /// Nothing here is truncated, so nothing may claim to be. A limitation for a bound that did not
    /// bite would teach a reader to ignore the whole block.
    /// </summary>
    [Fact]
    public async Task A_small_project_states_no_truncation_limitation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Small project", "Short purpose.");
        await SeedTaskAsync(dbContext, project, "The only task");

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("Showing", StringComparison.Ordinal));
        Assert.False(snapshot.ProjectPurposeTruncated);
        Assert.True(snapshot.IsWithinBudget);
    }

    [Fact]
    public async Task Truncation_is_deterministic()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Repeatable project");

        for (var index = 0; index < ProjectSnapshot.MaxTasks + 6; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2}");
            await SeedSessionAsync(
                dbContext,
                task,
                AgentSessionRole.Planner,
                AgentSessionStatus.Completed,
                // Deliberately tied timestamps: ordering must still be stable across reads.
                startedUtc: BaseTime);
        }

        for (var index = 0; index < ProjectSnapshot.MaxContextEntries + 6; index++)
        {
            await SeedContextEntryAsync(
                dbContext,
                project,
                task: null,
                $"Entry {index:D2}",
                "Content.",
                createdUtc: BaseTime);
        }

        var first = await RequireSnapshotAsync(dbContext, project.Id);
        var second = await RequireSnapshotAsync(dbContext, project.Id);

        Assert.Equal(
            first.Tasks.Select(task => task.TaskId).ToList(),
            second.Tasks.Select(task => task.TaskId).ToList());
        Assert.Equal(
            first.Sessions.Select(session => session.SessionId).ToList(),
            second.Sessions.Select(session => session.SessionId).ToList());
        Assert.Equal(
            first.ContextEntries.Select(entry => entry.ContextEntryId).ToList(),
            second.ContextEntries.Select(entry => entry.ContextEntryId).ToList());
        Assert.Equal(first.Limitations, second.Limitations);
        Assert.Equal(first.EstimatedCharacters, second.EstimatedCharacters);
    }

    /// <summary>
    /// The documented reduction order: context entries, then sessions, then tasks beyond the floor.
    /// Sized so that dropping the entries alone is enough — sessions must survive, because dropping
    /// more than the budget requires discards project state for nothing.
    /// </summary>
    [Fact]
    public async Task An_over_budget_snapshot_drops_context_entries_before_sessions()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Heavy project");
        var task = await SeedTaskAsync(dbContext, project, "Heavy task");
        await SeedSessionAsync(dbContext, task, AgentSessionRole.Planner, AgentSessionStatus.Completed);

        for (var index = 0; index < ProjectSnapshot.MaxContextEntries; index++)
        {
            await SeedContextEntryAsync(
                dbContext,
                project,
                task: null,
                $"Entry {index:D2}",
                new string('c', ProjectSnapshot.MaxContextExcerptCharacters),
                createdUtc: BaseTime.AddMinutes(index));
        }

        // One task carrying enough projection-authored text to push the snapshot over on its own,
        // but not so much that dropping the entries cannot recover it.
        var projection = ProjectionFor(
            project,
            TaskIn(task.Id, "Heavy task", TaskDisplayState.Waiting, TaskDisplayReasonCode.AwaitingWorkerPickup,
                new string('r', ProjectSnapshot.MaxSnapshotCharacters - 8_000), needsAttention: false));

        var result = await CreateService(dbContext, new StubProjectionService(projection))
            .GetSnapshotAsync(project.Id);

        Assert.Equal(ProjectSnapshotOutcome.Available, result.Outcome);
        var snapshot = result.Snapshot!;

        Assert.True(snapshot.IsWithinBudget);
        Assert.Empty(snapshot.ContextEntries);
        Assert.Single(snapshot.Sessions);
        Assert.Single(snapshot.Tasks);
        Assert.True(snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters);
        AssertLimitation(
            snapshot,
            $"All {ProjectSnapshot.MaxContextEntries} context entries included before reduction were omitted");
        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("sessions included before reduction were omitted", StringComparison.Ordinal));
    }

    /// <summary>
    /// The second reduction step, on its own. Sized so that dropping the context entries is not
    /// enough and dropping the sessions is: the tasks must survive, because cutting a project's work
    /// down to five when ten would have fitted is a smaller answer for no reason.
    ///
    /// The weight is carried by long task titles, which is where a real project's characters
    /// actually sit — a session's only variable-width field is the title of the task it ran against,
    /// so the tasks and the sessions are roughly the same size and dropping one recovers the budget.
    /// </summary>
    [Fact]
    public async Task An_over_budget_snapshot_drops_sessions_before_reducing_tasks()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Wordy project", "Short purpose.");

        const int taskCount = ProjectSnapshot.MaxSessions;
        const int titleLength = 1_500;

        for (var index = 0; index < taskCount; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2} {new string('t', titleLength)}");
            await SeedSessionAsync(
                dbContext,
                task,
                AgentSessionRole.Planner,
                AgentSessionStatus.Completed,
                startedUtc: BaseTime.AddMinutes(index));
        }

        for (var index = 0; index < ProjectSnapshot.MaxContextEntries; index++)
        {
            await SeedContextEntryAsync(
                dbContext,
                project,
                task: null,
                $"Entry {index:D2}",
                new string('c', ProjectSnapshot.MaxContextExcerptCharacters),
                createdUtc: BaseTime.AddMinutes(index));
        }

        var result = await CreateService(dbContext).GetSnapshotAsync(project.Id);

        Assert.Equal(ProjectSnapshotOutcome.Available, result.Outcome);
        var snapshot = result.Snapshot!;

        Assert.True(snapshot.IsWithinBudget);
        Assert.True(snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters);

        // Both steps ahead of the task floor ran, and the floor did not.
        Assert.Empty(snapshot.ContextEntries);
        Assert.Empty(snapshot.Sessions);
        Assert.Equal(taskCount, snapshot.Tasks.Count);

        AssertLimitation(
            snapshot,
            $"All {ProjectSnapshot.MaxContextEntries} context entries included before reduction were omitted");
        AssertLimitation(
            snapshot,
            $"All {ProjectSnapshot.MaxSessions} recent sessions included before reduction were omitted");

        // Reduction stopped the moment the snapshot fitted: the task floor is not mentioned, and
        // nothing claims the project could not be summarised.
        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains(
                $"Only the first {ProjectSnapshot.MinimumTasksWhenOverBudget} tasks",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("is not sent to a reasoning provider", StringComparison.Ordinal));

        // And nothing still claims the dropped categories are visible.
        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("most recent sessions of", StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("most recent active context entries", StringComparison.Ordinal));
    }

    /// <summary>
    /// The third reduction step, reached and survived. Sized so that five tasks fit and six do not,
    /// because the interesting branch is the one where the floor produces a snapshot that can be
    /// sent — testing only the refusal past the floor leaves it unproven that the floor ever helps.
    /// </summary>
    [Fact]
    public async Task Reducing_to_the_task_floor_can_bring_a_snapshot_back_within_budget()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Verbose project", "Short purpose.");

        // Eight tasks at this weight are over budget; the first five are not.
        const int taskCount = ProjectSnapshot.MinimumTasksWhenOverBudget + 3;
        const int reasonLength = 4_000;

        var seeded = new List<DemiplaneTask>();
        for (var index = 0; index < taskCount; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2}");
            await SeedSessionAsync(dbContext, task, AgentSessionRole.Planner, AgentSessionStatus.Completed);
            await SeedContextEntryAsync(dbContext, project, task: null, $"Entry {index:D2}", "Content.");

            seeded.Add(TaskIn(
                task.Id,
                $"Task {index:D2}",
                TaskDisplayState.Waiting,
                TaskDisplayReasonCode.AwaitingWorkerPickup,
                new string('r', reasonLength),
                needsAttention: false));
        }

        var result = await CreateService(dbContext, new StubProjectionService(ProjectionFor(project, [.. seeded])))
            .GetSnapshotAsync(project.Id);

        // The floor produced a snapshot that fits, so it is available rather than refused.
        Assert.Equal(ProjectSnapshotOutcome.Available, result.Outcome);
        var snapshot = result.Snapshot!;

        Assert.True(snapshot.IsWithinBudget);
        Assert.True(snapshot.EstimatedCharacters <= ProjectSnapshot.MaxSnapshotCharacters);
        Assert.Equal(ProjectSnapshot.MinimumTasksWhenOverBudget, snapshot.Tasks.Count);

        // The first five in the Demiplane's order, not an arbitrary five.
        Assert.Equal(
            seeded.Take(ProjectSnapshot.MinimumTasksWhenOverBudget).Select(task => task.TaskId).ToList(),
            snapshot.Tasks.Select(task => task.TaskId).ToList());

        AssertLimitation(
            snapshot,
            $"Only the first {ProjectSnapshot.MinimumTasksWhenOverBudget} tasks were kept");

        // Reduction stopped here: nothing claims the project was unsummarisable.
        Assert.DoesNotContain(
            snapshot.Limitations,
            limitation => limitation.Contains("is not sent to a reasoning provider", StringComparison.Ordinal));

        // And the count the snapshot reports is still the project's, not the five that survived.
        Assert.Equal(taskCount, snapshot.Health.TotalTasks);
    }

    /// <summary>
    /// Reduction decides whether a project is shown to a provider at all, and it decides on the
    /// length of a string. The serializer that produced that length must therefore be the one the
    /// provider boundary will use — two configurations that differ by a comma are two budgets.
    /// </summary>
    [Fact]
    public async Task The_canonical_serializer_produces_the_representation_snapshot_size_is_measured_from()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Measured project");
        var task = await SeedTaskAsync(dbContext, project, "Measured task");
        await SeedSessionAsync(dbContext, task, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        await SeedContextEntryAsync(dbContext, project, task, "Entry", "Content.");

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        // The reported size is exactly the length of the canonical form of the measured snapshot.
        var measured = ProjectSnapshotSerialization.Serialize(
            ProjectSnapshotSerialization.ForMeasurement(snapshot));

        Assert.Equal(measured.Length, snapshot.EstimatedCharacters);
        Assert.Equal(snapshot.EstimatedCharacters, ProjectSnapshotSerialization.Measure(snapshot));

        // The placeholders are what makes it deterministic, and they are the documented reason the
        // estimate is not the byte-for-byte length of the snapshot as finally written.
        Assert.Contains("\"EstimatedCharacters\":0", measured, StringComparison.Ordinal);
        Assert.Contains("\"IsWithinBudget\":true", measured, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.ObservedAt.ToString("O"), measured, StringComparison.Ordinal);
        Assert.True(ProjectSnapshotSerialization.Serialize(snapshot).Length >= snapshot.EstimatedCharacters);

        // Compact, with enums as names: the contract a provider envelope will inherit.
        Assert.False(ProjectSnapshotSerialization.Options.WriteIndented);
        Assert.True(ProjectSnapshotSerialization.Options.IsReadOnly);
        Assert.DoesNotContain('\n', measured);
        Assert.Contains($"\"{ProjectStatus.Active}\"", measured, StringComparison.Ordinal);
        Assert.Contains($"\"{AgentSessionRole.Planner}\"", measured, StringComparison.Ordinal);
    }

    /// <summary>
    /// Past the floor there is nothing honest left to drop, so the snapshot says so instead of
    /// sending a project it has quietly cut in half.
    /// </summary>
    [Fact]
    public async Task A_snapshot_still_over_budget_after_every_reduction_is_refused()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Unsummarisable project");

        var tasks = new List<DemiplaneTask>();
        for (var index = 0; index < ProjectSnapshot.MinimumTasksWhenOverBudget; index++)
        {
            var task = await SeedTaskAsync(dbContext, project, $"Task {index:D2}");
            tasks.Add(TaskIn(
                task.Id,
                $"Task {index:D2}",
                TaskDisplayState.Waiting,
                TaskDisplayReasonCode.AwaitingWorkerPickup,
                new string('r', ProjectSnapshot.MaxSnapshotCharacters),
                needsAttention: false));
        }

        var result = await CreateService(dbContext, new StubProjectionService(ProjectionFor(project, [.. tasks])))
            .GetSnapshotAsync(project.Id);

        Assert.Equal(ProjectSnapshotOutcome.TooLarge, result.Outcome);

        var snapshot = result.Snapshot!;
        Assert.False(snapshot.IsWithinBudget);
        Assert.True(snapshot.EstimatedCharacters > ProjectSnapshot.MaxSnapshotCharacters);
        Assert.Equal(ProjectSnapshot.MinimumTasksWhenOverBudget, snapshot.Tasks.Count);
        AssertLimitation(snapshot, "is not sent to a reasoning provider");
    }

    /// <summary>
    /// Worker keys and display names are administrator-chosen strings that in practice name machines.
    /// Counts and declared roles answer every question the Familiar can honestly ask about workers.
    /// </summary>
    [Fact]
    public async Task Worker_identities_are_never_part_of_a_snapshot()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Staffed project");
        await SeedWorkerAsync(dbContext, "build-box-07.internal", "Studio desktop", AgentSessionRole.Planner);
        await SeedWorkerAsync(dbContext, "laptop-02", "Spare laptop", AgentSessionRole.Planner, AgentSessionRole.Reviewer);
        await SeedDisabledWorkerAsync(dbContext, "retired-01", "Retired box", AgentSessionRole.Implementer);

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        Assert.Equal(2, snapshot.Workers.EnabledWorkerCount);
        Assert.Equal(
            new[] { AgentSessionRole.Planner, AgentSessionRole.Reviewer },
            snapshot.Workers.DeclaredRoles.ToArray());
        Assert.Equal(2, snapshot.Workers.OnlineCount);

        var serialized = Serialize(snapshot);
        Assert.DoesNotContain("build-box-07", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("laptop-02", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Studio desktop", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Spare laptop", serialized, StringComparison.OrdinalIgnoreCase);

        // A disabled worker cannot be granted a claim, so its role must not read as available capacity.
        Assert.DoesNotContain(AgentSessionRole.Implementer, snapshot.Workers.DeclaredRoles);
    }

    [Fact]
    public async Task Provider_readiness_is_carried_from_the_demiplane_unchanged()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Watched project");

        var snapshot = await RequireSnapshotAsync(dbContext, project.Id);

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal("Claude", provider.Provider);
        Assert.Equal(ProviderCapacityStatus.Unknown, provider.Status);
        Assert.Equal(ProviderCapacityConfidence.None, provider.Confidence);
        AssertLimitation(snapshot, "remaining capacity");
    }

    /// <summary>
    /// A contended SQLite file is ordinary here — the runner, the capture path and the claim scan
    /// all write to the same database. A snapshot that cannot be read is a page that says "not right
    /// now", not an unhandled exception, and the reason it gives carries nothing about the file it
    /// failed to read.
    /// </summary>
    [Fact]
    public async Task A_busy_database_is_a_typed_unavailable_outcome_rather_than_a_throw()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seedContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(seedContext, "Contended project");

        // The busy condition is raised where a contended file would raise it — inside the read this
        // service depends on — and classified by the same SessionHandoffApprovalService.IsDatabaseBusy
        // that the write paths use, whose own tests lock a real file.
        var busy = new ThrowingProjectionService(
            new SqliteException("SQLite Error 5: 'database is locked' reading /var/data/find-familiar.db.", 5));

        var result = await CreateService(seedContext, busy).GetSnapshotAsync(project.Id);

        Assert.Equal(ProjectSnapshotOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Detail);

        // The wording is this application's, not the exception's: no file, no error code, no verb
        // from a library the reader has never heard of.
        Assert.DoesNotContain("find-familiar.db", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/data", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unclassified failure is not swallowed: it is a defect, and hiding it behind "unavailable"
    /// would turn every future bug in this service into a shrug on the page.
    /// </summary>
    [Fact]
    public async Task An_unexpected_failure_is_not_disguised_as_unavailable()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Broken project");
        var failing = new ThrowingProjectionService(new InvalidOperationException("Defect."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService(dbContext, failing).GetSnapshotAsync(project.Id));
    }

    [Fact]
    public async Task Building_a_snapshot_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, "Untouched project");
        var task = await SeedTaskAsync(dbContext, project, "Untouched task");
        var session = await SeedSessionAsync(dbContext, task, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        await SeedHandoffAsync(dbContext, task, session, AgentSessionRole.Implementer);
        await SeedContextEntryAsync(dbContext, project, task, "Entry", "Content.");

        var before = await CountRowsAsync(dbContext);
        var revisionBefore = project.ContextRevision;

        await RequireSnapshotAsync(dbContext, project.Id);

        Assert.Equal(before, await CountRowsAsync(dbContext));
        Assert.Empty(dbContext.ChangeTracker.Entries());
        Assert.Equal(
            revisionBefore,
            (await dbContext.Projects.AsNoTracking().SingleAsync(candidate => candidate.Id == project.Id)).ContextRevision);
    }

    private static readonly DateTime BaseTime = new(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);

    // The production contract, not a second one: a leak these tests cannot see because the test
    // serializer wrote the snapshot differently is a leak that ships.
    private static string Serialize(ProjectSnapshot snapshot) =>
        ProjectSnapshotSerialization.Serialize(snapshot);

    private static void AssertLimitation(ProjectSnapshot snapshot, string fragment) =>
        Assert.Contains(
            snapshot.Limitations,
            limitation => limitation.Contains(fragment, StringComparison.Ordinal));

    private static async Task<ProjectSnapshot> RequireSnapshotAsync(
        FamiliarDbContext dbContext,
        Guid projectId,
        IDemiplaneProjectionService? projection = null)
    {
        var result = await CreateService(dbContext, projection).GetSnapshotAsync(projectId);
        Assert.Equal(ProjectSnapshotOutcome.Available, result.Outcome);
        return result.Snapshot!;
    }

    private static ProjectSnapshotService CreateService(
        FamiliarDbContext dbContext,
        IDemiplaneProjectionService? projection = null) =>
        new(
            dbContext,
            projection ?? new DemiplaneProjectionService(dbContext, CapacityService(), TimeProvider.System),
            TimeProvider.System);

    private static ProviderCapacityService CapacityService() =>
        new(
            [new UnknownProviderCapacityReader("Claude", TimeProvider.System, "No usage surface is exposed.")],
            TimeProvider.System,
            NullLogger<ProviderCapacityService>.Instance);

    private sealed class StubProjectionService(DemiplaneProjection projection) : IDemiplaneProjectionService
    {
        public int Calls { get; private set; }

        public Task<DemiplaneProjection?> GetProjectionAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(projection.ProjectId == projectId ? projection : null);
        }
    }

    private sealed class ThrowingProjectionService(Exception exception) : IDemiplaneProjectionService
    {
        public Task<DemiplaneProjection?> GetProjectionAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) => throw exception;
    }

    private static DemiplaneProjection ProjectionFor(FamiliarProject project, params DemiplaneTask[] tasks) =>
        new(
            project.Id,
            project.Name,
            project.Purpose,
            project.Status,
            project.ContextRevision,
            tasks,
            [],
            new DateTimeOffset(BaseTime));

    private static DemiplaneTask TaskIn(
        Guid taskId,
        string title,
        TaskDisplayState state,
        TaskDisplayReasonCode reasonCode,
        string reasonText,
        bool needsAttention,
        string? recommendation = null) =>
        new(
            taskId,
            title,
            TaskStatus.Ready,
            state,
            reasonCode,
            reasonText,
            needsAttention,
            BaseTime,
            [],
            CurrentSessionId: null,
            CurrentRole: null,
            Provider: null,
            PendingHandoffId: null,
            PendingHandoffToken: null,
            ProposedRole: null,
            ProposedKind: null,
            new FamiliarSummary(
                "Nothing has run for this task yet.",
                "Waiting.",
                reasonText,
                needsAttention ? "Decide what happens next." : null,
                recommendation,
                null));

    private static async Task<int> CountRowsAsync(FamiliarDbContext dbContext) =>
        await dbContext.Projects.CountAsync()
        + await dbContext.Tasks.CountAsync()
        + await dbContext.AgentSessions.CountAsync()
        + await dbContext.SessionHandoffs.CountAsync()
        + await dbContext.ContextEntries.CountAsync()
        + await dbContext.Workers.CountAsync()
        + await dbContext.Conversations.CountAsync();

    private static async Task<FamiliarProject> SeedProjectAsync(
        FamiliarDbContext dbContext,
        string name,
        string? purpose = null)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = purpose ?? $"Purpose of {name}.",
            Status = ProjectStatus.Active,
            CreatedUtc = BaseTime,
            UpdatedUtc = BaseTime
        };

        dbContext.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(project).State = EntityState.Detached;
        return project;
    }

    private static async Task<FamiliarTask> SeedTaskAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        string title)
    {
        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = title,
            RequestedOutcome = $"Outcome for {title}.",
            Status = TaskStatus.Ready,
            CreatedUtc = BaseTime,
            UpdatedUtc = BaseTime
        };

        dbContext.Add(task);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(task).State = EntityState.Detached;
        return task;
    }

    private static async Task<AgentSession> SeedSessionAsync(
        FamiliarDbContext dbContext,
        FamiliarTask task,
        AgentSessionRole role,
        AgentSessionStatus status,
        DateTime? startedUtc = null)
    {
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = status,
            Provider = "Claude",
            ContextRevisionRead = 1,
            StartedUtc = startedUtc ?? BaseTime,
            CompletedUtc = status == AgentSessionStatus.Started ? null : (startedUtc ?? BaseTime).AddMinutes(10)
        };

        dbContext.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(session).State = EntityState.Detached;
        return session;
    }

    private static async Task<SessionHandoff> SeedHandoffAsync(
        FamiliarDbContext dbContext,
        FamiliarTask task,
        AgentSession sourceSession,
        AgentSessionRole proposedRole)
    {
        var handoff = new SessionHandoff
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SourceSessionId = sourceSession.Id,
            SourceOutcome = AgentSessionStatus.Completed,
            ProposedRole = proposedRole,
            Kind = SessionHandoffKind.NextRole,
            Status = SessionHandoffStatus.Pending,
            ObservedContextRevision = 1,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = BaseTime,
            UpdatedUtc = BaseTime
        };

        dbContext.Add(handoff);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(handoff).State = EntityState.Detached;
        return handoff;
    }

    private static async Task<ContextEntry> SeedContextEntryAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        FamiliarTask? task,
        string title,
        string content,
        DateTime? createdUtc = null,
        ContextEntryState state = ContextEntryState.Active)
    {
        var entry = new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            TaskId = task?.Id,
            Kind = ContextEntryKind.Decision,
            Title = title,
            Content = content,
            State = state,
            CreatedUtc = createdUtc ?? BaseTime
        };

        dbContext.Add(entry);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(entry).State = EntityState.Detached;
        return entry;
    }

    private static Task<Worker> SeedWorkerAsync(
        FamiliarDbContext dbContext,
        string workerKey,
        string displayName,
        params AgentSessionRole[] roles) =>
        SeedWorkerAsync(dbContext, workerKey, displayName, true, roles);

    private static Task<Worker> SeedDisabledWorkerAsync(
        FamiliarDbContext dbContext,
        string workerKey,
        string displayName,
        params AgentSessionRole[] roles) =>
        SeedWorkerAsync(dbContext, workerKey, displayName, false, roles);

    private static async Task<Worker> SeedWorkerAsync(
        FamiliarDbContext dbContext,
        string workerKey,
        string displayName,
        bool enabled,
        AgentSessionRole[] roles)
    {
        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            WorkerKey = workerKey,
            DisplayName = displayName,
            Enabled = enabled,
            Capabilities = WorkerCapabilities.Format(roles),
            RegisteredUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow
        };

        dbContext.Add(worker);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(worker).State = EntityState.Detached;
        return worker;
    }
}
