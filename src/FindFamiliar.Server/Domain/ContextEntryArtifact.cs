namespace FindFamiliar.Server.Domain;

/// <summary>
/// The complete artifact behind a bounded <see cref="ContextEntry"/>.
///
/// <b>Why this is a separate row rather than a wider column.</b> <see cref="ContextEntry.Content"/> is
/// read constantly — by retrieval, by the standing brief, by every task list and projection — and its
/// 12,000-character bound is what keeps those paths cheap and their budgets meaningful. Widening it to
/// hold a whole Planner proposal would make every one of those reads carry an artifact none of them
/// wants. So the excerpt stays where it is and stays bounded, and the artifact it excerpts lives here,
/// loaded only by the one path that asks for it.
///
/// <b>Why it exists at all.</b> Before it, a Planner artifact was cut to 12,000 characters at the
/// adapter and that cut string was the only copy — so the plan a human was asked to approve was a plan
/// whose remainder had never been stored by anything. An approval gate that cannot show the artifact it
/// gates is not a gate. See ADR-0020.
///
/// <b>Sensitivity is not decided here.</b> This row has no independent visibility of its own; it is
/// reachable only through its entry, and the entry's <see cref="ContextEntry.IsSensitive"/> flag and
/// kind decide who may see it. A document that could be fetched without its entry would be a second
/// answer to "may this be shown", and the two would drift.
/// </summary>
public sealed class ContextEntryArtifact
{
    /// <summary>
    /// Mirrors <c>RunnerProtocol.MaxCompleteArtifactLength</c>. The server does not reference the
    /// runner project, so the two constants are kept in lockstep by hand, exactly as the rest of the
    /// runner-bridge contract is (ADR-0006).
    /// </summary>
    public const int MaxContentLength = 200_000;

    public Guid Id { get; set; }

    public Guid ContextEntryId { get; set; }

    public ContextEntry ContextEntry { get; set; } = null!;

    /// <summary>The artifact as retained — whole, unless it exceeded the retention bound.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The artifact's length before any bound was applied.
    ///
    /// Equal to <see cref="Content"/>'s length in the ordinary case, and greater than it when the
    /// artifact exceeded what may be retained. Storing the true length is what lets a reader be told
    /// "this is 200,000 of 240,000 characters" instead of being handed a prefix that looks whole.
    /// </summary>
    public int OriginalLength { get; set; }

    /// <summary>True when everything the producer wrote is in <see cref="Content"/>.</summary>
    public bool IsComplete => OriginalLength <= Content.Length;

    public DateTime CreatedUtc { get; set; }
}
