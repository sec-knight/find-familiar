namespace FindFamiliar.Server.Domain;

/// <summary>
/// How much weight a recorded fact carries — the difference between something checked and something
/// merely stated.
///
/// The Familiar's whole value is that a later reader can trust what it holds. A record that says "the
/// gateway shipped" is worth something different depending on whether a repository was inspected or a
/// person said so in conversation, and a reader who cannot tell the two apart will treat both as
/// settled. Before this existed, authors wrote the distinction into prose when they remembered to.
///
/// A closed enum rather than free text, for the same reason <see cref="FamiliarActionKind"/> is one:
/// a column a caller can put anything into is a column nobody can filter on.
/// </summary>
public enum ContextProvenance
{
    /// <summary>
    /// Not stated. Every row written before provenance existed, and nothing written after — the
    /// recording service requires an explicit value, so this never appears on a new entry.
    ///
    /// It is deliberately not called "Unknown": these records are not suspect, they simply predate the
    /// question being asked.
    /// </summary>
    Unspecified,

    /// <summary>
    /// Checked against the repository, the database, or a test run at the time of writing. The
    /// strongest class this system issues, and still a claim about a moment: a verified fact can be
    /// made false by the next commit.
    /// </summary>
    RepositoryVerified,

    /// <summary>Produced by an agent session and captured through the session result path.</summary>
    SessionReported,

    /// <summary>
    /// Stated by the human. Not verifiable from here, and often the most important thing recorded —
    /// external validation, intent, and decisions taken outside this machine arrive this way.
    /// </summary>
    HumanReported,

    /// <summary>
    /// Reported by an external tool or client without independent verification. Kept distinct from
    /// <see cref="HumanReported"/> because a person vouching for something and a program asserting it
    /// are different kinds of evidence.
    /// </summary>
    ExternalReported
}
