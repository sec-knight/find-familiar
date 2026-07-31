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

    public ICollection<ContextEntry> ContextEntries { get; } = new List<ContextEntry>();
}
