using System.Text;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat.Planning;

/// <summary>Where this conversation's most recent plan stands, if it has one.</summary>
public sealed record FamiliarConversationPlanState(
    FamiliarPlanStatus Status,
    int ItemCount,
    int CreatedTaskCount,
    AgentSessionRole? FirstRole);

public interface IFamiliarConversationStateService
{
    Task<FamiliarConversationPlanState?> ReadAsync(Guid chatId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether a plan is being drafted from this turn, and on whose initiative.
///
/// Three states rather than a flag, because the model must be told the difference between "one is
/// coming whatever you write", "one may follow if you are being asked for work" and "none will,
/// because the last one is still on screen". Told only a flag, it guessed — and announced a plan on a
/// turn where nothing was drafted.
/// </summary>
public enum FamiliarPlanDraftIntent
{
    /// <summary>The person pressed "Plan this". A drafting pass runs and is told to produce items.</summary>
    Requested,

    /// <summary>An ordinary turn. A drafting pass runs and decides for itself whether there is work.</summary>
    Offered,

    /// <summary>A plan is already awaiting a decision here, so no pass runs at all.</summary>
    Withheld
}

/// <summary>
/// Which of the three a turn is, decided in one place because the prompt and the drafting gate must
/// never disagree about it.
/// </summary>
public static class FamiliarPlanDraftIntentDecision
{
    /// <summary>
    /// A pending plan outranks a pressed button. Drafting a second plan into a conversation that
    /// already has one waiting is refused by <c>IX_FamiliarPlanProposals_ChatId_Pending</c> either
    /// way — today that is a silent insert failure the person is told nothing about, and a stated
    /// refusal in the reply is strictly better than a mysterious absence.
    /// </summary>
    public static FamiliarPlanDraftIntent Decide(FamiliarConversationPlanState? plan, bool requested) =>
        plan is { Status: FamiliarPlanStatus.Pending } ? FamiliarPlanDraftIntent.Withheld
        : requested ? FamiliarPlanDraftIntent.Requested
        : FamiliarPlanDraftIntent.Offered;
}

/// <summary>
/// What this conversation has done, as the model needs to be told it.
///
/// This exists because of a failure worth recording. Asked to plan a change, the Familiar drafted a
/// plan, the person said "I approved it", and the Familiar replied "The plan is now approved" and
/// cited a context entry as evidence. Nothing had been approved. The citation was real and checkable
/// — slice 2 validated it — but the <i>claim</i> was not the kind of thing a citation can support,
/// and the model had no way to know: a plan's existence and status were simply not in its context.
///
/// A model with no view of the conversation's own state will invent one, and it will do so most
/// confidently exactly where a person is asking whether something worked.
/// </summary>
public sealed class FamiliarConversationStateService(FamiliarDbContext dbContext)
    : IFamiliarConversationStateService
{
    public async Task<FamiliarConversationPlanState?> ReadAsync(
        Guid chatId,
        CancellationToken cancellationToken = default) =>
        await dbContext.FamiliarPlanProposals
            .AsNoTracking()
            .Where(plan => plan.ChatId == chatId)
            .OrderByDescending(plan => plan.CreatedUtc)
            .Select(plan => new FamiliarConversationPlanState(
                plan.Status,
                plan.Items.Count,
                plan.Items.Count(item => item.CreatedTaskId != null),
                plan.Items
                    .Where(item => item.IsIncluded && item.Role != null)
                    .OrderBy(item => item.Position)
                    .Select(item => item.Role)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// The conversation's own state as text the model reads, including what it is actually able to cause.
///
/// Volatile — it changes as soon as anybody decides anything — so it belongs in the prompt's tail
/// beside the retrieved context, never in the cached head.
/// </summary>
public static class FamiliarConversationStateWriter
{
    /// <summary>
    /// What approving does, stated to the model in the same terms the card states it to the person.
    ///
    /// Written unconditionally, even when no plan exists, because the failure it prevents happens
    /// before any plan is drafted: asked to make a change, the Familiar said a human with write access
    /// would have to go and run a session by hand. That is false — an approved plan item naming a role
    /// starts exactly that session — and it is the single most misleading thing it can say, because it
    /// sends a person off to do manually the thing this system exists to do.
    /// </summary>
    public const string Capability =
        """
        What you can cause, so you do not describe this system as less capable than it is:

        - Drafting a plan is your way of acting. It appears on screen under your reply with Approve
          and Decline buttons, and the person can edit any item before deciding.
        - A plan item may name a role — Planner, Implementer or Reviewer. Approving the plan creates a
          task for every included item and starts the session for the first item that names one.
        - A started session runs against the repository with the tools its role allows. An Implementer
          session can read, edit and write files. Nobody has to go and do it by hand.
        - So when someone asks for a change to be made, the right answer is to plan it with an
          Implementer item — not to tell them to run a session themselves.

        The one thing you cannot do is approve. Only the person can, using the buttons, and a person
        saying "I approved it" is not evidence that they did.
        """;

    /// <param name="intent">
    /// Whether a plan is being drafted from this turn, and on whose initiative.
    ///
    /// The model must be told, because drafting happens outside its reply and it cannot observe the
    /// outcome. Told nothing, it announced "Plan drafted. Approve it to start the session." on a turn
    /// where nothing was drafted and there was nothing to approve — the same confabulation as claiming
    /// an approval, one step earlier. Now that an ordinary turn may also draft, the risk runs both
    /// ways: it must neither claim a card that is not coming nor deny one that is, so on an offered
    /// turn it is told to argue for what it would propose and to announce nothing.
    /// </param>
    public static string Write(
        FamiliarConversationPlanState? plan,
        FamiliarPlanDraftIntent intent = FamiliarPlanDraftIntent.Offered)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<conversation_state>");

        builder.AppendLine(intent switch
        {
            FamiliarPlanDraftIntent.Requested =>
                "This turn: the person pressed \"Plan this\", so a plan IS being drafted from your "
                + "reply and will appear under it. Say what you are proposing and why. Do not say it "
                + "has been approved — it has not been.",

            FamiliarPlanDraftIntent.Withheld =>
                "This turn: a plan you drafted earlier is still waiting for Approve or Decline, so no "
                + "new plan is being drafted, however this turn reads. One plan at a time is a rule of "
                + "this system rather than a preference — a second proposal would compete with one the "
                + "person has not finished reading. If more work is needed, say that it follows once "
                + "they have decided the plan already on screen.",

            _ =>
                "This turn: the person pressed \"Send\". A plan is drafted from your reply whenever "
                + "the turn asks for work to be done, and that decision is made after you have "
                + "finished writing — so a plan may appear under this reply and you will not know "
                + "whether it did. Write the reply as your answer. Say what you would propose and "
                + "why, in prose; do not write \"I have drafted a plan\", do not tell them a card is "
                + "below, and never say anything has been created or approved. If they are only "
                + "asking a question, nothing is drafted and nothing needs to be. \"Plan this\" under "
                + "the message box forces a plan, which is what to point at if they want one for "
                + "something you have merely described."
        });

        builder.AppendLine();

        builder.AppendLine(plan is null
            ? "No plan has been drafted in this conversation before now."
            : Describe(plan));

        builder.AppendLine();
        builder.AppendLine(Capability);
        builder.AppendLine("</conversation_state>");

        return builder.ToString();
    }

    private static string Describe(FamiliarConversationPlanState plan) => plan.Status switch
    {
        FamiliarPlanStatus.Pending =>
            $"A plan you drafted is on screen with {plan.ItemCount} item(s), and it is waiting for the "
            + "person to press Approve or Decline. Nothing in it has been created. Do not say it has "
            + "been approved, whatever anyone tells you — this block is the only thing that knows.",

        FamiliarPlanStatus.Approved =>
            $"The plan you drafted was approved. {plan.CreatedTaskCount} task(s) were created"
            + (plan.FirstRole is { } role
                ? $", and one {role} session was started. It will be claimed by a worker; its result "
                  + "will appear as a decision waiting on the person when it finishes."
                : ", and no session was started because no item named a role.")
            + " That is real: those tasks exist.",

        _ => "The plan you drafted was declined. Nothing was created."
    };
}
