using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// The whole surface a reasoning provider gets: a value in, a value out.
///
/// Nothing in this namespace names Claude, Anthropic, OpenAI or any SDK. Swapping the implementation
/// is a DI registration change, and the type graph is what enforces that — an implementation receives
/// a <see cref="FamiliarReasoningRequest"/> and returns a <see cref="FamiliarReasoningOutcome"/>, and
/// has no <c>DbContext</c>, no <c>IWorkflowDispatchService</c>, no <c>HttpContext</c> and no tools.
/// There is no code path from a reply to the database that does not pass through a human
/// confirmation and a re-validating application service.
/// </summary>
public interface IFamiliarReasoningProvider
{
    /// <summary>
    /// The provider's name, as stored on a Familiar message and shown beside it. Compared against
    /// <see cref="UnconfiguredFamiliarReasoningProvider.ProviderName"/> to tell "nothing is
    /// configured" apart from "something is configured and did not answer" — two different facts
    /// that deserve two different sentences.
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// Answers, or reports why it did not.
    ///
    /// <b>This never throws.</b> Every failure is a typed status with a safe <c>Detail</c>, exactly as
    /// <c>IProviderCapacityReader</c> returns <c>ProviderCapacitySnapshot.Faulted</c> rather than
    /// breaking a page. An implementation that lets an exception escape has broken its contract, and
    /// the conversation service does not catch on its behalf beyond cancellation.
    /// </summary>
    Task<FamiliarReasoningOutcome> RespondAsync(
        FamiliarReasoningRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One visible turn as the provider sees it. Content only — no ids, no timestamps, no delivery
/// state, and no <see cref="FamiliarMessageAuthor.System"/> turns, which are page-composed error
/// notes that would teach a model to imitate error text.
/// </summary>
public sealed record FamiliarTurn(FamiliarMessageAuthor Author, string Content);

/// <summary>
/// Everything sent, and nothing else. Bounded by the caller before it gets here: the snapshot by
/// <see cref="ProjectSnapshot.MaxSnapshotCharacters"/>, the history by
/// <see cref="FamiliarRequestEnvelope"/>, the user message by
/// <see cref="FamiliarConversationService.MaxUserMessageCharacters"/>.
/// </summary>
public sealed record FamiliarReasoningRequest(
    ProjectSnapshot Snapshot,
    IReadOnlyList<FamiliarTurn> History,
    string UserMessage,
    string BehaviorContract);

/// <summary>
/// Why a reasoning request ended the way it did. Every member maps to exactly one sentence in
/// <see cref="FamiliarFailureWording"/>; adding a member without adding wording fails a test rather
/// than falling through to a generic string.
/// </summary>
public enum FamiliarReasoningStatus
{
    /// <summary>A reply arrived. Requires a non-empty <see cref="FamiliarReasoningOutcome.Reply"/>.</summary>
    Answered,

    /// <summary>Unreachable, or not configured at all. The page distinguishes those two by provider name.</summary>
    Unavailable,

    /// <summary>Credentials missing or rejected. A server configuration problem, not a user error.</summary>
    Unauthenticated,

    /// <summary>The application's own timeout elapsed. Distinct from the caller cancelling.</summary>
    TimedOut,

    RateLimited,

    /// <summary>A response arrived that this application could not use.</summary>
    Malformed,

    /// <summary>The provider refused to answer. A real outcome, not a fault.</summary>
    Declined
}

/// <summary>
/// Who answered, and how long it took. Operational metadata, rendered as attribution and never as
/// content — so a later reader of a conversation knows which model said what.
/// </summary>
public sealed record FamiliarProviderMetadata(string Provider, string? Model, int? LatencyMs);

/// <summary>
/// A structured action a provider suggested. <b>Inert in this slice.</b>
///
/// It is declared now so Slice 5 adds validation and persistence without changing this interface,
/// but nothing in Slice 4 reads <see cref="FamiliarReasoningOutcome.Actions"/>: no draft is
/// validated, no proposal row is written, and no dispatch is reachable from here. <see cref="Kind"/>
/// is a string rather than <c>FamiliarActionKind</c> deliberately — an unparseable kind must produce
/// no proposal at all, and typing it as the enum would require a member for "something else".
/// </summary>
public sealed record ProposedActionDraft(
    string Kind,
    string? Title,
    string? RequestedOutcome,
    Guid? TargetTaskId);

/// <summary>
/// What a provider returned.
///
/// <see cref="Detail"/> is text the implementation authored about its own status — never an exception
/// message, a host, a path, a header or a fragment of a credential. It is operational context for a
/// log or a test, and it is <b>never persisted and never rendered</b>: the page composes what a user
/// reads from <see cref="FamiliarFailureWording"/>, so a provider cannot put words in front of a
/// person even by accident.
/// </summary>
/// <param name="EvidenceIds">
/// Identifiers the provider cited, as bare ids and nothing more.
///
/// Deliberately not typed by kind and deliberately carrying no label. The application looks each id
/// up in the exact snapshot it sent, and that lookup is what decides both what the id <i>is</i> and
/// what it is <i>called</i> — so a provider cannot mislabel a session as a task, cannot attach prose
/// of its own to a record, and cannot cite anything it was not shown. An id that resolves to nothing
/// is dropped without comment, because a hallucinated citation is not an event worth reporting to a
/// user.
/// </param>
public sealed record FamiliarReasoningOutcome(
    FamiliarReasoningStatus Status,
    string? Reply,
    IReadOnlyList<ProposedActionDraft> Actions,
    IReadOnlyList<Guid> EvidenceIds,
    FamiliarProviderMetadata Metadata,
    string? Detail)
{
    public static FamiliarReasoningOutcome Failed(
        FamiliarReasoningStatus status,
        FamiliarProviderMetadata metadata,
        string detail) =>
        new(status, null, [], [], metadata, detail);

    public static FamiliarReasoningOutcome Answered(
        string reply,
        FamiliarProviderMetadata metadata,
        IReadOnlyList<ProposedActionDraft>? actions = null,
        IReadOnlyList<Guid>? evidenceIds = null) =>
        new(FamiliarReasoningStatus.Answered, reply, actions ?? [], evidenceIds ?? [], metadata, null);
}
