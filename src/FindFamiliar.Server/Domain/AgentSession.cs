namespace FindFamiliar.Server.Domain;

public sealed class AgentSession
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public FamiliarTask Task { get; set; } = null!;

    public AgentSessionRole Role { get; set; }

    public string? Provider { get; set; }

    public string? ExternalSessionReference { get; set; }

    public AgentSessionStatus Status { get; set; } = AgentSessionStatus.Started;

    public int ContextRevisionRead { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>Structured adapter/provider failure metadata. Null for successful or human-cancelled sessions.</summary>
    public string? FailureCategory { get; set; }

    public int? FailureAdapterExitCode { get; set; }

    public bool? FailureProviderLaunched { get; set; }

    public int? FailureProviderExitCode { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>
    /// Worker currently holding this session's execution claim, or null when unclaimed (ADR-0008).
    /// A claim is an execution lease only — it never substitutes for <see cref="Status"/>, which
    /// remains the authority for whether the session may still be captured or cancelled.
    /// </summary>
    public Guid? ClaimedByWorkerId { get; set; }

    public Worker? ClaimedByWorker { get; set; }

    public DateTime? ClaimedUtc { get; set; }

    /// <summary>
    /// Lease expiry. Once this passes, the session becomes claimable again so work is recovered
    /// after a worker crashes without ever cancelling or completing it.
    /// </summary>
    public DateTime? ClaimExpiresUtc { get; set; }

    /// <summary>
    /// Unique fencing token for the current claim generation. Ownership-sensitive operations
    /// must present this value so a worker whose lease expired cannot affect a later claimant.
    /// </summary>
    public Guid? ClaimId { get; set; }

    public ICollection<ContextEntry> ContextEntries { get; } = new List<ContextEntry>();
}
