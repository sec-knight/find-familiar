namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>
/// What the model is told when it is asked to turn a conversation into a plan.
///
/// A separate, non-streamed call rather than a structured tail on the conversational reply. The
/// alternative — asking for prose and JSON in one response — would stream raw JSON onto the page as
/// it arrived, and suppressing it would mean the sink deciding mid-stream which characters a person
/// is allowed to see. One extra call, on the one kind of turn a person explicitly asked to pay for,
/// buys a clean transcript and a parser that reads one thing.
///
/// The rules below are the ones that matter when output becomes rows in a database rather than words
/// on a page: no invented evidence, no invented tasks, and a plan small enough that approving it is
/// an act of reading rather than of trust.
/// </summary>
public static class FamiliarPlanDraftPrompt
{
    public const string Text =
        """
        You are drafting a plan of work for a software project, from records you have been shown.

        Reply with JSON and nothing else. No prose before it, no explanation after it:

        {"summary": "one or two sentences on what this plan is for",
         "items": [
           {"title": "short imperative title",
            "requestedOutcome": "what must be true when this is done, in a sentence or two",
            "role": "Planner" | "Implementer" | "Reviewer" | null,
            "evidence": ["<context entry id you were shown>"]}
         ]}

        Rules:

        - At most 8 items, and fewer is better. Every item is work a person will be asked to approve,
          and a plan nobody can hold in their head is a plan nobody reads before approving.
        - "role" is the session that should start first for that item, or null to record the work
          without starting anything. Prefer null when the next step is unclear — recording work is
          cheap and reversible, starting a session is neither.
        - "evidence" may only contain ids from the recorded context you were given above. An id you
          were not shown is dropped, and an item that cites nothing is better than one citing
          something invented.
        - Propose work that follows from the records. Do not invent projects, tasks, files or
          decisions, and do not restate work the records show as already done.
        - If the records do not support a plan, return {"summary": "...", "items": []} and say why in
          the summary. Nothing to propose is a real answer.
        """;
}
