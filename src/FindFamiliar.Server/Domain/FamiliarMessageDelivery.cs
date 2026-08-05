namespace FindFamiliar.Server.Domain;

/// <summary>How completely a message arrived, which is what the page styles on.</summary>
public enum FamiliarMessageDelivery
{
    /// <summary>Ordinary. Human and System messages are always this.</summary>
    Delivered,

    /// <summary>
    /// A reply arrived but part of the outcome was discarded. Reserved rather than used: a rejected
    /// action draft produces no note today. The state exists so a future partial outcome has an
    /// honest place to land instead of being recorded as fully delivered.
    /// </summary>
    Degraded,

    /// <summary>No provider text exists. Carries a <c>FailureCode</c> and no Familiar speech.</summary>
    Failed
}
