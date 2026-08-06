namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// The default registration: says plainly that no reasoning provider is configured.
///
/// This mirrors <c>UnknownProviderCapacityReader</c>, and for the same reason ADR-0011 gave — the
/// honest default is stated, not simulated. The application starts, the Familiar page renders, the
/// deterministic summary is complete, and a message sent on a stock build with no credentials is
/// durably saved and answered with the one sentence that is true. Nothing is stubbed to look as
/// though it worked.
///
/// It performs no I/O, so it cannot fail, and it is registered before any real provider so a
/// misconfigured deployment degrades to honesty rather than to an exception.
/// </summary>
public sealed class UnconfiguredFamiliarReasoningProvider : IFamiliarReasoningProvider
{
    /// <summary>
    /// The name the conversation service compares against to choose between "nothing is configured"
    /// and "something is configured and did not answer". Those are different facts about the server,
    /// and telling a user the second when the first is true sends them looking for an outage.
    /// </summary>
    public const string ProviderName = "none";

    public string Provider => ProviderName;

    public Task<FamiliarReasoningOutcome> RespondAsync(
        FamiliarReasoningRequest request,
        CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<FamiliarReasoningOutcome>(cancellationToken)
            : Task.FromResult(FamiliarReasoningOutcome.Failed(
            FamiliarReasoningStatus.Unavailable,
            new FamiliarProviderMetadata(ProviderName, null, null),
            "No reasoning provider is configured."));
}
