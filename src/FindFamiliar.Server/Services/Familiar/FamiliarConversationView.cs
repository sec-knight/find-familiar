using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// One project's conversation as the page renders it: values only, already ordered, already
/// project-filtered.
///
/// The page receives this rather than entities for the reason <see cref="ProjectSnapshot"/> exists —
/// no navigation property, no lazy load, and nothing reachable from a message that was not
/// deliberately put on it. In particular there is no route from here to a prompt, a raw provider
/// payload or a thinking block, because no such column exists to project from.
/// </summary>
public sealed record FamiliarConversationView(
    Guid ConversationId,
    Guid ProjectId,
    IReadOnlyList<FamiliarMessageView> Messages,
    IReadOnlyList<FamiliarProposalView> PendingProposals);

/// <summary>
/// One visible turn. <see cref="Content"/> is rendered encoded and never interpreted: a URL a
/// provider wrote is characters, not a link, and a command it wrote is characters, not a button.
/// </summary>
public sealed record FamiliarMessageView(
    Guid MessageId,
    FamiliarMessageAuthor Author,
    int Sequence,
    string Content,
    DateTime CreatedUtc,
    string? ProviderName,
    string? ProviderModel,
    FamiliarMessageDelivery Delivery,
    string? FailureCode,
    IReadOnlyList<FamiliarEvidenceView> Evidence);

/// <summary>Server-composed provenance. <see cref="Label"/> was written here, never by a provider.</summary>
public sealed record FamiliarEvidenceView(
    FamiliarEvidenceKind Kind,
    Guid ReferenceId,
    string Label);

/// <summary>
/// A pending proposal, carried so the page can render it in its own region — outside any message
/// bubble, where it cannot be mistaken for a sentence.
///
/// It is a record of what a human was shown. Nothing here is authority to act: this slice renders it
/// and offers no control that would decide it.
/// </summary>
public sealed record FamiliarProposalView(
    Guid ProposalId,
    Guid MessageId,
    FamiliarActionKind Kind,
    Guid ConcurrencyToken,
    int ObservedContextRevision,
    string? Title,
    string? RequestedOutcome,
    Guid? TargetTaskId,
    string? TargetTaskTitle,
    DateTime CreatedUtc);
