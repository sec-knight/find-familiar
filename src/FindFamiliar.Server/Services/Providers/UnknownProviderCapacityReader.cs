namespace FindFamiliar.Server.Services.Providers;

/// <summary>
/// The reader shipped for Claude in Sprint 10. It reports Unknown, always, and says why.
///
/// This is not a stub awaiting completion — it is the only honest answer available. The Claude Code
/// CLI this project invokes exposes no non-interactive usage surface: no <c>usage</c> subcommand, no
/// <c>--limits</c> flag, and nothing cached under the user's Claude directory carrying a limit, a
/// window or a reset time. The adapter's JSON envelope deliberately discards everything except
/// <c>is_error</c>, <c>result</c> and <c>permission_denials</c> (ADR-0007).
///
/// The alternative — estimating from local transcript token counts, or reading another tool's
/// rate-limit file — would put a number on screen that describes something other than this
/// application's provider. ADR-0011 records what a real reader would require.
/// </summary>
public sealed class UnknownProviderCapacityReader(string provider, TimeProvider timeProvider, string detail)
    : IProviderCapacityReader
{
    public string Provider { get; } = provider;

    public Task<ProviderCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderCapacitySnapshot.Unknown(
            Provider,
            timeProvider.GetUtcNow(),
            source: "no-usage-source",
            detail: detail));
}
