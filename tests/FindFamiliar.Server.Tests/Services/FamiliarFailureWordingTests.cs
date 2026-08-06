using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Familiar.Reasoning;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The wording a person reads when a reasoning provider does not answer.
///
/// This asserts the copy itself, in the shape <see cref="DecisionOutcomeWordingTests"/> established:
/// the sentences are a deliverable, not an implementation detail, and a silent edit to one of them
/// changes what the application claims about its own failures.
/// </summary>
public sealed class FamiliarFailureWordingTests
{
    private const int Timeout = 60;

    /// <summary>
    /// Every failure status has wording. A status added without a sentence must fail here rather than
    /// fall through to something generic at runtime.
    /// </summary>
    [Fact]
    public void Every_failure_status_has_wording()
    {
        var failures = Enum.GetValues<FamiliarReasoningStatus>()
            .Where(status => status != FamiliarReasoningStatus.Answered)
            .ToList();

        Assert.NotEmpty(failures);

        foreach (var status in failures)
        {
            var note = FamiliarFailureWording.For(status, providerIsUnconfigured: false, Timeout);

            Assert.False(string.IsNullOrWhiteSpace(note.Code));
            Assert.False(string.IsNullOrWhiteSpace(note.Sentence));
        }
    }

    /// <summary>Answered is not a failure, and asking for its wording is a programming error.</summary>
    [Fact]
    public void Answered_has_no_wording()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FamiliarFailureWording.For(FamiliarReasoningStatus.Answered, false, Timeout));
    }

    /// <summary>
    /// The verbatim table from user-experience.md §3. These are the sentences the reviewer checks
    /// this file against.
    /// </summary>
    [Theory]
    [InlineData(FamiliarReasoningStatus.Unavailable, true, "provider-not-configured",
        "No reasoning provider is configured, so I can only show you what is recorded. The summary above is complete.")]
    [InlineData(FamiliarReasoningStatus.Unavailable, false, "provider-unavailable",
        "The reasoning provider could not be reached. Your message was saved and nothing was changed.")]
    [InlineData(FamiliarReasoningStatus.Unauthenticated, false, "provider-unauthenticated",
        "The reasoning provider rejected this application's credentials. That is a server configuration problem, not something you did.")]
    [InlineData(FamiliarReasoningStatus.RateLimited, false, "provider-rate-limited",
        "The reasoning provider is rate limiting this application right now. Your message was saved — try again shortly.")]
    [InlineData(FamiliarReasoningStatus.Malformed, false, "provider-response-unusable",
        "The reasoning provider returned a response this application could not use. Nothing was changed.")]
    [InlineData(FamiliarReasoningStatus.Declined, false, "provider-declined",
        "The reasoning provider declined to answer that message.")]
    public void Wording_matches_the_authored_copy(
        FamiliarReasoningStatus status,
        bool unconfigured,
        string expectedCode,
        string expectedSentence)
    {
        var note = FamiliarFailureWording.For(status, unconfigured, Timeout);

        Assert.Equal(expectedCode, note.Code);
        Assert.Equal(expectedSentence, note.Sentence);
    }

    [Fact]
    public void Timeout_wording_states_this_applications_own_bound()
    {
        var note = FamiliarFailureWording.For(FamiliarReasoningStatus.TimedOut, false, 45);

        Assert.Equal("provider-timeout", note.Code);
        Assert.Equal(
            "The reasoning provider did not answer within 45 seconds. Your message was saved — try again.",
            note.Sentence);
    }

    [Fact]
    public void Too_large_wording_matches_the_authored_copy()
    {
        var note = FamiliarFailureWording.TooLarge();

        Assert.Equal("snapshot-too-large", note.Code);
        Assert.Equal(
            "This project is larger than I can summarise for the reasoning provider safely, so I did not send it. The summary above is complete and accurate.",
            note.Sentence);
    }

    /// <summary>
    /// "No provider is configured" and "the provider could not be reached" are different facts about
    /// the server. Telling somebody the second when the first is true sends them looking for an
    /// outage that does not exist.
    /// </summary>
    [Fact]
    public void An_unconfigured_provider_is_distinguished_from_an_unreachable_one()
    {
        var unconfigured = FamiliarFailureWording.For(FamiliarReasoningStatus.Unavailable, true, Timeout);
        var unreachable = FamiliarFailureWording.For(FamiliarReasoningStatus.Unavailable, false, Timeout);

        Assert.NotEqual(unconfigured.Code, unreachable.Code);
        Assert.NotEqual(unconfigured.Sentence, unreachable.Sentence);
    }

    /// <summary>
    /// No sentence names a host, a path, a header, an exception type or a key — the property that
    /// makes it safe to render provider failures at all.
    /// </summary>
    [Fact]
    public void No_wording_names_a_host_path_or_exception()
    {
        var sentences = Enum.GetValues<FamiliarReasoningStatus>()
            .Where(status => status != FamiliarReasoningStatus.Answered)
            .SelectMany(status => new[]
            {
                FamiliarFailureWording.For(status, true, Timeout).Sentence,
                FamiliarFailureWording.For(status, false, Timeout).Sentence
            })
            .Append(FamiliarFailureWording.TooLarge().Sentence)
            .ToList();

        string[] forbidden =
        [
            "http://", "https://", "Exception", "StackTrace", "at FindFamiliar",
            "C:\\", "/home/", "/srv/", "api-key", "Bearer", "sk-", "localhost", "127.0.0.1"
        ];

        foreach (var sentence in sentences)
        {
            foreach (var fragment in forbidden)
            {
                Assert.DoesNotContain(fragment, sentence, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// No failure sentence claims a competing actor. Race wording is reserved for outcomes that
    /// establish a real competitor, exactly as ADR-0011 recorded for decision outcomes.
    /// </summary>
    [Fact]
    public void No_wording_claims_somebody_else_acted()
    {
        var sentences = Enum.GetValues<FamiliarReasoningStatus>()
            .Where(status => status != FamiliarReasoningStatus.Answered)
            .Select(status => FamiliarFailureWording.For(status, false, Timeout).Sentence)
            .Append(FamiliarFailureWording.TooLarge().Sentence);

        foreach (var sentence in sentences)
        {
            Assert.DoesNotContain("someone else", sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("already", sentence, StringComparison.OrdinalIgnoreCase);
        }
    }
}
