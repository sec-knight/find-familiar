namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// Configuration for any endpoint that speaks the OpenAI chat-completions shape.
///
/// That shape is the closest thing this ecosystem has to a lingua franca: local runtimes
/// (llama.cpp's server, vLLM, LM Studio, Ollama's <c>/v1</c>) and hosted services (OpenAI, Groq,
/// Together, DeepInfra, OpenRouter) all speak it. One implementation and a base address therefore
/// cover both "run it on my own machine" and "point it at something cheap", which is the property
/// this application wants most from a reasoning provider — the model is the part a person should be
/// able to choose without touching code.
///
/// <b>No key lives here.</b> <see cref="ApiKeyVariable"/> names an <i>environment variable</i>; the
/// value is read from the environment at startup and never from configuration, so a key cannot be
/// committed to <c>appsettings.json</c>, printed by a configuration dump, or checked into a
/// repository. Naming the variable rather than fixing it is what lets one binary serve
/// <c>OPENAI_API_KEY</c>, <c>GROQ_API_KEY</c>, <c>OPENROUTER_API_KEY</c> and the rest.
/// </summary>
public sealed class OpenAiCompatibleReasoningOptions
{
    public const string SectionName = "Familiar:Reasoning:OpenAiCompatible";

    /// <summary>
    /// The endpoint's root, without the <c>/chat/completions</c> path.
    ///
    /// Defaults to a local Ollama, because the zero-cost, zero-credential option is the right thing
    /// to have to actively opt out of.
    /// </summary>
    public string BaseAddress { get; set; } = "http://127.0.0.1:11434/v1";

    /// <summary>The model identifier, exactly as the endpoint names it.</summary>
    public string Model { get; set; } = "qwen3:4b";

    /// <summary>
    /// Which environment variable holds the key, or null for an endpoint that needs none — the usual
    /// case for a local runtime.
    /// </summary>
    public string? ApiKeyVariable { get; set; }

    /// <summary>
    /// The name recorded on a Familiar message and shown beside it. Set it to something a reader will
    /// recognise months later — "Groq (llama-3.3-70b)" beats "OpenAiCompatible".
    /// </summary>
    public string DisplayName { get; set; } = DefaultDisplayName;

    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    /// <summary>
    /// Generous by default, because the same setting serves a hosted endpoint that answers in a
    /// second and a local model on a slow machine that spends minutes reading the prompt before it
    /// writes anything. A timeout tuned for the first reports the second as broken.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>
    /// Whether to ask for a schema-constrained reply via <c>response_format</c>.
    ///
    /// On by default, and worth keeping on: it is what makes a small local model emit valid
    /// structured output reliably rather than approximately. Endpoints that do not implement it may
    /// reject the request, so it can be turned off — the reply is parsed and validated either way,
    /// and an unparseable one is reported rather than guessed at.
    /// </summary>
    public bool UseStructuredOutput { get; set; } = true;

    public const string DefaultDisplayName = "Local model";
    public const int DefaultMaxOutputTokens = 2048;
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>
    /// The key from the environment, or null when none is configured or set.
    ///
    /// Null is a supported answer rather than a failure: local runtimes need no key, and a hosted
    /// endpoint missing one falls back to the unconfigured provider so the page still renders and
    /// says why.
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
}
