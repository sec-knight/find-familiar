namespace FindFamiliar.Server.Domain;

public sealed class FamiliarTask
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public FamiliarProject Project { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    public string RequestedOutcome { get; set; } = string.Empty;

    public TaskStatus Status { get; set; } = TaskStatus.Draft;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ICollection<AgentSession> AgentSessions { get; } = new List<AgentSession>();

    public ICollection<ContextEntry> ContextEntries { get; } = new List<ContextEntry>();
}
