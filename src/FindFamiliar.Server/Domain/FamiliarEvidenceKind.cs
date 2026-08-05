namespace FindFamiliar.Server.Domain;

/// <summary>
/// Which table a <see cref="FamiliarEvidence.ReferenceId"/> names. Exactly the four kinds a project
/// snapshot carries, because an id that was never in the snapshot is never accepted as evidence.
/// </summary>
public enum FamiliarEvidenceKind
{
    Task,
    Session,
    Handoff,
    ContextEntry
}
