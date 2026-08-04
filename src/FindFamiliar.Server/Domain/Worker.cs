namespace FindFamiliar.Server.Domain;

/// <summary>
/// A registered execution worker (ADR-0008). Identity is the administrator-chosen
/// <see cref="WorkerKey"/>, which is stable across worker restarts, so a worker that stops and
/// starts again is the same durable row rather than a new one.
///
/// This row deliberately holds no machine-specific configuration: no repository path, no drive
/// letter, no adapter path, no credential. Those stay local to the worker host and are never sent
/// to the server.
/// </summary>
public sealed class Worker
{
    public Guid Id { get; set; }

    /// <summary>Stable administrator-chosen identity, unique across workers.</summary>
    public string WorkerKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Administrator switch. A disabled worker keeps its registration and heartbeat history but is
    /// never granted a claim.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Canonical, comma-separated <see cref="AgentSessionRole"/> names this worker can execute,
    /// reported by the worker on every heartbeat. Read through <see cref="WorkerCapabilities"/>.
    /// </summary>
    public string Capabilities { get; set; } = string.Empty;

    public DateTime RegisteredUtc { get; set; }

    public DateTime LastHeartbeatUtc { get; set; }

    /// <summary>Last time this worker was granted a claim. Operator visibility only.</summary>
    public DateTime? LastClaimUtc { get; set; }

    public ICollection<AgentSession> ClaimedSessions { get; } = new List<AgentSession>();
}
