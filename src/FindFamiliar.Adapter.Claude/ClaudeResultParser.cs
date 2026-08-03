using System.Text.Json;
using System.Text.Json.Serialization;
using FindFamiliar.Runner;

namespace FindFamiliar.Adapter.Claude;

public enum ClaudeResultOutcome
{
    Valid,
    Malformed,
    ErrorEnvelope,
    BlankResult,
    PermissionDenied
}

/// <summary>
/// The subset of Claude's <c>--output-format json</c> envelope this adapter depends on. Every
/// other property in the envelope (timings, cost, usage, session identifiers) is deliberately
/// ignored so a provider-side addition cannot change adapter behavior.
/// </summary>
public sealed record ClaudeEnvelope(
    [property: JsonPropertyName("is_error")] bool IsError,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("permission_denials")] JsonElement? PermissionDenials);

public static class ClaudeResultParser
{
    private const string TruncationMarker = "\n\n[truncated by adapter]";

    /// <summary>Fixed artifact title — Claude's envelope carries no title field and the adapter must not invent provenance.</summary>
    public const string ArtifactTitle = "Claude Code session result";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ClaudeResultOutcome TryParse(string stdout, out AdapterResult? result)
    {
        result = null;

        ClaudeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ClaudeEnvelope>(stdout, JsonOptions);
        }
        catch (JsonException)
        {
            return ClaudeResultOutcome.Malformed;
        }

        if (envelope is null)
        {
            return ClaudeResultOutcome.Malformed;
        }

        if (envelope.IsError)
        {
            return ClaudeResultOutcome.ErrorEnvelope;
        }

        // A non-empty denial list means the model attempted a tool the policy blocked. With an
        // empty --tools schema that should be impossible, so treat it as a policy failure rather
        // than quietly returning a partial answer.
        if (envelope.PermissionDenials is { ValueKind: JsonValueKind.Array } denials && denials.GetArrayLength() > 0)
        {
            return ClaudeResultOutcome.PermissionDenied;
        }

        if (string.IsNullOrWhiteSpace(envelope.Result))
        {
            return ClaudeResultOutcome.BlankResult;
        }

        var text = envelope.Result;

        result = new AdapterResult(
            RunnerProtocol.ContractVersion,
            Truncate(text, RunnerProtocol.MaxLongFieldLength),
            Truncate(text, RunnerProtocol.MaxSummaryLength),
            ArtifactTitle,
            Truncate(text, RunnerProtocol.MaxLongFieldLength));

        return ClaudeResultOutcome.Valid;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength - TruncationMarker.Length), TruncationMarker);
    }
}
