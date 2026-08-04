using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum WorkerHeartbeatStatus
{
    Success,
    ValidationFailed
}

public enum WorkerClaimStatus
{
    /// <summary>A session was claimed; <see cref="WorkerClaimOutcome.Claim"/> is populated.</summary>
    Granted,

    /// <summary>The worker is known and enabled, but nothing eligible was available.</summary>
    NoWorkAvailable,

    UnknownWorker,
    WorkerDisabled,
    ValidationFailed
}

public enum WorkerClaimRenewalStatus
{
    Renewed,
    UnknownWorker,
    WorkerDisabled,
    ClaimLost,
    ValidationFailed
}

public sealed record WorkerHeartbeatRequest(
    string? WorkerKey,
    string? DisplayName,
    IReadOnlyList<string>? Capabilities);

public sealed record WorkerHeartbeatOutcome(
    WorkerHeartbeatStatus Status,
    Guid WorkerId = default,
    bool Enabled = false,
    WorkerAvailability Availability = WorkerAvailability.Offline,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static WorkerHeartbeatOutcome Success(Guid workerId, bool enabled) =>
        new(WorkerHeartbeatStatus.Success, workerId, enabled, WorkerAvailability.Online);

    public static WorkerHeartbeatOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(WorkerHeartbeatStatus.ValidationFailed, ValidationErrors: errors);
}

/// <summary>
/// Identifies the work a worker was granted. <see cref="ProjectIds"/> is supplied by the worker
/// per request and never persisted — it is the worker telling the server which projects it has a
/// local repository mapping for.
/// </summary>
public sealed record WorkerClaimRequest(
    string? WorkerKey,
    IReadOnlyList<Guid>? ProjectIds,
    int? LeaseSeconds);

public sealed record WorkerClaim(
    Guid WorkerId,
    Guid ClaimId,
    Guid ProjectId,
    Guid TaskId,
    Guid SessionId,
    AgentSessionRole Role,
    int ContextRevisionRead,
    DateTime ClaimedUtc,
    DateTime LeaseExpiresUtc);

public sealed record WorkerClaimRenewalRequest(
    string? WorkerKey,
    Guid SessionId,
    Guid ClaimId,
    int? LeaseSeconds);

public sealed record WorkerClaimRenewalOutcome(
    WorkerClaimRenewalStatus Status,
    DateTime? LeaseExpiresUtc = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly WorkerClaimRenewalOutcome UnknownWorker = new(WorkerClaimRenewalStatus.UnknownWorker);
    public static readonly WorkerClaimRenewalOutcome WorkerDisabled = new(WorkerClaimRenewalStatus.WorkerDisabled);
    public static readonly WorkerClaimRenewalOutcome ClaimLost = new(WorkerClaimRenewalStatus.ClaimLost);

    public static WorkerClaimRenewalOutcome Renewed(DateTime leaseExpiresUtc) =>
        new(WorkerClaimRenewalStatus.Renewed, leaseExpiresUtc);

    public static WorkerClaimRenewalOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(WorkerClaimRenewalStatus.ValidationFailed, ValidationErrors: errors);
}

public sealed record WorkerClaimOutcome(
    WorkerClaimStatus Status,
    WorkerClaim? Claim = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null)
{
    public static readonly WorkerClaimOutcome NoWorkAvailable = new(WorkerClaimStatus.NoWorkAvailable);
    public static readonly WorkerClaimOutcome UnknownWorker = new(WorkerClaimStatus.UnknownWorker);
    public static readonly WorkerClaimOutcome WorkerDisabled = new(WorkerClaimStatus.WorkerDisabled);

    public static WorkerClaimOutcome Granted(WorkerClaim claim) => new(WorkerClaimStatus.Granted, claim);

    public static WorkerClaimOutcome ValidationFailed(IReadOnlyDictionary<string, string> errors) =>
        new(WorkerClaimStatus.ValidationFailed, ValidationErrors: errors);
}

