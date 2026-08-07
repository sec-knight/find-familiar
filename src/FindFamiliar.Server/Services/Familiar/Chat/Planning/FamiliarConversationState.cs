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

        - When you draft a plan, it appears on screen with Approve and Decline buttons under it.
        - A plan item may name a role — Planner, Implementer or Reviewer. Approving the plan creates a
          task for every included item and starts the session for the first item that names one.
        - A started session runs against the repository with the tools its role allows. An Implementer
          session can read, edit and write files. Nobody has to go and do it by hand.
        - So when someone asks for a change to be made, the right answer is to plan it with an
          Implementer item — not to tell them to run a session themselves.

        What you cannot do: approve anything. Only the person can, using the buttons. A person saying
        "I approved it" is not evidence that they did.
        """;

    /// <param name="planRequestedThisTurn">
    /// True when the person pressed "Plan this" rather than "Send".
    ///
    /// The model must be told which, because drafting happens outside its reply: on a plan turn a plan
    /// is being drafted whatever it says, and on an ordinary turn no amount of prose produces one. Told
    /// neither, it announced "Plan drafted. Approve it to start the session." on a turn where nothing
    /// was drafted and there was nothing to approve — the same confabulation as claiming an approval,
    /// one step earlier.
    /// </param>
    public static string Write(FamiliarConversationPlanState? plan, bool planRequestedThisTurn = false)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<conversation_state>");

        builder.AppendLine(planRequestedThisTurn
            ? "This turn: the person pressed \"Plan this\", so a plan IS being drafted from your reply "
              + "and will appear under it. Say what you are proposing and why. Do not say it has been "
              + "approved — it has not been."
            : "This turn: the person pressed \"Send\", so NO plan is being drafted. Nothing you write "
              + "creates one. If they want a plan, say plainly that they should press \"Plan this\" "
              + "under the message box. Never announce a plan you have not been asked to draft.");

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
