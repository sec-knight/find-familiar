using System.Text.Json;
using System.Text.Json.Serialization;

namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>A reply as <see cref="FamiliarReplySchema"/> describes it.</summary>
public sealed record FamiliarReplyPayload(
    [property: JsonPropertyName("reply")] string? Reply,
    [property: JsonPropertyName("action")] FamiliarActionPayload? Action,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string>? Evidence);

public sealed record FamiliarActionPayload(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("requestedOutcome")] string? RequestedOutcome,
    [property: JsonPropertyName("targetTaskId")] string? TargetTaskId);

/// <summary>
/// Reads a provider's structured output into this application's own types.
///
/// Every provider that speaks <see cref="FamiliarReplySchema"/> parses through here, so the rules
/// about what a reply may contain are written once rather than per implementation.
///
/// Defensive by construction. A schema enforced elsewhere is not a guarantee this code may assume:
/// the JSON is parsed rather than trusted, a blank reply is a failure rather than an empty bubble, an
/// unparseable task id is dropped rather than carried as text, and an unrecognised kind produces no
/// draft. Nothing unknown is ever executed — the worst a malformed payload can do is yield no action.
///
/// What this does <b>not</b> do: decide whether an action is allowed, or check an identifier against
/// anything. Both need the snapshot that produced the reply, and both belong to the conversation
/// service and <c>ProposedActionValidator</c>. This layer only turns text into values.
/// </summary>
public static class FamiliarStructuredReply
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    /// <summary>
    /// The payload, or null when the reply cannot be used.
    ///
    /// Null means <see cref="FamiliarReasoningStatus.Malformed"/> to the caller. There is no partial
    /// success: a payload this application cannot read is not one it should half-believe.
    /// </summary>
    public static FamiliarReplyPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        FamiliarReplyPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<FamiliarReplyPayload>(json, Options);
        }
        catch (JsonException)
        {
            // A JsonException's message can quote the offending document, so it is not carried.
            return null;
        }

        return string.IsNullOrWhiteSpace(payload?.Reply) ? null : payload;
    }

    /// <summary>
    /// At most one draft, and only when its kind is one this application published. The conversation
    /// service validates it again against the snapshot before any proposal row exists.
    /// </summary>
    public static IReadOnlyList<ProposedActionDraft> Drafts(FamiliarReplyPayload payload)
    {
        if (payload.Action is not { } action || string.IsNullOrWhiteSpace(action.Kind))
        {
            return [];
        }

        if (!FamiliarReplySchema.ActionKinds.Contains(action.Kind, StringComparer.Ordinal))
        {
            return [];
        }

        // An unparseable id becomes null rather than travelling as text. Only a Guid present in the
        // snapshot is ever accepted, so a malformed one could never validate — dropping it here keeps
        // the provider-neutral type honest about what it holds.
        Guid? targetTaskId = Guid.TryParse(action.TargetTaskId, out var parsed) ? parsed : null;

        return [new ProposedActionDraft(action.Kind, action.Title, action.RequestedOutcome, targetTaskId)];
    }

    /// <summary>
    /// The cited identifiers that parse as Guids, de-duplicated. Anything else is dropped without
    /// comment — a citation this application cannot resolve is not worth reporting to a user.
    /// </summary>
    public static IReadOnlyList<Guid> EvidenceIds(FamiliarReplyPayload payload)
    {
        if (payload.Evidence is not { Count: > 0 } evidence)
        {
            return [];
        }

        return evidence
            .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }
}
