using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public interface IWorkerOverviewService
{
    Task<IReadOnlyList<WorkerOverviewItem>> GetWorkersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only operator projection over registered workers, following the same shape as
/// <see cref="WorkQueueService"/>: a direct EF Core query, no repository layer, no writes.
/// </summary>
public sealed class WorkerOverviewService(FamiliarDbContext dbContext, TimeProvider timeProvider) : IWorkerOverviewService
{
    public async Task<IReadOnlyList<WorkerOverviewItem>> GetWorkersAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var workers = await dbContext.Workers
            .AsNoTracking()
            .OrderBy(worker => worker.WorkerKey)
            .ToListAsync(cancellationToken);

        if (workers.Count == 0)
        {
            return [];
        }

        var workerIds = workers.Select(worker => worker.Id).ToList();

        // Only a Started session can still be executing, so a claim on a terminal session is
        // history rather than an active claim and is deliberately not shown as one.
        var claims = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session =>
                session.ClaimedByWorkerId != null
                && workerIds.Contains(session.ClaimedByWorkerId.Value)
                && session.Status == AgentSessionStatus.Started)
            .Select(session => new
            {
                WorkerId = session.ClaimedByWorkerId!.Value,
                session.TaskId,
                TaskTitle = session.Task.Title,
                SessionId = session.Id,
                session.Role,
                session.ClaimedUtc,
                session.ClaimExpiresUtc
            })
            .ToListAsync(cancellationToken);

        var claimsByWorker = claims
            .GroupBy(claim => claim.WorkerId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(claim => claim.ClaimedUtc).First());

        return workers
            .Select(worker =>
            {
                WorkerActiveClaim? activeClaim = null;

                if (claimsByWorker.TryGetValue(worker.Id, out var claim))
                {
                    activeClaim = new WorkerActiveClaim(
                        claim.TaskId,
                        claim.TaskTitle,
                        claim.SessionId,
                        claim.Role,
                        claim.ClaimedUtc ?? worker.LastClaimUtc ?? nowUtc,
                        claim.ClaimExpiresUtc ?? nowUtc,
                        claim.ClaimExpiresUtc is null || claim.ClaimExpiresUtc <= nowUtc);
                }

                return new WorkerOverviewItem(
                    worker.Id,
                    worker.WorkerKey,
                    worker.DisplayName,
                    worker.Enabled,
                    WorkerCapabilities.Parse(worker.Capabilities),
                    WorkerCoordinationService.DeriveAvailability(worker.LastHeartbeatUtc, nowUtc),
                    worker.RegisteredUtc,
                    worker.LastHeartbeatUtc,
                    worker.LastClaimUtc,
                    activeClaim);
            })
            .ToList();
    }
}
