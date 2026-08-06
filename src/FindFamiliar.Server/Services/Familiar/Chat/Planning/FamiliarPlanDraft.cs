using System.Text.Json;
using System.Text.Json.Serialization;
using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>One item as the drafting model wrote it, before any of it is believed.</summary>
public sealed record DraftedPlanItem(
    string Title,
    string RequestedOutcome,
    AgentSessionRole? Role,
    IReadOnlyList<Guid> EvidenceEntryIds);

/// <summary>A drafted plan that survived parsing and validation. Still proposes nothing.</summary>
public sealed record DraftedPlan(string Summary, IReadOnlyList<DraftedPlanItem> Items);

/// <summary>
/// Reads a plan out of model output, strictly, and refuses everything it cannot vouch for.
///
/// This is the boundary where model text stops being text. Everything past it is written to a
/// database, so the rules here are deliberately harsh: an unparseable reply produces no plan rather
/// than a partial one, an unknown role produces no role rather than a guessed one, and an item citing
/// evidence that was never in the pack loses the citation rather than keeping an invented source.
///
/// The same posture as <c>FamiliarStructuredReply</c> in Sprint 11, and for the same reason. A parser
/// that repairs what it reads is a parser that will one day repair something into meaning nothing was
/// asked for — and here that would mean creating work nobody proposed.
/// </summary>
public static class FamiliarPlanDraftReader
{
    /// <summary>
    /// Parses a drafting reply, or returns null when there is no plan in it.
    ///
    /// Null is an ordinary outcome, not a failure: asked to plan against a project with nothing to do,
    /// the honest answer is no items, and a caller must render that as "nothing to propose" rather
    /// than as an error.
    /// </summary>
    /// <param name="offeredEvidence">
    /// The ids that were actually in this turn's pack. Anything else an item cites is dropped, which
    /// is the same check the rendered transcript applies to prose — a plan is an argument about what
    /// to do next, and an argument built on invented sources is worse than one with none.
    /// </param>
    public static DraftedPlan? Read(string? output, IReadOnlyCollection<Guid> offeredEvidence)
    {
        if (Unfence(output) is not { Length: > 0 } json)
        {
            return null;
        }

        DraftPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<DraftPayload>(json, Options);
        }
        catch (JsonException)
        {
            // No plan at all. A half-read plan is the one outcome worse than none, because the half
            // that parsed would look complete.
            return null;
        }

        if (payload?.Items is not { Count: > 0 } items)
        {
            return null;
        }

        var read = new List<DraftedPlanItem>();

        foreach (var item in items)
        {
            if (Clean(item.Title, FamiliarPlanItem.MaxTitleLength) is not { Length: > 0 } title
                || Clean(item.RequestedOutcome, FamiliarPlanItem.MaxRequestedOutcomeLength)
                    is not { Length: > 0 } outcome)
            {
                // An item without a title or an outcome is not a proposal a person could evaluate.
                // Dropped rather than filled in with a placeholder that would read as intent.
                continue;
            }

            read.Add(new DraftedPlanItem(title, outcome, ReadRole(item.Role), ReadEvidence(item.Evidence, offeredEvidence)));

            if (read.Count == FamiliarPlanProposal.MaxItems)
            {
                // Truncated rather than refused. A plan of nine is not nine times as wrong as a plan
                // of eight, and the cap exists so an approval stays readable.
                break;
            }
        }

        return read.Count == 0
            ? null
            : new DraftedPlan(Clean(payload.Summary, FamiliarPlanProposal.MaxSummaryLength) ?? string.Empty, read);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// The JSON inside a reply, whether or not it arrived wrapped in a Markdown fence.
    ///
    /// Both spellings are accepted for the reason commit 41b35e1 recorded: the same model returns
    /// bare JSON for a short answer and fenced JSON once the prompt reaches full size, and a reader
    /// that handled only one would work in testing and fail in use.
    /// </summary>
    private static string? Unfence(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var text = output.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>
    /// A role, or none. An unrecognised name yields null rather than a default, because defaulting
    /// would silently turn "start something I did not understand" into "start a Planner".
    /// </summary>
    private static AgentSessionRole? ReadRole(string? role) =>
        Enum.TryParse<AgentSessionRole>(role, ignoreCase: true, out var parsed) ? parsed : null;

    private static IReadOnlyList<Guid> ReadEvidence(
        IReadOnlyList<string>? cited,
        IReadOnlyCollection<Guid> offered)
    {
        if (cited is null || cited.Count == 0 || offered.Count == 0)
        {
            return [];
        }

        return cited
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && offered.Contains(id))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// One line of trimmed text, bounded by its column. Control characters go because a title with a
    /// newline in it breaks every list it is ever rendered into.
    /// </summary>
    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value.Where(character => !char.IsControl(character) || character == '\n').ToArray())
            .Replace('\n', ' ')
            .Trim();

        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private sealed record DraftPayload(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("items")] IReadOnlyList<DraftItemPayload>? Items);

    private sealed record DraftItemPayload(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("requestedOutcome")] string? RequestedOutcome,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("evidence")] IReadOnlyList<string>? Evidence);
}
