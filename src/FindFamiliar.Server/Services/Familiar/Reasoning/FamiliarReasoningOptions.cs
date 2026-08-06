namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// Configuration under <c>Familiar:Reasoning:</c>.
///
/// <b>There is deliberately no API key property here.</b> A credential read from configuration is a
/// credential that can be written to <c>appsettings.json</c>, committed, and logged by a
/// configuration dump. Provider implementations read their key from the environment only
/// (specification §5.1, §9), so this type has nowhere to put one.
///
/// Slice 4 uses only <see cref="TimeoutSeconds"/>. The rest exists now so that adding a real
/// provider is a registration change rather than a new configuration surface.
/// </summary>
public sealed class FamiliarReasoningOptions
{
    public const string SectionName = "Familiar:Reasoning";

    /// <summary>Which implementation the composition root should bind. Unset means the unconfigured default.</summary>
    public string? Provider { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// How long the application waits before giving up on a provider. Enforced by a caller-owned
    /// <see cref="CancellationTokenSource"/> rather than an SDK default, so the timeout is this
    /// application's own and stays distinguishable from the caller going away.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    public string? Effort { get; set; }

    public const int DefaultTimeoutSeconds = 60;
    public const int DefaultMaxOutputTokens = 4096;

    /// <summary>
    /// Bounds the configured timeout. A zero or negative value would make every request time out
    /// before it started and read, on the page, as a provider that never answers.
    /// </summary>
    public const int MinTimeoutSeconds = 5;

    public const int MaxTimeoutSeconds = 300;

    public int ResolvedTimeoutSeconds =>
        Math.Clamp(TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);
}
