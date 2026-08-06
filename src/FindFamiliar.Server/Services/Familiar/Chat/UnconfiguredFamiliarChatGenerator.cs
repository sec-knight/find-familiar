namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// The generator registered when no conversational provider is configured, and Sprint 12 slice 1's
/// only one.
///
/// It is the honest default, exactly as <c>UnconfiguredFamiliarReasoningProvider</c> and
/// <c>UnknownProviderCapacityReader</c> are: with nothing configured the application starts,
/// <c>/Familiar</c> renders, a conversation is created, a turn is durably recorded, generation runs
/// detached and finishes, and the one sentence that is true is what appears. No credential is
/// required to run this application at all.
///
/// It fails rather than completes, and that is the point. A turn with no provider behind it has no
/// reply, and recording one would be this application inventing speech — the thing the whole
/// Demiplane rule exists to prevent. The failure is a real, terminal, tested path through the same
/// machinery a real provider will use.
/// </summary>
public sealed class UnconfiguredFamiliarChatGenerator : IFamiliarChatGenerator
{
    public const string ProviderName = "Unconfigured";

    public const string FailureCode = "chat-provider-unconfigured";

    public const string Sentence =
        "No conversational provider is configured, so there is nothing to answer with. "
        + "Your message was saved and the conversation is intact.";

    public string Name => ProviderName;

    public Task<FamiliarChatGenerationOutcome> GenerateAsync(
        FamiliarChatGenerationRequest request,
        IFamiliarChatOutputSink sink,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(FamiliarChatGenerationOutcome.Failed(FailureCode, Sentence));
}
