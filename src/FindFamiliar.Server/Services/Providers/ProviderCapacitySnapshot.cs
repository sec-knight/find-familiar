namespace FindFamiliar.Server.Services.Providers;

/// <summary>How much of a provider's allowance is left.</summary>
public enum ProviderCapacityStatus
{
    /// <summary>Comfortable headroom.</summary>
    Available,

    /// <summary>Usable, but a large piece of work may not finish.</summary>
    Constrained,

    /// <summary>Only small work should be attempted.</summary>
    Low,

    /// <summary>No allowance remains. A scheduling condition, never an implementation failure.</summary>
    Exhausted,

    /// <summary>
    /// The provider is reachable but its remaining capacity cannot be determined. This is the honest
    /// default, not a placeholder — see ADR-0011.
    /// </summary>
    Unknown,

    /// <summary>The provider itself is not configured or not reachable.</summary>
    Unavailable
}

/// <summary>How much the reported numbers can be trusted.</summary>
public enum ProviderCapacityConfidence
{
    /// <summary>The provider itself reported these figures.</summary>
    ProviderReported,

    /// <summary>Derived from observations this application made, not from the provider.</summary>
    Observed,

    /// <summary>An estimate. Must never be presented as a provider-reported balance.</summary>
    Estimated,

    /// <summary>Nothing is known.</summary>
    None
}

/// <summary>
/// One rolling usage window, when a provider exposes them. Percentages are only ever populated from a
/// source that actually reports them.
/// </summary>
public sealed record ProviderUsageWindow(
    string Label,
    double? UsedPercent,
    TimeSpan? WindowLength,
    DateTimeOffset? ResetsAt);

/// <summary>
/// A point-in-time reading of one provider's capacity.
///
/// Every quantitative field is nullable on purpose. A reader that cannot determine a value leaves it
/// null and the UI says so; it must never substitute a plausible number. <see cref="Source"/> and
/// <see cref="Confidence"/> exist so a reading can always be traced back to who claimed it.
/// </summary>
public sealed record ProviderCapacitySnapshot(
    string Provider,
    ProviderCapacityStatus Status,
    ProviderCapacityConfidence Confidence,
    DateTimeOffset ObservedAt,
    string Source,
    IReadOnlyList<ProviderUsageWindow> Windows,
    decimal? CreditsRemaining = null,
    DateTimeOffset? ResetsAt = null,
    string? Detail = null,
    string? Error = null)
{
    /// <summary>
    /// A reading that states plainly that nothing is known. Used when no reader can supply real data,
    /// which is the current situation for every provider — see ADR-0011.
    /// </summary>
    public static ProviderCapacitySnapshot Unknown(
        string provider,
        DateTimeOffset observedAt,
        string source,
        string? detail = null) =>
        new(
            provider,
            ProviderCapacityStatus.Unknown,
            ProviderCapacityConfidence.None,
            observedAt,
            source,
            [],
            Detail: detail);

    /// <summary>A reader that threw. The page still renders; the failure is shown, not swallowed.</summary>
    public static ProviderCapacitySnapshot Faulted(
        string provider,
        DateTimeOffset observedAt,
        string source,
        string error) =>
        new(
            provider,
            ProviderCapacityStatus.Unavailable,
            ProviderCapacityConfidence.None,
            observedAt,
            source,
            [],
            Error: error);

    /// <summary>
    /// True when this reading is old enough that it should not be relied on. A stale reading is
    /// displayed with its age rather than silently refreshed or hidden.
    /// </summary>
    public bool IsStale(DateTimeOffset nowUtc, TimeSpan maximumAge) =>
        nowUtc - ObservedAt > maximumAge;
}
