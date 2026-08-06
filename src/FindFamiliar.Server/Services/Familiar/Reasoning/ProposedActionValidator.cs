using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services.Familiar.Reasoning;

/// <summary>
/// A draft that survived validation: the parameters a proposal row will carry.
///
/// Every field here was either checked against the originating snapshot or is a length-bounded
/// string a human will see and may edit before confirming. Nothing reaches this type unexamined.
/// </summary>
public sealed record ValidatedAction(
    FamiliarActionKind Kind,
    string? Title,
    string? RequestedOutcome,
    Guid? TargetTaskId);

/// <summary>
/// Turns at most one provider-authored draft into at most one proposal, or into nothing.
///
/// The trust table this implements is architecture.md §5, and the shape of it matters more than any
/// individual rule: validation is total and silent. Every draft either becomes a
/// <see cref="ValidatedAction"/> whose every field has been checked, or it becomes null. There is no
/// third result, no partially-trusted draft, and no error surface — <b>a rejected draft is not an
/// error</b>. The reply is still shown; the person simply gets no button.
///
/// Reporting "the model proposed something invalid" was considered and rejected. It would teach
/// people to read model intent as system state, which is the exact confusion this whole feature is
/// built to avoid. A proposal that does not validate never existed as far as the page is concerned.
///
/// This is not the security boundary. <see cref="FamiliarActionService"/> re-validates every gate
/// inside the confirming transaction, because a proposal is a record of what a human was shown and
/// the world may have moved since. This is the earlier, cheaper filter that keeps unusable drafts
/// out of the database at all.
/// </summary>
public static class ProposedActionValidator
{
    /// <summary>
    /// At most one proposal per reply, which the filtered unique index
    /// <c>IX_FamiliarActionProposals_ConversationId_Pending</c> enforces at the schema level too.
    /// One pending row per conversation is what makes concurrent confirmation trivially safe:
    /// contenders can only ever race for the same row.
    /// </summary>
    public const int MaxActionsPerReply = 1;

    /// <summary>
    /// Validates the single draft a reply may carry against the exact snapshot that produced it.
    ///
    /// More than one draft yields nothing at all rather than the first: a provider that proposed two
    /// actions did not understand the contract, and silently picking one would be this application
    /// choosing an action on a model's behalf.
    /// </summary>
    public static ValidatedAction? Validate(
        IReadOnlyList<ProposedActionDraft> drafts,
        ProjectSnapshot snapshot)
    {
        if (drafts.Count is 0 or > MaxActionsPerReply)
        {
            return null;
        }

        var draft = drafts[0];

        // A closed two-member enum, matched exactly. An unrecognised kind produces no proposal, so
        // model text can never select executable behaviour this application did not define — and
        // there is deliberately no Unknown member for one to be stored as.
        //
        // Exact string equality rather than Enum.TryParse, which accepts surrounding whitespace,
        // case variants under ignoreCase, comma-separated flag lists, and bare numeric values —
        // "0" parses to CreateTask. None of that leniency is wanted when the input is model output:
        // the kind is either the name this application published or it is nothing.
        var kind = Enum.GetValues<FamiliarActionKind>()
            .Cast<FamiliarActionKind?>()
            .FirstOrDefault(candidate => string.Equals(
                candidate!.Value.ToString(), draft.Kind, StringComparison.Ordinal));

        if (kind is null)
        {
            return null;
        }

        return kind.Value switch
        {
            FamiliarActionKind.CreateTask => ValidateCreateTask(draft),
            FamiliarActionKind.StartPlanner => ValidateStartPlanner(draft, snapshot),
            _ => null
        };
    }

    /// <summary>
    /// Title and requested outcome, both required and both bounded.
    ///
    /// These are the two fields a human may edit before confirming, so they are validated again at
    /// confirmation time against the same rules — what is checked here is a draft, and what is
    /// created there is whatever the person actually approved.
    /// </summary>
    private static ValidatedAction? ValidateCreateTask(ProposedActionDraft draft)
    {
        var title = draft.Title?.Trim();
        var requestedOutcome = draft.RequestedOutcome?.Trim();

        if (!IsWithinBounds(title, FamiliarActionProposal.MaxTitleLength)
            || !IsWithinBounds(requestedOutcome, FamiliarActionProposal.MaxRequestedOutcomeLength))
        {
            return null;
        }

        // A task id on a CreateTask draft is meaningless, and carrying it would leave a column set
        // that the confirmation path does not read. Dropped rather than rejected.
        return new ValidatedAction(FamiliarActionKind.CreateTask, title, requestedOutcome, null);
    }

    /// <summary>
    /// The target must be a task the provider was actually shown.
    ///
    /// Presence in the snapshot is the whole check, and it is stronger than "belongs to this
    /// project": the snapshot is built per project and filtered on it, so an id found there is by
    /// construction this project's. A free-text task id has no path in — the id must resolve against
    /// the exact snapshot that produced the reply, so a provider cannot name a task it inferred, a
    /// task from another project, or a task that does not exist.
    /// </summary>
    private static ValidatedAction? ValidateStartPlanner(ProposedActionDraft draft, ProjectSnapshot snapshot)
    {
        if (draft.TargetTaskId is not { } targetTaskId || targetTaskId == Guid.Empty)
        {
            return null;
        }

        if (!snapshot.Tasks.Any(task => task.TaskId == targetTaskId))
        {
            return null;
        }

        // Title and outcome belong to CreateTask. Carrying provider prose onto a StartPlanner
        // proposal would put text on a row whose rendering reads everything else from the task.
        return new ValidatedAction(FamiliarActionKind.StartPlanner, null, null, targetTaskId);
    }

    /// <summary>Present, non-whitespace, and within the column's bound.</summary>
    public static bool IsWithinBounds(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength;
}
