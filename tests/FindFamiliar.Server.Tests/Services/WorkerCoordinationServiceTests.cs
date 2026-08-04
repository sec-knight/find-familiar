using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Worker registration, heartbeat, and the atomic claim/lease behavior from ADR-0008. These run
/// against a real file-backed SQLite database, because the claim's correctness depends on the
/// database actually serializing the conditional UPDATE — an in-memory provider would not prove it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class WorkerCoordinationServiceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_heartbeat_registers_the_worker()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        var outcome = await service.HeartbeatAsync(
            new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner"]));

        Assert.Equal(WorkerHeartbeatStatus.Success, outcome.Status);
        Assert.True(outcome.Enabled);

        var worker = await dbContext.Workers.SingleAsync();
        Assert.Equal("workstation-01", worker.WorkerKey);
        Assert.Equal("Workstation 01", worker.DisplayName);
        Assert.Equal("Planner", worker.Capabilities);
        Assert.Equal(Origin.UtcDateTime, worker.RegisteredUtc);
        Assert.Equal(Origin.UtcDateTime, worker.LastHeartbeatUtc);
    }

    [Fact]
    public async Task Repeated_heartbeat_updates_the_same_registration()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        var first = await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner"]));

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await service.HeartbeatAsync(
            new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner", "Reviewer"]));

        Assert.Equal(first.WorkerId, second.WorkerId);

        var worker = await dbContext.Workers.SingleAsync();
        Assert.Equal("Planner,Reviewer", worker.Capabilities);
        Assert.Equal(Origin.UtcDateTime, worker.RegisteredUtc);
        Assert.Equal(Origin.UtcDateTime.AddMinutes(5), worker.LastHeartbeatUtc);
    }

    [Fact]
    public async Task Heartbeat_never_re_enables_a_disabled_worker()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner"]));

        var worker = await dbContext.Workers.SingleAsync();
        worker.Enabled = false;
        await dbContext.SaveChangesAsync();

        var outcome = await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner"]));

        Assert.Equal(WorkerHeartbeatStatus.Success, outcome.Status);
        Assert.False(outcome.Enabled);
        Assert.False((await dbContext.Workers.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task Heartbeat_requires_a_worker_key_and_a_recognized_capability()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var missingKey = await service.HeartbeatAsync(new WorkerHeartbeatRequest("  ", "Nameless", ["Planner"]));
        Assert.Equal(WorkerHeartbeatStatus.ValidationFailed, missingKey.Status);
        Assert.Contains("WorkerKey", missingKey.ValidationErrors!.Keys);

        var unknownRole = await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "W", ["Architect"]));
        Assert.Equal(WorkerHeartbeatStatus.ValidationFailed, unknownRole.Status);
        Assert.Contains("Capabilities", unknownRole.ValidationErrors!.Keys);

        Assert.Empty(await dbContext.Workers.ToListAsync());
    }

    [Theory]
    [InlineData(30, WorkerAvailability.Online)]
    [InlineData(89, WorkerAvailability.Online)]
    [InlineData(300, WorkerAvailability.Stale)]
    [InlineData(1200, WorkerAvailability.Offline)]
    public void Availability_is_derived_from_the_heartbeat_age(int ageSeconds, WorkerAvailability expected)
    {
        var lastHeartbeat = Origin.UtcDateTime;
        var now = lastHeartbeat.AddSeconds(ageSeconds);

        Assert.Equal(expected, WorkerCoordinationService.DeriveAvailability(lastHeartbeat, now));
    }

    [Fact]
    public async Task Worker_that_stops_heartbeating_becomes_stale_then_offline()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var coordination = new WorkerCoordinationService(dbContext, clock);
        var overview = new WorkerOverviewService(dbContext, clock);

        await coordination.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner"]));

        Assert.Equal(WorkerAvailability.Online, (await overview.GetWorkersAsync()).Single().Availability);

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(WorkerAvailability.Stale, (await overview.GetWorkersAsync()).Single().Availability);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(WorkerAvailability.Offline, (await overview.GetWorkersAsync()).Single().Availability);
    }

    [Fact]
    public async Task Eligible_session_is_claimed_with_a_bounded_lease()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        var (project, task, session) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "Workstation 01", ["Planner"]));

        var outcome = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", [project.Id], 600));

        Assert.Equal(WorkerClaimStatus.Granted, outcome.Status);
        var claim = outcome.Claim!;
        Assert.Equal(session.Id, claim.SessionId);
        Assert.Equal(task.Id, claim.TaskId);
        Assert.Equal(project.Id, claim.ProjectId);
        Assert.Equal(AgentSessionRole.Planner, claim.Role);
        Assert.NotEqual(Guid.Empty, claim.ClaimId);
        Assert.Equal(Origin.UtcDateTime, claim.ClaimedUtc);
        Assert.Equal(Origin.UtcDateTime.AddSeconds(600), claim.LeaseExpiresUtc);

        var claimed = await dbContext.AgentSessions.AsNoTracking().SingleAsync(candidate => candidate.Id == session.Id);
        Assert.Equal(claim.WorkerId, claimed.ClaimedByWorkerId);
        Assert.Equal(claim.ClaimId, claimed.ClaimId);
        Assert.Equal(Origin.UtcDateTime.AddSeconds(600), claimed.ClaimExpiresUtc);
        // The claim is an execution lease only: it must not touch the session's own status.
        Assert.Equal(AgentSessionStatus.Started, claimed.Status);
    }

    [Fact]
    public async Task A_live_claim_is_not_granted_to_a_second_worker()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        var (project, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("worker-a", "A", ["Planner"]));
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("worker-b", "B", ["Planner"]));

        var first = await service.ClaimNextAsync(new WorkerClaimRequest("worker-a", [project.Id], 600));
        Assert.Equal(WorkerClaimStatus.Granted, first.Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await service.ClaimNextAsync(new WorkerClaimRequest("worker-b", [project.Id], 600));

        Assert.Equal(WorkerClaimStatus.NoWorkAvailable, second.Status);
    }

    [Fact]
    public async Task Concurrent_claims_for_one_session_grant_exactly_one_owner()
    {
        using var database = new TemporarySqliteDatabase();
        await using var seedContext = await database.CreateContextAsync();

        var (project, _, session) = await SeedSessionAsync(seedContext, AgentSessionRole.Planner, AgentSessionStatus.Started);

        const int workerCount = 8;
        for (var i = 0; i < workerCount; i++)
        {
            var registration = new WorkerCoordinationService(seedContext, new TestTimeProvider(Origin));
            await registration.HeartbeatAsync(new WorkerHeartbeatRequest($"worker-{i}", $"Worker {i}", ["Planner"]));
        }

        // Each racer gets its own DbContext, exactly as concurrent HTTP requests would.
        var contexts = new List<FamiliarDbContext>();
        for (var i = 0; i < workerCount; i++)
        {
            contexts.Add(await database.CreateContextAsync());
        }

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = contexts.Select((context, index) => Task.Run(async () =>
        {
            await barrier.Task;
            var service = new WorkerCoordinationService(context, new TestTimeProvider(Origin));
            return await service.ClaimNextAsync(new WorkerClaimRequest($"worker-{index}", [project.Id], 600));
        })).ToList();

        barrier.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        foreach (var context in contexts)
        {
            await context.DisposeAsync();
        }

        var granted = outcomes.Where(outcome => outcome.Status == WorkerClaimStatus.Granted).ToList();
        Assert.Single(granted);
        Assert.All(
            outcomes.Where(outcome => outcome.Status != WorkerClaimStatus.Granted),
            outcome => Assert.Equal(WorkerClaimStatus.NoWorkAvailable, outcome.Status));

        await using var verifyContext = await database.CreateContextAsync();
        var claimed = await verifyContext.AgentSessions.AsNoTracking().SingleAsync(candidate => candidate.Id == session.Id);
        Assert.Equal(granted.Single().Claim!.WorkerId, claimed.ClaimedByWorkerId);
    }

    [Fact]
    public async Task Expired_lease_lets_another_worker_recover_the_session()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        var (project, _, session) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("worker-a", "A", ["Planner"]));
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("worker-b", "B", ["Planner"]));

        var first = await service.ClaimNextAsync(new WorkerClaimRequest("worker-a", [project.Id], 60));
        Assert.Equal(WorkerClaimStatus.Granted, first.Status);

        // Still inside the lease: no recovery.
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(
            WorkerClaimStatus.NoWorkAvailable,
            (await service.ClaimNextAsync(new WorkerClaimRequest("worker-b", [project.Id], 60))).Status);

        // Lease elapsed: the abandoned session becomes claimable again.
        clock.Advance(TimeSpan.FromSeconds(2));
        var recovered = await service.ClaimNextAsync(new WorkerClaimRequest("worker-b", [project.Id], 60));

        Assert.Equal(WorkerClaimStatus.Granted, recovered.Status);
        Assert.Equal(session.Id, recovered.Claim!.SessionId);
        Assert.NotEqual(first.Claim!.WorkerId, recovered.Claim.WorkerId);
        Assert.NotEqual(first.Claim.ClaimId, recovered.Claim.ClaimId);
    }

    [Fact]
    public async Task Renewal_requires_the_live_owner_generation_and_an_enabled_worker()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var clock = new TestTimeProvider(Origin);
        var service = new WorkerCoordinationService(dbContext, clock);

        var (project, _, session) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("worker-a", "A", ["Planner"]));
        var claim = (await service.ClaimNextAsync(new WorkerClaimRequest("worker-a", [project.Id], 60))).Claim!;

        clock.Advance(TimeSpan.FromSeconds(20));
        var staleGeneration = await service.RenewClaimAsync(
            new WorkerClaimRenewalRequest("worker-a", session.Id, Guid.NewGuid(), 60));
        Assert.Equal(WorkerClaimRenewalStatus.ClaimLost, staleGeneration.Status);

        var renewed = await service.RenewClaimAsync(
            new WorkerClaimRenewalRequest("worker-a", session.Id, claim.ClaimId, 60));
        Assert.Equal(WorkerClaimRenewalStatus.Renewed, renewed.Status);
        Assert.Equal(Origin.UtcDateTime.AddSeconds(80), renewed.LeaseExpiresUtc);

        var worker = await dbContext.Workers.SingleAsync(candidate => candidate.Id == claim.WorkerId);
        worker.Enabled = false;
        await dbContext.SaveChangesAsync();

        var disabled = await service.RenewClaimAsync(
            new WorkerClaimRenewalRequest("worker-a", session.Id, claim.ClaimId, 60));
        Assert.Equal(WorkerClaimRenewalStatus.WorkerDisabled, disabled.Status);

        worker.Enabled = true;
        await dbContext.SaveChangesAsync();
        clock.Advance(TimeSpan.FromSeconds(61));

        var expired = await service.RenewClaimAsync(
            new WorkerClaimRenewalRequest("worker-a", session.Id, claim.ClaimId, 60));
        Assert.Equal(WorkerClaimRenewalStatus.ClaimLost, expired.Status);
    }

    [Fact]
    public async Task Terminal_sessions_are_never_claimed()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var (completedProject, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Completed);
        var (cancelledProject, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Cancelled);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "W", ["Planner"]));

        var outcome = await service.ClaimNextAsync(
            new WorkerClaimRequest("workstation-01", [completedProject.Id, cancelledProject.Id], 600));

        Assert.Equal(WorkerClaimStatus.NoWorkAvailable, outcome.Status);
    }

    [Fact]
    public async Task Session_whose_role_the_worker_does_not_support_is_not_offered()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var (project, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Implementer, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("planner-only", "Planner only", ["Planner"]));

        var outcome = await service.ClaimNextAsync(new WorkerClaimRequest("planner-only", [project.Id], 600));

        Assert.Equal(WorkerClaimStatus.NoWorkAvailable, outcome.Status);
    }

    [Fact]
    public async Task Session_in_an_unmapped_project_is_not_offered()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var (_, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "W", ["Planner"]));

        // The worker reports only a project it has a local repository mapping for; the seeded
        // session belongs to a different project, so it must not be handed out.
        var unmapped = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", [Guid.NewGuid()], 600));
        Assert.Equal(WorkerClaimStatus.NoWorkAvailable, unmapped.Status);

        var none = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", [], 600));
        Assert.Equal(WorkerClaimStatus.NoWorkAvailable, none.Status);
    }

    [Fact]
    public async Task Unknown_and_disabled_workers_are_refused()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var (project, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);

        var unknown = await service.ClaimNextAsync(new WorkerClaimRequest("never-registered", [project.Id], 600));
        Assert.Equal(WorkerClaimStatus.UnknownWorker, unknown.Status);

        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "W", ["Planner"]));
        var worker = await dbContext.Workers.SingleAsync();
        worker.Enabled = false;
        await dbContext.SaveChangesAsync();

        var disabled = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", [project.Id], 600));
        Assert.Equal(WorkerClaimStatus.WorkerDisabled, disabled.Status);

        var untouched = await dbContext.AgentSessions.AsNoTracking().SingleAsync();
        Assert.Null(untouched.ClaimedByWorkerId);
    }

    [Fact]
    public async Task Lease_outside_the_allowed_range_is_rejected()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var (project, _, _) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "W", ["Planner"]));

        var tooShort = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", [project.Id], 1));
        Assert.Equal(WorkerClaimStatus.ValidationFailed, tooShort.Status);

        var tooLong = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", [project.Id], 100_000));
        Assert.Equal(WorkerClaimStatus.ValidationFailed, tooLong.Status);

        Assert.Null((await dbContext.AgentSessions.AsNoTracking().SingleAsync()).ClaimedByWorkerId);
    }

    [Fact]
    public async Task Oversized_project_list_is_rejected_before_querying()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("workstation-01", "W", ["Planner"]));

        var tooMany = Enumerable
            .Range(0, WorkerCoordinationService.MaxClaimProjectIds + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();

        var outcome = await service.ClaimNextAsync(new WorkerClaimRequest("workstation-01", tooMany, 600));

        Assert.Equal(WorkerClaimStatus.ValidationFailed, outcome.Status);
        Assert.Contains("ProjectIds", outcome.ValidationErrors!.Keys);
    }

    [Fact]
    public async Task Releasing_a_claim_requires_current_ownership()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var service = new WorkerCoordinationService(dbContext, new TestTimeProvider(Origin));

        var (project, _, session) = await SeedSessionAsync(dbContext, AgentSessionRole.Planner, AgentSessionStatus.Started);
        await service.HeartbeatAsync(new WorkerHeartbeatRequest("worker-a", "A", ["Planner"]));

        var claim = (await service.ClaimNextAsync(new WorkerClaimRequest("worker-a", [project.Id], 600))).Claim!;

        // A different worker cannot release someone else's lease.
        await service.ReleaseClaimAsync(session.Id, Guid.NewGuid(), claim.ClaimId);
        Assert.Equal(
            claim.WorkerId,
            (await dbContext.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).ClaimedByWorkerId);

        // The right worker with a stale generation still cannot release the active claim.
        await service.ReleaseClaimAsync(session.Id, claim.WorkerId, Guid.NewGuid());
        Assert.Equal(
            claim.WorkerId,
            (await dbContext.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).ClaimedByWorkerId);

        await service.ReleaseClaimAsync(session.Id, claim.WorkerId, claim.ClaimId);
        var released = await dbContext.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Null(released.ClaimedByWorkerId);
        Assert.Null(released.ClaimExpiresUtc);
        Assert.Null(released.ClaimId);
        Assert.Equal(AgentSessionStatus.Started, released.Status);
    }

    private static async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedSessionAsync(
        FamiliarDbContext dbContext,
        AgentSessionRole role,
        AgentSessionStatus status)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Worker coordination project {Guid.NewGuid():N}",
            Purpose = "Seeded for WorkerCoordinationServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = Origin.UtcDateTime,
            UpdatedUtc = Origin.UtcDateTime
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Worker coordination task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for WorkerCoordinationServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = Origin.UtcDateTime,
            UpdatedUtc = Origin.UtcDateTime
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = status,
            ContextRevisionRead = 1,
            StartedUtc = Origin.UtcDateTime,
            CompletedUtc = status == AgentSessionStatus.Started ? null : Origin.UtcDateTime
        };

        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }
}
