namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// Everything a generator is given about the turn it is answering.
///
/// Deliberately not the entity. A generator reads no rows and writes none: the host owns the turn's
/// lifecycle, so a generator cannot leave a turn in a state nothing will finish.
/// </summary>
public sealed record FamiliarChatGenerationRequest(
    Guid ChatId,
    Guid TurnId,
    int Sequence,
    string UserText,
    Guid? FocusProjectId);

/// <summary>
/// Where output goes as it is produced.
///
/// Appends land in the persisted turn, not in the connection that asked for the reply — that is what
/// "detached from the requesting connection" means in practice. A caller that has gone away changes
/// nothing about where the text ends up.
/// </summary>
public interface IFamiliarChatOutputSink
{
    /// <summary>
    /// Appends a fragment to the turn's accumulated output. Fragments arriving after the cap in
    /// <see cref="Domain.FamiliarChatTurn.MaxOutputLength"/> is reached are discarded rather than
    /// allowed to fail the write.
    /// </summary>
    Task AppendAsync(string fragment, CancellationToken cancellationToken = default);
}

/// <summary>
/// How a generation ended.
///
/// A failure carries this application's own sentence, never a provider's error text — the same rule
/// <c>FamiliarFailureWording</c> holds for the per-project conversation, restated here because it is
/// the rule most easily lost while wiring a provider up.
/// </summary>
/// <summary>
/// Who answered and what it cost. Operational metadata, recorded as attribution and never rendered as
/// content — so a later reader of a conversation knows which model said what, and so a turn can be
/// re-run against a different one and compared.
/// </summary>
public sealed record FamiliarChatGenerationMetadata(
    string? ProviderName = null,
    string? ProviderModel = null,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>
/// How a generation ended.
///
/// A failure carries this application's own sentence, never a provider's error text — the same rule
/// <c>FamiliarFailureWording</c> holds for the per-project conversation, restated here because it is
/// the rule most easily lost while wiring a provider up.
///
/// <see cref="Sentence"/> is written only where no output was produced. A generation that failed part
/// way through has already appended its own note through the sink, so the partial reply a person read
/// is never overwritten by an explanation of why it stopped.
/// </summary>
public sealed record FamiliarChatGenerationOutcome(
    bool Succeeded,
    string? FailureCode = null,
    string? Sentence = null,
    FamiliarChatGenerationMetadata? Metadata = null)
{
    public static readonly FamiliarChatGenerationOutcome Completed = new(true);

    public static FamiliarChatGenerationOutcome Answered(FamiliarChatGenerationMetadata metadata) =>
        new(true, Metadata: metadata);

    public static FamiliarChatGenerationOutcome Failed(
        string failureCode,
        string? sentence,
        FamiliarChatGenerationMetadata? metadata = null) =>
        new(false, failureCode, sentence, metadata);
}

/// <summary>
/// The talk lane's producer of replies (ADR-0013).
///
/// Independent of the Runner and its Claude Code adapter by design: an agentic session spawns a
/// process against a repository, and a conversational turn must not. Implementations are chosen by
/// configuration, never by a code change.
///
/// An implementation must not throw. The host classifies a thrown exception as a fault rather than
/// letting it escape, but a generator that throws has broken this contract.
/// </summary>
public interface IFamiliarChatGenerator
{
    /// <summary>A name for this generator, for operator-facing text. Never rendered as speech.</summary>
    string Name { get; }

    Task<FamiliarChatGenerationOutcome> GenerateAsync(
        FamiliarChatGenerationRequest request,
        IFamiliarChatOutputSink sink,
        CancellationToken cancellationToken = default);
}
