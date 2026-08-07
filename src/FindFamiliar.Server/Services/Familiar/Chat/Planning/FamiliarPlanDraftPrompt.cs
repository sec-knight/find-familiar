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

        - **The person's request is the specification.** Plan what they asked for. The records are
          supporting material — they tell you how this system works and what has already happened, and
          they never override the request. If the records seem to say the request is impossible, plan
          it anyway and say so in the summary: records go stale, and a defect fixed last week still
          reads like a live constraint.
        - **When someone asks for a change to be made, plan an item that makes it.** Give that item
          the role that does the work — usually Implementer — so approving it starts the session that
          does it. A plan that only records notes about a request is not a plan for that request.
        - "role" is the session that starts for that item, or null to create the task without starting
          anything. Use null when the work genuinely needs a person first, not as a default: a plan
          where nothing starts leaves the person exactly where they were before they asked.
        - At most 8 items, and fewer is better. Every item is work a person will be asked to approve,
          and a plan nobody can hold in their head is a plan nobody reads before approving.
        - "evidence" may only contain ids from the recorded context you were given above. An id you
          were not shown is dropped, and an item that cites nothing is better than one citing
          something invented. Do not cite an entry as support for something it does not say.
        - Do not invent projects, tasks, files or decisions, and do not restate work the records show
          as already done.
        - Return {"summary": "...", "items": []} only when there is genuinely nothing to do — not
          because the records left you unsure. Say what you are unsure about in the summary instead.
        """;

    /// <summary>
    /// Appended when the person pressed "Plan this". They have already declared what they want, so the
    /// judgement below is not theirs to make again.
    /// </summary>
    public const string Requested =
        """
        The person pressed "Plan this". They have asked for a plan, so draft one. Return no items only
        if the exchange genuinely names no work at all — not because the records left you unsure.
        """;

    /// <summary>
    /// Appended on an ordinary turn, where this pass runs uninvited and its first job is to decide
    /// whether it should have.
    ///
    /// This paragraph is the only guard against a plan card appearing on a turn that was a question,
    /// and it is prose, which is worth being honest about. What makes it safe enough is the gate
    /// around it rather than the wording inside it: a conversation holds at most one pending plan, so
    /// the worst this can produce is a single stray card with a Decline button under it.
    /// </summary>
    public const string Offered =
        """
        Nobody asked for a plan. This pass runs on every turn, so your first decision is whether this
        exchange is asking for work to be done at all.

        Return {"summary": "why there is nothing to propose", "items": []} unless the person is asking
        for something to be changed, built, fixed, investigated or produced. A question about what is
        recorded, a request to explain or summarise, a correction, an argument about how something
        should work, thanks and small talk are all answered with no items. A plan is a card a person
        has to read and dismiss, so the bar is a request for work — not a subject that could one day
        become work.

        Where they are asking for something to be done, draft it exactly as you would if they had
        pressed the button.
        """;

    /// <summary>
    /// The prompt for a pass started this way. The judgement goes last, after the rules it operates
    /// on, which is where a model weights it most.
    /// </summary>
    public static string For(FamiliarPlanDraftIntent intent) =>
        Text + "\n\n" + (intent == FamiliarPlanDraftIntent.Requested ? Requested : Offered);
}
