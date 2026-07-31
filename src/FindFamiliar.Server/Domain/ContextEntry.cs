namespace FindFamiliar.Server.Domain;

public sealed class ContextEntry
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public FamiliarProject Project { get; set; } = null!;

    public Guid? TaskId { get; set; }

    public FamiliarTask? Task { get; set; }

    public Guid? SourceSessionId { get; set; }

    public AgentSession? SourceSession { get; set; }

    public ContextEntryKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public ContextEntryState State { get; set; } = ContextEntryState.Active;

    public Guid? SupersedesContextEntryId { get; set; }

    public ContextEntry? SupersedesContextEntry { get; set; }

    public DateTime CreatedUtc { get; set; }
}
