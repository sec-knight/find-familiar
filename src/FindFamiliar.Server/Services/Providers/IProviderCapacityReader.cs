namespace FindFamiliar.Server.Services.Providers;

/// <summary>
/// Collects capacity information for one provider.
///
/// Provider-specific knowledge lives behind this boundary and nowhere else, so the Demiplane never
/// learns what a Claude rate-limit envelope or a Codex rolling window looks like. A reader may throw;
/// the aggregating service turns that into a visible Unavailable reading rather than a broken page.
///
/// A reader must not invent values. If it cannot determine remaining capacity it returns
/// <see cref="ProviderCapacitySnapshot.Unknown"/>. Presenting an estimate as a provider-reported
/// balance is the specific failure this interface exists to prevent.
/// </summary>
public interface IProviderCapacityReader
{
    /// <summary>The provider this reader reports on, e.g. "Claude".</summary>
    string Provider { get; }

    Task<ProviderCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken = default);
}
