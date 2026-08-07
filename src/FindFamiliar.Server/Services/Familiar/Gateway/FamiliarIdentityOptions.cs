namespace FindFamiliar.Server.Services.Familiar.Gateway;

/// <summary>
/// Who this Familiar is, to a body that is about to speak as it.
///
/// Configuration rather than a table, and the reason is proportion rather than principle: the first
/// slice needs exactly three strings, and a migration plus an editor page to hold three strings would
/// be work spent on the wrong end of the problem. ADR-0016 records the decision and what would change
/// it — the moment identity acquires anything a person would want to edit without a deploy, or
/// anything that differs per body, it becomes a row.
///
/// It is deliberately not the persona. <see cref="Guidance"/> is a sentence or two of register, not a
/// system prompt: the Familiar's character is enforced by this server on the paths this server owns,
/// and shipping a paragraph of instruction to an external client would be handing that character to
/// whichever body happened to connect, to keep or discard as it liked.
/// </summary>
public sealed class FamiliarIdentityOptions
{
    public const string SectionName = "Familiar:Identity";

    /// <summary>
    /// What the Familiar is called. The default is the common noun rather than a name, because a
    /// deployment that has not chosen one has not chosen one, and inventing a name here would put a
    /// character in front of a person who never asked for it.
    /// </summary>
    public string Name { get; set; } = "Familiar";

    public string Description { get; set; } =
        "A user-owned durable AI continuity layer. It holds the projects, decisions, sessions and "
        + "recorded context that persist across conversations, clients and devices.";

    /// <summary>
    /// A compact note on register, or null. Bounded by <see cref="MaxGuidanceLength"/> so this cannot
    /// quietly become the system prompt it is not meant to be.
    /// </summary>
    public string? Guidance { get; set; }

    public const int MaxGuidanceLength = 500;

    public string ResolvedName =>
        string.IsNullOrWhiteSpace(Name) ? "Familiar" : Name.Trim();

    public string? ResolvedGuidance =>
        string.IsNullOrWhiteSpace(Guidance)
            ? null
            : Guidance.Trim().Length <= MaxGuidanceLength
                ? Guidance.Trim()
                : Guidance.Trim()[..MaxGuidanceLength];
}
