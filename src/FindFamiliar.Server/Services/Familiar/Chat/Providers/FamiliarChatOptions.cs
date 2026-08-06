namespace FindFamiliar.Server.Services.Familiar.Chat.Providers;

/// <summary>
/// Configuration for the talk lane (ADR-0013), independent of <c>Familiar:Reasoning</c>.
///
/// Two seams, two settings sections, deliberately. The per-project Familiar and the system-wide
/// conversation answer different questions with different latency budgets, and a single section would
/// mean changing the model for one silently changed it for the other.
///
/// Provider, model and account identity travel as one unit so a provider swap cannot silently inherit
/// the wrong credential. <see cref="Team"/> is a label this application never sends anywhere — it
/// exists so an operator reading a configuration dump can tell which account's traffic this is.
///
/// <b>No key lives here.</b> <see cref="ApiKeyVariable"/> names an <i>environment variable</i>; the
/// value is read from the environment at startup and never through configuration, so a key cannot be
/// committed, printed by a configuration dump, or bound into <c>IConfiguration</c> at all. This is why
/// the variable is <c>XAI_API_KEY</c> rather than the <c>XAI__ApiKey</c> ADR-0013 first wrote: the
/// double underscore is ASP.NET's section separator and would have bound the secret into
/// configuration, which is exactly what naming the variable avoids.
/// </summary>
public sealed class FamiliarChatOptions
{
    public const string SectionName = "Familiar:Chat";

    /// <summary>
    /// Which implementation answers. Anything other than <see cref="XaiProvider"/> leaves the
    /// unconfigured generator in place, so a typo degrades to an honest sentence rather than to a
    /// dead stream.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>The value of <see cref="Provider"/> that selects the OpenAI-compatible xAI endpoint.</summary>
    public const string XaiProvider = "xai";

    /// <summary>The endpoint's root, without the <c>/chat/completions</c> path.</summary>
    public string BaseAddress { get; set; } = "https://api.x.ai/v1";

    /// <summary>
    /// The model identifier, exactly as the endpoint names it.
    ///
    /// Configuration, never a compile-time constant. Provider model rosters churn, and a retired
    /// model must surface as a visible error in the UI rather than as a dead stream — which is what
    /// the status classification in the provider is for.
    /// </summary>
    public string Model { get; set; } = "grok-4.1-fast";

    /// <summary>
    /// The account this traffic belongs to. A label for operators only: it is never sent, never
    /// stored on a turn, and never rendered to a user.
    /// </summary>
    public string? Team { get; set; }

    /// <summary>Which environment variable holds the key. The key itself never appears in configuration.</summary>
    public string? ApiKeyVariable { get; set; }

    /// <summary>
    /// The name recorded on a turn and shown beside it. Set it to something a reader will recognise
    /// months later — "xAI (grok-4.1-fast)" beats "xai".
    /// </summary>
    public string DisplayName { get; set; } = "xAI";

    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>
    /// Bounds the whole stream, not the first token.
    ///
    /// Generous, because it is a backstop against a hung connection rather than a latency target. The
    /// property that actually matters to a person — first token in under a second — is a consequence
    /// of streaming, not of this number.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// The key from the environment, or null when none is configured or set.
    ///
    /// Null is a supported answer rather than a failure: the composition root falls back to the
    /// unconfigured generator, so the page still renders and says exactly why.
    /// </summary>
    public string? ReadApiKey(Func<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyVariable))
        {
            return null;
        }

        var value = (environment ?? Environment.GetEnvironmentVariable)(ApiKeyVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>True when this configuration selects the xAI provider and a key is actually present.</summary>
    public bool IsConfigured(Func<string, string?>? environment = null) =>
        string.Equals(Provider, XaiProvider, StringComparison.OrdinalIgnoreCase)
        && ReadApiKey(environment) is not null;
}
