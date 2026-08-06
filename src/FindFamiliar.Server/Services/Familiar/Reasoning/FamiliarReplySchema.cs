using System.Text.Json;

namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// The shape every reasoning provider must return, as a strict JSON schema.
///
/// This is a statement about what <i>this application</i> accepts, not about any provider's API, so
/// it lives here beside the abstraction rather than in an implementation. Anthropic's structured
/// output and Ollama's constrained decoding both take a JSON schema, and both take this one — which
/// is the point: the reply contract is written once, and swapping providers cannot quietly change
/// what a reply is allowed to contain.
///
/// Three fields, <c>additionalProperties: false</c> at every level, and every field listed in
/// <c>required</c>. The contract is closed: a provider cannot introduce a field this application did
/// not define, and an unknown one is a schema violation rather than something to interpret.
///
/// <b>No tool is described here, and none is ever declared.</b> The action below is a described
/// intention that a human must confirm — never an invocation.
/// </summary>
public static class FamiliarReplySchema
{
    /// <summary>The two action kinds, matching <see cref="Domain.FamiliarActionKind"/> exactly.</summary>
    public static readonly IReadOnlyList<string> ActionKinds = ["CreateTask", "StartPlanner"];

    /// <summary>The schema's top-level members, keyed by name — the shape most SDKs take.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Members { get; } =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!;

    /// <summary>The schema as one element, for callers that take a whole document.</summary>
    public static JsonElement Document { get; } = JsonSerializer.Deserialize<JsonElement>(SchemaJson);

    /// <summary>
    /// <c>action</c> and <c>evidence</c> are nullable rather than optional, and both are required.
    /// Nullable-and-required makes the model decide and say so; optional would let it omit a field
    /// and leave this application guessing whether it declined to propose or simply forgot.
    /// </summary>
    public const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "reply": {
              "type": "string",
              "description": "The visible answer, in prose. Never empty."
            },
            "action": {
              "type": ["object", "null"],
              "description": "At most one proposed action, or null when none is proposed.",
              "properties": {
                "kind": {
                  "type": "string",
                  "enum": ["CreateTask", "StartPlanner"],
                  "description": "Which of the two supported actions is proposed."
                },
                "title": {
                  "type": ["string", "null"],
                  "description": "CreateTask only: the title of the task to create."
                },
                "requestedOutcome": {
                  "type": ["string", "null"],
                  "description": "CreateTask only: what the task should achieve."
                },
                "targetTaskId": {
                  "type": ["string", "null"],
                  "description": "StartPlanner only: the id of a task present in the snapshot."
                }
              },
              "required": ["kind", "title", "requestedOutcome", "targetTaskId"],
              "additionalProperties": false
            },
            "evidence": {
              "type": ["array", "null"],
              "description": "Identifiers from the snapshot that this answer rests on.",
              "items": { "type": "string" }
            }
          },
          "required": ["reply", "action", "evidence"],
          "additionalProperties": false
        }
        """;
}
