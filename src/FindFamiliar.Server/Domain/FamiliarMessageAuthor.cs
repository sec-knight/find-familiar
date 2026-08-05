namespace FindFamiliar.Server.Domain;

/// <summary>Who a <see cref="FamiliarMessage"/> is attributed to on the page.</summary>
public enum FamiliarMessageAuthor
{
    /// <summary>The person using the page.</summary>
    Human,

    /// <summary>A reply produced by a reasoning provider.</summary>
    Familiar,

    /// <summary>
    /// A note composed by this application — never speech. The Familiar does not report failures of
    /// components it cannot observe, so the page says it instead.
    /// </summary>
    System
}
