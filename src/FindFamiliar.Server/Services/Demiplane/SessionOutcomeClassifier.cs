using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Demiplane;

/// <summary>
/// Classifies why a session was cancelled, using only the fixed diagnostic strings this codebase's
/// own runner writes.
///
/// This is the line that keeps the Demiplane honest. The runner records a durable cancellation whose
/// reason is <c>"Runner cancelled: {category}."</c> for a small, closed set of categories it controls
/// (ADR-0006 fixed those strings deliberately, and they contain no secrets and no model output). Those
/// we recognise.
///
/// Anything else — including every reason a human typed — is left unclassified. We never pattern-match
/// a summary, a raw output, or a review verdict to decide that a build or a test failed: ADR-0005
/// rejected exactly that, and a Demiplane that guessed would be confidently wrong at the worst moment.
/// </summary>
public static class SessionOutcomeClassifier
{
    private const string RunnerPrefix = "Runner cancelled: ";

    /// <summary>
    /// Maps a cancellation reason to a failure category, or null when it was not machine-recorded —
    /// which means a human cancelled it deliberately.
    /// </summary>
    public static TaskDisplayReasonCode? ClassifyCancellation(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();
        if (!trimmed.StartsWith(RunnerPrefix, StringComparison.Ordinal))
        {
            // A human's own words. Their reason is displayed verbatim; we do not interpret it.
            return null;
        }

        var category = trimmed[RunnerPrefix.Length..].TrimEnd('.').Trim();

        return category switch
        {
            "adapter-launch-failed" => TaskDisplayReasonCode.ProviderRuntimeLaunchFailed,
            "adapter-timeout" => TaskDisplayReasonCode.ProviderRunTimedOut,
            "adapter-non-zero-exit" => TaskDisplayReasonCode.ProviderRequestFailed,
            "adapter-output-oversized" => TaskDisplayReasonCode.ProviderResponseUnusable,
            "adapter-output-malformed" => TaskDisplayReasonCode.ProviderResponseUnusable,
            "adapter-output-invalid" => TaskDisplayReasonCode.ProviderResponseUnusable,

            // A category this version does not recognise. It was machine-recorded, so the session did
            // fail, but we will not invent a cause for it.
            _ => TaskDisplayReasonCode.Unknown
        };
    }

    /// <summary>
    /// True when the cancellation was recorded by the runner rather than chosen by a human, which is
    /// the difference between <see cref="TaskDisplayState.Failed"/> and
    /// <see cref="TaskDisplayState.Cancelled"/>.
    /// </summary>
    public static bool WasMachineRecorded(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && reason.TrimStart().StartsWith(RunnerPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The durable cancellation reason for a session, or null when none was recorded.
    /// Cancellation writes exactly one Handoff-kind entry (ADR-0005 §2), so this reads real
    /// persisted history rather than anything reconstructed at render time.
    /// </summary>
    public static string? FindCancellationReason(IEnumerable<ContextEntry> entries, Guid sessionId) =>
        entries
            .Where(entry =>
                entry.SourceSessionId == sessionId
                && entry.Kind == ContextEntryKind.Handoff)
            .OrderByDescending(entry => entry.CreatedUtc)
            .Select(entry => entry.Content)
            .FirstOrDefault();
}
