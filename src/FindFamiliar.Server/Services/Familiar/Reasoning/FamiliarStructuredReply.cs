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
            payload = JsonSerializer.Deserialize<FamiliarReplyPayload>(Unfence(json), Options);
        }
        catch (JsonException)
        {
            // A JsonException's message can quote the offending document, so it is not carried.
            return null;
        }

        return string.IsNullOrWhiteSpace(payload?.Reply) ? null : payload;
    }

    /// <summary>
    /// Strips a Markdown code fence around the payload, if one is present.
    ///
    /// Models routinely wrap JSON in <c>```json … ```</c> even when a schema was requested, and the
    /// habit gets stronger as the prompt gets longer — observed against a real endpoint that returned
    /// bare JSON for a short snapshot and fenced JSON for a full one. Only endpoints doing true
    /// constrained decoding never do it, and this application deliberately supports ones that do not.
    ///
    /// This normalises a known wrapper; it does not loosen anything. What is inside the fence is
    /// still parsed strictly and validated against the same rules, so a fenced payload that is not a
    /// valid reply is still rejected. Refusing to read one would discard a correct answer over its
    /// packaging.
    /// </summary>
    private static string Unfence(string json)
    {
        var trimmed = json.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Drop the opening fence and its optional language tag ("```json"), which occupy the first
        // line, then the closing fence at the end if it is there.
        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstBreak + 1)..].TrimEnd();

        if (body.EndsWith("```", StringComparison.Ordinal))
        {
            body = body[..^3].TrimEnd();
        }

        return body;
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
