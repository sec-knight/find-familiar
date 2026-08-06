using System.Net;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The talk lane's classification and wording, which are what a person actually experiences when the
/// provider misbehaves.
///
/// These are cheap tests guarding an expensive mistake. Every failure a provider can produce has to
/// arrive as one of a fixed set of statuses, and every status has to have a sentence this application
/// wrote — otherwise a provider's own error text is one careless edit away from being shown to
/// somebody, and error bodies routinely echo the request, the host, the account, or part of a key.
/// </summary>
public sealed class FamiliarChatProviderContractTests
{
    /// <summary>
    /// Exhaustive by enumeration rather than by trust: adding a status without wording fails here
    /// instead of silently falling through to the generic sentence.
    /// </summary>
    [Fact]
    public void Every_status_has_its_own_code_and_sentence()
    {
        var statuses = Enum.GetValues<FamiliarChatProviderStatus>();
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var status in statuses)
        {
            var note = FamiliarChatFailureWording.For(status);

            Assert.False(string.IsNullOrWhiteSpace(note.Code), $"{status} has no code.");
            Assert.False(string.IsNullOrWhiteSpace(note.Sentence), $"{status} has no sentence.");
            Assert.True(codes.Add(note.Code), $"{status} reuses the code {note.Code}.");
        }

        Assert.Equal(statuses.Length, codes.Count);
    }

    /// <summary>
    /// Codes are matched against in the page and in operational queries, so they are a contract. A
    /// code that changed shape would silently stop matching rather than fail.
    /// </summary>
    [Fact]
    public void Codes_are_stable_lowercase_kebab_and_fit_their_column()
    {
        foreach (var status in Enum.GetValues<FamiliarChatProviderStatus>())
        {
            var code = FamiliarChatFailureWording.For(status).Code;

            Assert.StartsWith("chat-", code, StringComparison.Ordinal);
            Assert.Equal(code.ToLowerInvariant(), code);
            Assert.True(
                code.Length <= FindFamiliar.Server.Domain.FamiliarChatTurn.MaxFailureCodeLength,
                $"{code} does not fit the FailureCode column.");
        }
    }

    /// <summary>
    /// The wording a person reads must never name a host, a path, a credential or an exception. These
    /// are the words that would appear if somebody wired a provider's message through, so they are
    /// asserted absent rather than assumed.
    /// </summary>
    [Fact]
    public void No_sentence_leaks_operational_detail()
    {
        string[] forbidden =
        [
            "http", "api.x.ai", "exception", "stack", "token=", "bearer", "apikey", "api_key", "/v1"
        ];

        foreach (var status in Enum.GetValues<FamiliarChatProviderStatus>())
        {
            var sentence = FamiliarChatFailureWording.For(status).Sentence;

            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, sentence, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// A truncation note is appended to a partial reply, so it must read as a continuation rather than
    /// as a whole sentence replacing what came before.
    /// </summary>
    [Fact]
    public void The_truncation_note_keeps_the_status_code_and_reads_as_an_addition()
    {
        var truncated = FamiliarChatFailureWording.Truncated(FamiliarChatProviderStatus.TimedOut);

        Assert.Equal(FamiliarChatFailureWording.For(FamiliarChatProviderStatus.TimedOut).Code, truncated.Code);
        Assert.StartsWith("\n", truncated.Sentence, StringComparison.Ordinal);
        Assert.Contains("incomplete", truncated.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- status classification

    /// <summary>
    /// A retired or renamed model is the failure this project is most likely to actually hit, because
    /// model rosters churn and the id is configuration. It must classify as Malformed, which is what
    /// makes it a visible error rather than a dead stream.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void A_rejected_request_is_malformed(HttpStatusCode statusCode) =>
        Assert.Equal(
            FamiliarChatProviderStatus.Malformed,
            OpenAiCompatibleFamiliarChatProvider.ClassifyStatus(statusCode));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public void A_credential_problem_is_unauthenticated(HttpStatusCode statusCode) =>
        Assert.Equal(
            FamiliarChatProviderStatus.Unauthenticated,
            OpenAiCompatibleFamiliarChatProvider.ClassifyStatus(statusCode));

    [Fact]
    public void Rate_limiting_is_its_own_status() =>
        Assert.Equal(
            FamiliarChatProviderStatus.RateLimited,
            OpenAiCompatibleFamiliarChatProvider.ClassifyStatus(HttpStatusCode.TooManyRequests));

    /// <summary>
    /// Anything unrecognised is Unavailable. From a person's side an unclassified failure and an
    /// unreachable endpoint are the same fact.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void An_unclassified_status_is_unavailable(HttpStatusCode statusCode) =>
        Assert.Equal(
            FamiliarChatProviderStatus.Unavailable,
            OpenAiCompatibleFamiliarChatProvider.ClassifyStatus(statusCode));

    // ---------------------------------------------------------------- configuration

    /// <summary>
    /// The key is read from the environment and never from configuration, so it cannot be committed or
    /// printed by a configuration dump. The options object names the variable; it never holds a value.
    /// </summary>
    [Fact]
    public void The_key_comes_from_the_environment_by_name()
    {
        var options = new FamiliarChatOptions { ApiKeyVariable = "FAMILIAR_TEST_CHAT_KEY" };

        Assert.Equal(
            "a-test-value",
            options.ReadApiKey(name => name == "FAMILIAR_TEST_CHAT_KEY" ? "  a-test-value  " : null));
    }

    [Fact]
    public void A_missing_or_blank_key_reads_as_none()
    {
        var options = new FamiliarChatOptions { ApiKeyVariable = "FAMILIAR_TEST_CHAT_KEY" };

        Assert.Null(options.ReadApiKey(_ => null));
        Assert.Null(options.ReadApiKey(_ => "   "));
        Assert.Null(new FamiliarChatOptions().ReadApiKey(_ => "ignored-because-no-variable-is-named"));
    }

    /// <summary>
    /// Both the provider name and a present key are required. A provider configured without a key
    /// would otherwise produce a stream that dies on every turn — a dead stream where an honest
    /// sentence belongs.
    /// </summary>
    [Theory]
    [InlineData("xai", "a-key", true)]
    [InlineData("xai", null, false)]
    [InlineData("XAI", "a-key", true)]
    [InlineData(null, "a-key", false)]
    [InlineData("typo", "a-key", false)]
    public void Configured_means_a_selected_provider_and_a_present_key(
        string? provider,
        string? key,
        bool expected)
    {
        var options = new FamiliarChatOptions
        {
            Provider = provider,
            ApiKeyVariable = "FAMILIAR_TEST_CHAT_KEY"
        };

        Assert.Equal(expected, options.IsConfigured(_ => key));
    }

    /// <summary>
    /// The model is configuration, never a compile-time constant, because provider rosters churn.
    /// This asserts it can be changed at all — the failure it guards against is somebody hard-coding
    /// an id and leaving the setting inert.
    /// </summary>
    [Fact]
    public void The_model_is_configuration()
    {
        var options = new FamiliarChatOptions { Model = "some-future-model" };

        Assert.Equal("some-future-model", options.Model);
    }
}
