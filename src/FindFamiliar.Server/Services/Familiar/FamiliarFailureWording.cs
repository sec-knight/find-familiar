using FindFamiliar.Server.Services.Familiar.Reasoning;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// A fixed failure code and the exact sentence a person reads for it.
///
/// <see cref="Code"/> is written by this codebase and stored on the message; <see cref="Sentence"/>
/// is composed here and rendered. Neither is ever derived from provider text.
/// </summary>
public sealed record FamiliarFailureNote(string Code, string Sentence);

/// <summary>
/// Everything the page says when a reasoning provider does not answer.
///
/// This exists so that provider text has no route to a person. A provider's <c>Detail</c> may name a
/// host, a path, an exception type or a fragment of a credential; none of it is persisted and none of
/// it is rendered. The user reads one of the sentences below and nothing else, and the codes are what
/// go in the database.
///
/// The wording is verbatim from user-experience.md §3, which is the authored copy the reviewer checks
/// this file against — <c>DecisionOutcomeWordingTests</c> established that this codebase asserts its
/// own copy rather than trusting it to stay put.
///
/// Two rules the sentences follow. None of them names a host, a path, a header, an exception type or
/// a key. And none of them speculates about a cause the server did not observe: "could not be
/// reached" is what a failed connection establishes, and "the provider is down" is not.
/// </summary>
public static class FamiliarFailureWording
{
    public const string NotConfigured = "provider-not-configured";
    public const string Unavailable = "provider-unavailable";
    public const string Unauthenticated = "provider-unauthenticated";
    public const string TimedOut = "provider-timeout";
    public const string RateLimited = "provider-rate-limited";
    public const string ResponseUnusable = "provider-response-unusable";
    public const string Declined = "provider-declined";
    public const string SnapshotTooLarge = "snapshot-too-large";

    /// <summary>
    /// The note for a failed reasoning outcome.
    ///
    /// <paramref name="providerIsUnconfigured"/> splits <see cref="FamiliarReasoningStatus.Unavailable"/>
    /// into the two different facts it covers: nothing is configured, or something is configured and
    /// could not be reached. Telling a user the second when the first is true sends them looking for
    /// an outage that does not exist.
    ///
    /// <paramref name="timeoutSeconds"/> is this application's own configured bound, not a duration
    /// reported by the provider.
    /// </summary>
    public static FamiliarFailureNote For(
        FamiliarReasoningStatus status,
        bool providerIsUnconfigured,
        int timeoutSeconds) => status switch
    {
        FamiliarReasoningStatus.Unavailable when providerIsUnconfigured => new(
            NotConfigured,
            "No reasoning provider is configured, so I can only show you what is recorded. The summary above is complete."),

        FamiliarReasoningStatus.Unavailable => new(
            Unavailable,
            "The reasoning provider could not be reached. Your message was saved and nothing was changed."),

        FamiliarReasoningStatus.Unauthenticated => new(
            Unauthenticated,
            "The reasoning provider rejected this application's credentials. That is a server configuration problem, not something you did."),

        FamiliarReasoningStatus.TimedOut => new(
            TimedOut,
            $"The reasoning provider did not answer within {timeoutSeconds} seconds. Your message was saved — try again."),

        FamiliarReasoningStatus.RateLimited => new(
            RateLimited,
            "The reasoning provider is rate limiting this application right now. Your message was saved — try again shortly."),

        FamiliarReasoningStatus.Malformed => new(
            ResponseUnusable,
            "The reasoning provider returned a response this application could not use. Nothing was changed."),

        FamiliarReasoningStatus.Declined => new(
            Declined,
            "The reasoning provider declined to answer that message."),

        // Answered is not a failure, and there is no sentence for it. Reaching here means a caller
        // asked for the wording of a success, which is a programming error rather than a state a
        // user should ever be shown a string about.
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Only a failed reasoning status has wording.")
    };

    /// <summary>
    /// What the page says when the project could not be summarised small enough to send.
    ///
    /// This covers both bounds that can bite: a snapshot over
    /// <see cref="ProjectSnapshot.MaxSnapshotCharacters"/> after every documented reduction, and a
    /// complete request envelope over <see cref="FamiliarRequestEnvelope.MaxEnvelopeCharacters"/>
    /// with no history left to drop. From the person's side they are the same fact — the project did
    /// not fit and nothing was sent — and the deterministic summary above is still complete.
    /// </summary>
    public static FamiliarFailureNote TooLarge() => new(
        SnapshotTooLarge,
        "This project is larger than I can summarise for the reasoning provider safely, so I did not send it. The summary above is complete and accurate.");
}
