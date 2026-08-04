using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services;

/// <summary>
/// One registered worker as an operator sees it. Availability and lease state are derived at read
/// time from persisted timestamps — this projection performs no writes and holds no authority over
/// session state.
/// </summary>
public sealed record WorkerOverviewItem(
    Guid WorkerId,
    string WorkerKey,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<AgentSessionRole> Capabilities,
    WorkerAvailability Availability,
    DateTime RegisteredUtc,
    DateTime LastHeartbeatUtc,
    DateTime? LastClaimUtc,
    WorkerActiveClaim? ActiveClaim);

/// <summary>A live (Started, unexpired) claim this worker currently holds.</summary>
public sealed record WorkerActiveClaim(
    Guid TaskId,
    string TaskTitle,
    Guid SessionId,
    AgentSessionRole Role,
    DateTime ClaimedUtc,
    DateTime LeaseExpiresUtc,
    bool LeaseExpired);