public interface IWorkerCoordinationService
{
    Task<WorkerHeartbeatOutcome> HeartbeatAsync(WorkerHeartbeatRequest request, CancellationToken cancellationToken = default);

    Task<WorkerClaimOutcome> ClaimNextAsync(WorkerClaimRequest request, CancellationToken cancellationToken = default);

    Task<WorkerClaimRenewalOutcome> RenewClaimAsync(
        WorkerClaimRenewalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a claim the given worker still holds. Used to undo a claim the server cannot serve.</summary>
    Task ReleaseClaimAsync(Guid sessionId, Guid workerId, Guid claimId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Worker registration, heartbeat and atomic work claiming (ADR-0008).
///
/// The server is the only thing that decides which session a worker owns. A worker never selects
/// its own work: it reports who it is and which projects it can service, and receives at most one
/// claim per request.
/// </summary>
public sealed class WorkerCoordinationService(FamiliarDbContext dbContext, TimeProvider timeProvider)
    : IWorkerCoordinationService
{
    public const int MaxWorkerKeyLength = 100;
    public const int MaxDisplayNameLength = 160;

    public const int MinLeaseSeconds = 30;
    public const int MaxLeaseSeconds = 3600;
    public const int DefaultLeaseSeconds = 1800;

    /// <summary>A worker heard from within this window is Online.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    /// <summary>Beyond <see cref="OnlineWindow"/> but within this window a worker is Stale; past it, Offline.</summary>
    public static readonly TimeSpan StaleWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Upper bound on candidate sessions examined in one claim request. A claim attempt that loses
    /// a race moves on to the next candidate rather than failing the whole request, but the walk is
    /// bounded so a contended queue can never turn one request into an unbounded write loop.
    /// </summary>
    private const int MaxClaimCandidates = 20;

    /// <summary>Upper bound on the project mappings one worker may report in a single claim request.</summary>
    public const int MaxClaimProjectIds = 200;

    public static WorkerAvailability DeriveAvailability(DateTime lastHeartbeatUtc, DateTime nowUtc)
    {
        var age = nowUtc - lastHeartbeatUtc;

        if (age <= OnlineWindow)
        {
            return WorkerAvailability.Online;
        }

        return age <= StaleWindow ? WorkerAvailability.Stale : WorkerAvailability.Offline;
    }

    public async Task<WorkerHeartbeatOutcome> HeartbeatAsync(
        WorkerHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        var workerKey = request.WorkerKey?.Trim();
        if (string.IsNullOrWhiteSpace(workerKey))
        {
            errors[nameof(request.WorkerKey)] = "WorkerKey is required.";
        }
        else if (workerKey.Length > MaxWorkerKeyLength)
        {
            errors[nameof(request.WorkerKey)] = $"WorkerKey must be {MaxWorkerKeyLength} characters or fewer.";
        }

        var displayName = request.DisplayName?.Trim();
        if (!string.IsNullOrEmpty(displayName) && displayName.Length > MaxDisplayNameLength)
        {
            errors[nameof(request.DisplayName)] = $"DisplayName must be {MaxDisplayNameLength} characters or fewer.";
        }

        var capabilities = ParseCapabilities(request.Capabilities);
        if (capabilities.Count == 0)
        {
            errors[nameof(request.Capabilities)] = "At least one recognized role capability is required.";
        }

        if (errors.Count > 0)
        {
            return WorkerHeartbeatOutcome.ValidationFailed(errors);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var worker = await dbContext.Workers.SingleOrDefaultAsync(
            candidate => candidate.WorkerKey == workerKey,
            cancellationToken);

        if (worker is null)
        {
            // First heartbeat registers the worker. Enabled by default: an administrator who wants
            // a worker parked can disable it, and that decision then survives every later heartbeat.
            worker = new Worker
            {
                Id = Guid.NewGuid(),
                WorkerKey = workerKey!,
                DisplayName = string.IsNullOrEmpty(displayName) ? workerKey! : displayName,
                Enabled = true,
                Capabilities = WorkerCapabilities.Format(capabilities),
                RegisteredUtc = nowUtc,
                LastHeartbeatUtc = nowUtc
            };

            dbContext.Workers.Add(worker);
        }
        else
        {
            worker.DisplayName = string.IsNullOrEmpty(displayName) ? worker.DisplayName : displayName;
            worker.Capabilities = WorkerCapabilities.Format(capabilities);
            worker.LastHeartbeatUtc = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkerHeartbeatOutcome.Success(worker.Id, worker.Enabled);
    }

    public async Task<WorkerClaimOutcome> ClaimNextAsync(
        WorkerClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        var workerKey = request.WorkerKey?.Trim();
        if (string.IsNullOrWhiteSpace(workerKey))
        {
            errors[nameof(request.WorkerKey)] = "WorkerKey is required.";
        }

        if (request.LeaseSeconds is { } requested && (requested < MinLeaseSeconds || requested > MaxLeaseSeconds))
        {
            errors[nameof(request.LeaseSeconds)] =
                $"LeaseSeconds must be between {MinLeaseSeconds} and {MaxLeaseSeconds}.";
        }

        // The project list becomes an IN clause, so it is bounded rather than trusted: an
        // authenticated but misconfigured worker should not be able to turn one poll into an
        // enormous parameterized query.
        if (request.ProjectIds is { Count: > MaxClaimProjectIds })
        {
            errors[nameof(request.ProjectIds)] = $"ProjectIds must contain {MaxClaimProjectIds} entries or fewer.";
        }

        if (errors.Count > 0)
        {
            return WorkerClaimOutcome.ValidationFailed(errors);
        }

        var worker = await dbContext.Workers.SingleOrDefaultAsync(
            candidate => candidate.WorkerKey == workerKey,
            cancellationToken);

        if (worker is null)
        {
            return WorkerClaimOutcome.UnknownWorker;
        }

        if (!worker.Enabled)
        {
            return WorkerClaimOutcome.WorkerDisabled;
        }

        // A project with no local repository mapping on this worker is simply not offered. This is
        // why repository paths never need to reach the server: the worker filters by project ID.
        var projectIds = request.ProjectIds?.Distinct().ToList() ?? [];
        var capabilities = WorkerCapabilities.Parse(worker.Capabilities).ToList();

        if (projectIds.Count == 0 || capabilities.Count == 0)
        {
            return WorkerClaimOutcome.NoWorkAvailable;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var leaseSeconds = request.LeaseSeconds ?? DefaultLeaseSeconds;
        var leaseExpiresUtc = nowUtc.AddSeconds(leaseSeconds);

        var candidates = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session =>
                session.Status == AgentSessionStatus.Started
                && projectIds.Contains(session.Task.ProjectId)
                && capabilities.Contains(session.Role)
                && (session.ClaimedByWorkerId == null
                    || session.ClaimExpiresUtc == null
                    || session.ClaimExpiresUtc <= nowUtc))
            .OrderBy(session => session.StartedUtc)
            .ThenBy(session => session.Id)
            .Select(session => new
            {
                session.Id,
                session.TaskId,
                ProjectId = session.Task.ProjectId,
                session.Role,
                session.ContextRevisionRead
            })
            .Take(MaxClaimCandidates)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var claimId = Guid.NewGuid();

            // The claim itself is one conditional UPDATE. The WHERE clause re-checks every
            // eligibility condition that another worker could have invalidated since the read
            // above, so two workers racing for the same session produce exactly one affected row.
            var affected = await dbContext.AgentSessions
                .Where(session =>
                    session.Id == candidate.Id
                    && session.Status == AgentSessionStatus.Started
                    && dbContext.Workers.Any(current =>
                        current.Id == worker.Id
                        && current.Enabled
                        && current.Capabilities == worker.Capabilities)
                    && (session.ClaimedByWorkerId == null
                        || session.ClaimExpiresUtc == null
                        || session.ClaimExpiresUtc <= nowUtc))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(session => session.ClaimedByWorkerId, worker.Id)
                        .SetProperty(session => session.ClaimedUtc, nowUtc)
                        .SetProperty(session => session.ClaimExpiresUtc, leaseExpiresUtc)
                        .SetProperty(session => session.ClaimId, claimId),
                    cancellationToken);

            if (affected != 1)
            {
                continue;
            }

            worker.LastClaimUtc = nowUtc;
            await dbContext.SaveChangesAsync(cancellationToken);

            return WorkerClaimOutcome.Granted(new WorkerClaim(
                worker.Id,
                claimId,
                candidate.ProjectId,
                candidate.TaskId,
                candidate.Id,
                candidate.Role,
                candidate.ContextRevisionRead,
                nowUtc,
                leaseExpiresUtc));
        }

        return WorkerClaimOutcome.NoWorkAvailable;
    }

    public async Task<WorkerClaimRenewalOutcome> RenewClaimAsync(
        WorkerClaimRenewalRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var workerKey = request.WorkerKey?.Trim();

        if (string.IsNullOrWhiteSpace(workerKey))
        {
            errors[nameof(request.WorkerKey)] = "WorkerKey is required.";
        }

        if (request.SessionId == Guid.Empty)
        {
            errors[nameof(request.SessionId)] = "SessionId is required.";
        }

        if (request.ClaimId == Guid.Empty)
        {
            errors[nameof(request.ClaimId)] = "ClaimId is required.";
        }

        if (request.LeaseSeconds is { } requested && (requested < MinLeaseSeconds || requested > MaxLeaseSeconds))
        {
            errors[nameof(request.LeaseSeconds)] =
                $"LeaseSeconds must be between {MinLeaseSeconds} and {MaxLeaseSeconds}.";
        }

        if (errors.Count > 0)
        {
            return WorkerClaimRenewalOutcome.ValidationFailed(errors);
        }

        var worker = await dbContext.Workers.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.WorkerKey == workerKey,
            cancellationToken);

        if (worker is null)
        {
            return WorkerClaimRenewalOutcome.UnknownWorker;
        }

        if (!worker.Enabled)
        {
            return WorkerClaimRenewalOutcome.WorkerDisabled;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var leaseExpiresUtc = nowUtc.AddSeconds(request.LeaseSeconds ?? DefaultLeaseSeconds);

        var affected = await dbContext.AgentSessions
            .Where(session =>
                session.Id == request.SessionId
                && session.Status == AgentSessionStatus.Started
                && session.ClaimedByWorkerId == worker.Id
                && session.ClaimId == request.ClaimId
                && session.ClaimExpiresUtc > nowUtc
                && dbContext.Workers.Any(current => current.Id == worker.Id && current.Enabled))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.ClaimExpiresUtc, leaseExpiresUtc),
                cancellationToken);

        return affected == 1
            ? WorkerClaimRenewalOutcome.Renewed(leaseExpiresUtc)
            : WorkerClaimRenewalOutcome.ClaimLost;
    }

    public async Task ReleaseClaimAsync(
        Guid sessionId,
        Guid workerId,
        Guid claimId,
        CancellationToken cancellationToken = default)
    {
        // Guarded by owner and generation so a stale release can never clear a newer lease.
        await dbContext.AgentSessions
            .Where(session =>
                session.Id == sessionId
                && session.ClaimedByWorkerId == workerId
                && session.ClaimId == claimId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.ClaimedByWorkerId, (Guid?)null)
                    .SetProperty(session => session.ClaimedUtc, (DateTime?)null)
                    .SetProperty(session => session.ClaimExpiresUtc, (DateTime?)null)
                    .SetProperty(session => session.ClaimId, (Guid?)null),
                cancellationToken);
    }

    private static List<AgentSessionRole> ParseCapabilities(IReadOnlyList<string>? capabilities)
    {
        if (capabilities is null)
        {
            return [];
        }

        var roles = new List<AgentSessionRole>();

        foreach (var candidate in capabilities)
        {
            if (Enum.TryParse<AgentSessionRole>(candidate?.Trim(), ignoreCase: true, out var role) && !roles.Contains(role))
            {
                roles.Add(role);
            }
        }

        return roles;
    }
}
