namespace FindFamiliar.Server.Domain;

/// <summary>
/// Availability derived from <see cref="Worker.LastHeartbeatUtc"/> at read time. This is never
/// persisted and never authoritative for workflow state — a session's own
/// <see cref="AgentSessionStatus"/> and claim remain the only authority (ADR-0008).
/// </summary>
public enum WorkerAvailability
{
    Online,
    Stale,
    Offline
}
