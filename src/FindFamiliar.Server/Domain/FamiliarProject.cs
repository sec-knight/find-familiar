namespace FindFamiliar.Server.Domain;

public sealed class FamiliarProject
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public int ContextRevision { get; private set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ICollection<FamiliarTask> Tasks { get; } = new List<FamiliarTask>();

    public ICollection<ContextEntry> ContextEntries { get; } = new List<ContextEntry>();

    public void IncrementContextRevision()
    {
        ContextRevision++;
        UpdatedUtc = DateTime.UtcNow;
    }
}
