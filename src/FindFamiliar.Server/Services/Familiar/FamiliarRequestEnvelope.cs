using System.Text.Json;
using FindFamiliar.Server.Services.Familiar.Reasoning;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>What fitting a request produced, and what had to be dropped to make it fit.</summary>
/// <param name="Fits">
/// False when the request is still over budget with no history left to drop. The caller must not
/// invoke the provider: a project that does not fit is refused, never silently cut down.
/// </param>
/// <param name="History">The turns that survived, oldest first.</param>
/// <param name="DroppedTurns">How many turns were dropped to fit. Zero when nothing was.</param>
/// <param name="Characters">The measured length of the complete serialized request.</param>
public sealed record FamiliarEnvelopeResult(
    bool Fits,
    IReadOnlyList<FamiliarTurn> History,
    int DroppedTurns,
    int Characters);

/// <summary>
/// Measures the complete provider request immediately before it is sent.
///
/// <see cref="ProjectSnapshot.EstimatedCharacters"/> is the size of the snapshot alone, and its own
/// doc comment names this as the thing that must happen next: "The final provider envelope must be
/// serialized and checked again immediately before transmission." Treating the snapshot's estimate
/// as the request's size would be wrong by the size of everything else in the request.
///
/// The gap is not marginal. History is bounded at
/// <see cref="FamiliarConversationService.MaxHistoryTurns"/> turns and a message may hold
/// <c>FamiliarMessage.MaxContentLength</c> characters, so history alone can reach 80 000 characters —
/// more than three times the snapshot budget. A count-based bound on history is therefore not a size
/// bound at all, which is why this trims by measurement and re-measures after every drop.
///
/// Everything is measured with <see cref="ProjectSnapshotSerialization.Options"/>, the one canonical
/// serializer. A second <see cref="JsonSerializerOptions"/> here would produce a second budget that
/// disagrees with the first by a comma, which is the exact failure that file exists to prevent.
/// </summary>
public static class FamiliarRequestEnvelope
{
    /// <summary>
    /// The whole-request budget, in characters of the canonical serialized form.
    ///
    /// Larger than <see cref="ProjectSnapshot.MaxSnapshotCharacters"/> because the request is the
    /// snapshot plus the contract plus the history plus the message, and a budget that left no room
    /// for those would refuse every request that carried any conversation at all.
    ///
    /// Characters rather than tokens, for the reason the snapshot budget gives: counting tokens means
    /// asking a provider, and this must be testable with none configured.
    /// </summary>
    public const int MaxEnvelopeCharacters = 40_000;

    /// <summary>
    /// The limitation line recorded when history was trimmed, so an answer built on a shortened
    /// conversation can say so — the same discipline every other bound in the snapshot follows.
    /// </summary>
    public static string DroppedHistoryLimitation(int droppedTurns) =>
        $"The earliest {droppedTurns} message{(droppedTurns == 1 ? string.Empty : "s")} of this conversation "
        + "were too large to include, so this answer does not take them into account.";

    /// <summary>
    /// Fits the request inside <see cref="MaxEnvelopeCharacters"/> by dropping history oldest-first,
    /// re-measuring after each drop.
    ///
    /// Oldest-first because the current message and the turns nearest it are what the question is
    /// about; dropping the newest to keep the oldest would answer a conversation nobody is having.
    /// The snapshot and the contract are never trimmed — the snapshot has already been reduced by
    /// its own documented policy, and a partially-sent contract is a different contract.
    /// </summary>
    public static FamiliarEnvelopeResult Fit(
        ProjectSnapshot snapshot,
        IReadOnlyList<FamiliarTurn> history,
        string userMessage,
        string behaviorContract)
    {
        var kept = history.ToList();

        while (true)
        {
            var characters = Measure(snapshot, kept, userMessage, behaviorContract);

            if (characters <= MaxEnvelopeCharacters)
            {
                return new FamiliarEnvelopeResult(true, kept, history.Count - kept.Count, characters);
            }

            if (kept.Count == 0)
            {
                // Nothing left to drop. The snapshot, the contract and this one message do not fit,
                // so the honest move is to refuse rather than to keep cutting into the project.
                return new FamiliarEnvelopeResult(false, kept, history.Count, characters);
            }

            kept.RemoveAt(0);
        }
    }

    /// <summary>
    /// The complete request as it will be serialized, measured in characters.
    ///
    /// The snapshot is measured through <see cref="ProjectSnapshotSerialization.ForMeasurement"/> for
    /// the reason that method exists: two of its fields would otherwise depend on their own result
    /// and a third varies in width with the clock, so an unfixed snapshot makes the same request
    /// measure differently on two consecutive page loads.
    /// </summary>
    public static int Measure(
        ProjectSnapshot snapshot,
        IReadOnlyList<FamiliarTurn> history,
        string userMessage,
        string behaviorContract) =>
        JsonSerializer.Serialize(
            new FamiliarReasoningRequest(
                ProjectSnapshotSerialization.ForMeasurement(snapshot),
                history,
                userMessage,
                behaviorContract),
            ProjectSnapshotSerialization.Options).Length;
}
