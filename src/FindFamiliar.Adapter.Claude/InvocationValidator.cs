using System.Text.Json;
using FindFamiliar.Runner;

namespace FindFamiliar.Adapter.Claude;

public enum InvocationParseOutcome
{
    Valid,
    Empty,
    Oversized,
    Malformed,
    MultipleDocuments,
    UnsupportedContractVersion,
    MissingFields,
    AssignmentTooLong
}

/// <summary>
/// Reads exactly one bounded protocol-v1 adapter invocation. Every field is validated before any
/// Claude process is launched; nothing here can select an executable, worktree, or permission mode.
/// </summary>
public static class InvocationValidator
{
    /// <summary>Upper bound on the stdin document, sized for the largest legal assignment plus protocol overhead.</summary>
    public const int MaxStdinBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static InvocationParseOutcome TryParse(string stdin, out AdapterInvocation? invocation)
    {
        invocation = null;

        if (string.IsNullOrWhiteSpace(stdin))
        {
            return InvocationParseOutcome.Empty;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(stdin) > MaxStdinBytes)
        {
            return InvocationParseOutcome.Oversized;
        }

        AdapterInvocation? parsed;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(stdin);

            // Scan for a second concatenated document before JsonDocument.Parse, which rejects
            // trailing content as plain malformed JSON and would lose the distinct category.
            var reader = new System.Text.Json.Utf8JsonReader(bytes);
            reader.Read();
            reader.Skip();
            for (var i = checked((int)reader.BytesConsumed); i < bytes.Length; i++)
            {
                if (bytes[i] is not (0x20 or 0x09 or 0x0A or 0x0D))
                {
                    return InvocationParseOutcome.MultipleDocuments;
                }
            }

            using var document = JsonDocument.Parse(bytes);
            parsed = document.Deserialize<AdapterInvocation>(JsonOptions);
        }
        catch (JsonException)
        {
            return InvocationParseOutcome.Malformed;
        }

        if (parsed is null)
        {
            return InvocationParseOutcome.Malformed;
        }

        if (parsed.ContractVersion != RunnerProtocol.ContractVersion)
        {
            return InvocationParseOutcome.UnsupportedContractVersion;
        }

        if (parsed.TaskId == Guid.Empty
            || parsed.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(parsed.Role)
            || string.IsNullOrWhiteSpace(parsed.RolePrompt)
            || string.IsNullOrWhiteSpace(parsed.AssignmentMarkdown))
        {
            return InvocationParseOutcome.MissingFields;
        }

        if (parsed.AssignmentMarkdown.Length > RunnerProtocol.MaxAssignmentMarkdownLength)
        {
            return InvocationParseOutcome.AssignmentTooLong;
        }

        invocation = parsed;
        return InvocationParseOutcome.Valid;
    }
}
