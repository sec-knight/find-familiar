using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// One project's conversation: the read side, and the send flow.
///
/// Every query filters on <c>projectId</c>, as <c>DemiplaneProjectionService</c> does: a conversation
/// is reached through its project and never through an id supplied by a caller, so no id from another
/// project can pull that project's transcript onto this page. Pending proposals are filtered on their
/// denormalised <see cref="FamiliarActionProposal.ProjectId"/> as well as their conversation, so
/// ownership holds even if the two ever disagreed.
///
/// Reads are <c>AsNoTracking</c> and ordered by <see cref="FamiliarMessage.Sequence"/>, never by
/// timestamp: two messages written in the same tick must still have one correct order, and the
/// sequence is the column that guarantees it.
///
/// Nothing here executes an action. A provider's reply becomes one row of text, server-resolved
/// evidence, and at most one <b>Pending</b> proposal — a record of what a person will be shown, never
/// authority to act. <c>IWorkflowDispatchService</c> is not reachable from this file; only
/// <see cref="FamiliarActionService"/> can turn a proposal into work, and only on an explicit human
/// confirmation that re-validates every gate inside its own transaction.
/// </summary>
public sealed class FamiliarConversationService(
    FamiliarDbContext dbContext,
    IProjectSnapshotService snapshots,
    IFamiliarReasoningProvider provider,
    IOptions<FamiliarReasoningOptions> options,
    TimeProvider timeProvider) : IFamiliarConversationService
{
    /// <summary>Specification §9. Longer than any question a person asks about one project.</summary>
    public const int MaxUserMessageCharacters = 4_000;

    /// <summary>
    /// Turns of history sent to the provider. A count, not a size — <see cref="FamiliarRequestEnvelope"/>
    /// owns the size bound, because ten turns of maximum-length messages is far larger than any
    /// request budget.
    /// </summary>
    public const int MaxHistoryTurns = 10;

    /// <summary>
    /// The project could not be read. Not one of user-experience.md §3's eight codes, because that
    /// table covers reasoning-provider failures and this is a database that was busy. It exists
    /// because by the time the snapshot is built the human's message is already durable, and a
    /// transcript that shows a question with no answer and no explanation is worse than one that says
    /// plainly what happened.
    /// </summary>
    public const string SnapshotUnavailableCode = "snapshot-unavailable";

    public async Task<FamiliarConversationView?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.FamiliarConversations
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId)
            .Select(candidate => new { candidate.Id, candidate.ProjectId })
            .SingleOrDefaultAsync(cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        var messages = await dbContext.FamiliarMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.Sequence)
            .Select(message => new FamiliarMessageView(
                message.Id,
                message.Author,
                message.Sequence,
                message.Content,
                message.CreatedUtc,
                message.ProviderName,
                message.ProviderModel,
                message.Delivery,
                message.FailureCode,
                message.Evidence
                    .OrderBy(evidence => evidence.Label)
                    .Select(evidence => new FamiliarEvidenceView(
                        evidence.Kind,
                        evidence.ReferenceId,
                        evidence.Label))
                    .ToList()))
            .ToListAsync(cancellationToken);

        var proposals = await dbContext.FamiliarActionProposals
            .AsNoTracking()
            .Where(proposal =>
                proposal.ConversationId == conversation.Id
                && proposal.ProjectId == projectId
                && proposal.Status == FamiliarActionStatus.Pending)
            .OrderBy(proposal => proposal.CreatedUtc)
            .Select(proposal => new FamiliarProposalView(
                proposal.Id,
                proposal.MessageId,
                proposal.Kind,
                proposal.ConcurrencyToken,
                proposal.ObservedContextRevision,
                proposal.Title,
                proposal.RequestedOutcome,
                proposal.TargetTaskId,
                // Resolved server-side from the task row, so the target's name on the page is
                // persisted state rather than anything a provider wrote about it.
                proposal.TargetTask == null ? null : proposal.TargetTask.Title,
                proposal.CreatedUtc))
            .ToListAsync(cancellationToken);

        return new FamiliarConversationView(conversation.Id, conversation.ProjectId, messages, proposals);
    }

    public async Task<FamiliarSendResult> SendAsync(
        Guid projectId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (message ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return FamiliarSendResult.Invalid("Type a message before sending.");
        }

        if (trimmed.Length > MaxUserMessageCharacters)
        {
            return FamiliarSendResult.Invalid(
                $"Keep your message to {MaxUserMessageCharacters:N0} characters or fewer.");
        }

        // ---- Transaction A: the user's words become durable before any network I/O ----
        Guid conversationId;
        int humanSequence;

        try
        {
            var appended = await AppendHumanMessageAsync(projectId, trimmed, cancellationToken);

            if (appended.Refusal == FamiliarSendStatus.ProjectNotFound)
            {
                return FamiliarSendResult.ProjectNotFound();
            }

            if (appended.Refusal is not null)
            {
                return FamiliarSendResult.Invalid(
                    "This project is archived, so no work can be started from here.");
            }

            (conversationId, humanSequence) = (appended.ConversationId, appended.Sequence);
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            dbContext.ChangeTracker.Clear();

            // Busy and locked are retryable and nothing was written. Nothing here claims a competing
            // actor, because none has been established.
            return FamiliarSendResult.DatabaseBusy();
        }

        // ---- Snapshot: built after the commit, so a slow or failing read cannot lose the message ----
        var snapshotResult = await snapshots.GetSnapshotAsync(projectId, cancellationToken);

        if (snapshotResult.Snapshot is null)
        {
            await AppendSystemMessageAsync(
                conversationId,
                humanSequence + 1,
                SnapshotUnavailableCode,
                "This project could not be read just now, so nothing was sent to the reasoning provider. Your message was saved — try again.",
                cancellationToken);

            return FamiliarSendResult.Reported();
        }

        var snapshot = snapshotResult.Snapshot;

        if (snapshotResult.Outcome == ProjectSnapshotOutcome.TooLarge || !snapshot.IsWithinBudget)
        {
            // The project was reduced by every documented step and still does not fit. Refusing is
            // the honest move; sending a quietly truncated project would answer about a different
            // project than the one on the page.
            var tooLarge = FamiliarFailureWording.TooLarge();

            await AppendSystemMessageAsync(
                conversationId, humanSequence + 1, tooLarge.Code, tooLarge.Sentence, cancellationToken);

            return FamiliarSendResult.Reported();
        }

        // ---- Bound the request, and measure the whole of it ----
        var history = await ReadHistoryAsync(conversationId, humanSequence, cancellationToken);
        var envelope = FamiliarRequestEnvelope.Fit(snapshot, history, trimmed, FamiliarBehaviorContract.Text);

        if (!envelope.Fits)
        {
            var tooLarge = FamiliarFailureWording.TooLarge();

            await AppendSystemMessageAsync(
                conversationId, humanSequence + 1, tooLarge.Code, tooLarge.Sentence, cancellationToken);

            return FamiliarSendResult.Reported();
        }

        // A trimmed conversation is a bound that bit, so it is stated in the same place every other
        // bound is stated rather than left for the reader to notice.
        var sentSnapshot = envelope.DroppedTurns == 0
            ? snapshot
            : snapshot with
            {
                Limitations = [.. snapshot.Limitations, FamiliarRequestEnvelope.DroppedHistoryLimitation(envelope.DroppedTurns)]
            };

        // ---- The provider call, bounded by this application's own timeout ----
        var outcome = await RespondAsync(
            new FamiliarReasoningRequest(sentSnapshot, envelope.History, trimmed, FamiliarBehaviorContract.Text),
            cancellationToken);

        // ---- Transaction B: whatever the provider had to say ----
        try
        {
            if (outcome.Status == FamiliarReasoningStatus.Answered
                && !string.IsNullOrWhiteSpace(outcome.Reply))
            {
                await AppendFamiliarMessageAsync(
                    conversationId, humanSequence + 1, outcome, sentSnapshot, cancellationToken);

                return FamiliarSendResult.Answered();
            }

            // An Answered with no reply has broken the interface's contract, and the honest thing to
            // call that is a response this application could not use — not a silent empty bubble.
            var status = outcome.Status == FamiliarReasoningStatus.Answered
                ? FamiliarReasoningStatus.Malformed
                : outcome.Status;

            var note = FamiliarFailureWording.For(
                status,
                provider.Provider == UnconfiguredFamiliarReasoningProvider.ProviderName,
                options.Value.ResolvedTimeoutSeconds);

            // outcome.Detail is deliberately not persisted and not rendered. It may name a host, a
            // path, an exception type or part of a credential; the sentence above is this
            // application's own and is the only thing a person reads.
            await AppendSystemMessageAsync(
                conversationId, humanSequence + 1, note.Code, note.Sentence, cancellationToken);

            return FamiliarSendResult.Reported();
        }
        catch (Exception exception) when (IsExpectedDatabaseFault(exception))
        {
            dbContext.ChangeTracker.Clear();

            // The human message is already committed, so this loses the reply rather than the
            // question — which is the trade the two-transaction shape was chosen to make.
            return FamiliarSendResult.DatabaseBusy();
        }
    }

    /// <summary>
    /// Calls the provider under a caller-owned timeout.
    ///
    /// The linked source is this application's bound, not the SDK's default, so the timeout is
    /// configured in one place and is the same for every implementation. Caller cancellation stays
    /// distinguishable from it: if the caller's token is the one that fired, the exception propagates
    /// as cancellation, because a request the user abandoned is not a provider that timed out and
    /// must not be recorded as one.
    /// </summary>
    private async Task<FamiliarReasoningOutcome> RespondAsync(
        FamiliarReasoningRequest request,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = options.Value.ResolvedTimeoutSeconds;
        var metadata = new FamiliarProviderMetadata(provider.Provider, null, null);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var startedAt = timeProvider.GetTimestamp();

        try
        {
            var outcome = await provider.RespondAsync(request, linked.Token);

            var latencyMs = (int)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

            return outcome with
            {
                Metadata = outcome.Metadata with { LatencyMs = outcome.Metadata.LatencyMs ?? latencyMs }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own bound elapsed.
            return FamiliarReasoningOutcome.Failed(
                FamiliarReasoningStatus.TimedOut,
                metadata,
                $"The reasoning provider did not answer within {timeoutSeconds} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The interface says RespondAsync never throws. An implementation that does has broken
            // its contract, and the page must still render — so this is classified rather than left
            // to become a 500. The exception's message is not carried anywhere.
            return FamiliarReasoningOutcome.Failed(
                FamiliarReasoningStatus.Unavailable,
                metadata,
                "The reasoning provider failed in a way this application does not classify.");
        }
    }

    /// <summary>
    /// What Transaction A did. A refusal writes nothing at all, so the project is exactly as it was.
    /// </summary>
    private sealed record AppendOutcome(Guid ConversationId, int Sequence, FamiliarSendStatus? Refusal)
    {
        public static readonly AppendOutcome ProjectMissing =
            new(Guid.Empty, 0, FamiliarSendStatus.ProjectNotFound);

        public static readonly AppendOutcome ProjectInactive =
            new(Guid.Empty, 0, FamiliarSendStatus.Invalid);
    }

    /// <summary>
    /// Transaction A. Appends the human message and commits, or refuses having written nothing.
    /// </summary>
    private async Task<AppendOutcome> AppendHumanMessageAsync(
        Guid projectId,
        string content,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var status = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (ProjectStatus?)project.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (status is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AppendOutcome.ProjectMissing;
        }

        if (status != ProjectStatus.Active)
        {
            // The page disables the input on an archived project, but a disabled control is a hint
            // to a browser and not a rule. The rule is here.
            await transaction.RollbackAsync(cancellationToken);
            return AppendOutcome.ProjectInactive;
        }

        var conversation = await dbContext.FamiliarConversations
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken);

        if (conversation is null)
        {
            // Created on the first send, never on a read. A project somebody only looked at keeps no
            // conversation row.
            conversation = new FamiliarConversation
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };

            dbContext.FamiliarConversations.Add(conversation);
        }
        else
        {
            conversation.UpdatedUtc = nowUtc;
        }

        var sequence = await NextSequenceAsync(conversation.Id, cancellationToken);

        dbContext.FamiliarMessages.Add(new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = FamiliarMessageAuthor.Human,
            Sequence = sequence,
            Content = content,
            CreatedUtc = nowUtc,
            Delivery = FamiliarMessageDelivery.Delivered
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AppendOutcome(conversation.Id, sequence, null);
    }

    /// <summary>
    /// Appends a page-composed note. The Familiar never speaks in an error's voice: a failure of a
    /// component it cannot observe is stated by the application, in its own author.
    /// </summary>
    private async Task AppendSystemMessageAsync(
        Guid conversationId,
        int preferredSequence,
        string failureCode,
        string sentence,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.FamiliarMessages.Add(new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Author = FamiliarMessageAuthor.System,
            Sequence = await ResolveSequenceAsync(conversationId, preferredSequence, cancellationToken),
            Content = sentence,
            CreatedUtc = nowUtc,
            Delivery = FamiliarMessageDelivery.Failed,
            FailureCode = failureCode
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Appends the reply and the evidence that survived validation.
    ///
    /// Only <see cref="FamiliarReasoningOutcome.Reply"/> is stored. No prompt, no contract, no raw
    /// payload, no thinking — the schema has no column for any of them, so nothing can write one.
    /// </summary>
    private async Task AppendFamiliarMessageAsync(
        Guid conversationId,
        int preferredSequence,
        FamiliarReasoningOutcome outcome,
        ProjectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var message = new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Author = FamiliarMessageAuthor.Familiar,
            Sequence = await ResolveSequenceAsync(conversationId, preferredSequence, cancellationToken),
            Content = outcome.Reply!.Trim(),
            CreatedUtc = nowUtc,
            ProviderName = outcome.Metadata.Provider,
            ProviderModel = outcome.Metadata.Model,
            LatencyMs = outcome.Metadata.LatencyMs,
            Delivery = FamiliarMessageDelivery.Delivered
        };

        dbContext.FamiliarMessages.Add(message);

        foreach (var evidence in ResolveEvidence(outcome.EvidenceIds, snapshot, message.Id))
        {
            dbContext.FamiliarEvidence.Add(evidence);
        }

        // The reply is committed on its own, before any proposal is attempted. A draft that cannot
        // be stored must never cost a person the answer they were given.
        await dbContext.SaveChangesAsync(cancellationToken);

        // At most one proposal, and only if the draft validates against the snapshot that produced
        // it. A rejected draft is not an error: the reply is still shown and the person simply gets
        // no button, because reporting "the model proposed something invalid" would teach people to
        // read model intent as system state.
        //
        // What is written here is a record of what a human will be shown. It is not authority to
        // act: nothing executes until FamiliarActionService consumes this row on an explicit
        // confirmation and re-validates every gate inside that transaction.
        if (ProposedActionValidator.Validate(outcome.Actions, snapshot) is not { } validated)
        {
            return;
        }

        dbContext.FamiliarActionProposals.Add(new FamiliarActionProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            ProjectId = snapshot.ProjectId,
            MessageId = message.Id,
            Kind = validated.Kind,
            Status = FamiliarActionStatus.Pending,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = snapshot.ContextRevision,
            Title = validated.Title,
            RequestedOutcome = validated.RequestedOutcome,
            TargetTaskId = validated.TargetTaskId,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (
            SessionHandoffApprovalService.IsUniqueConstraintViolation(exception))
        {
            // IX_FamiliarActionProposals_ConversationId_Pending allows one undecided proposal per
            // conversation, and two concurrent sends can both pass any prior check before either
            // commits. The database picks the winner; the loser's draft is dropped exactly as any
            // other draft that does not survive validation — silently, with the reply intact.
            //
            // Classified explicitly rather than left to the caller's catch, which would report a
            // unique violation as a busy database. That would be a false claim about what happened,
            // and this codebase reserves "busy" for conditions that are actually retryable.
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Turns cited ids into evidence rows, using the exact snapshot that produced the reply.
    ///
    /// The lookup decides both the kind and the label, so a provider cannot mislabel a session as a
    /// task and cannot attach prose of its own to a record. An id that resolves to nothing is dropped
    /// silently and without comment: a hallucinated citation is not an event worth reporting to a
    /// user, and reporting it would teach people to read model intent as system state.
    /// </summary>
    private static IEnumerable<FamiliarEvidence> ResolveEvidence(
        IReadOnlyList<Guid> citedIds,
        ProjectSnapshot snapshot,
        Guid messageId)
    {
        foreach (var id in citedIds.Distinct())
        {
            FamiliarEvidence? resolved = null;

            if (snapshot.Tasks.FirstOrDefault(task => task.TaskId == id) is { } task)
            {
                resolved = Compose(FamiliarEvidenceKind.Task, id, $"Task \"{task.Title}\"");
            }
            else if (snapshot.Sessions.FirstOrDefault(session => session.SessionId == id) is { } session)
            {
                resolved = Compose(
                    FamiliarEvidenceKind.Session,
                    id,
                    $"{session.Role} session on \"{session.TaskTitle}\"");
            }
            else if (snapshot.PendingHandoffs.FirstOrDefault(handoff => handoff.HandoffId == id) is { } handoff)
            {
                resolved = Compose(
                    FamiliarEvidenceKind.Handoff,
                    id,
                    $"Proposed {handoff.ProposedRole} step on \"{handoff.TaskTitle}\"");
            }
            else if (snapshot.ContextEntries.FirstOrDefault(entry => entry.ContextEntryId == id) is { } entry)
            {
                resolved = Compose(
                    FamiliarEvidenceKind.ContextEntry,
                    id,
                    $"{entry.Kind}: {entry.Title}");
            }

            if (resolved is not null)
            {
                resolved.MessageId = messageId;
                yield return resolved;
            }
        }

        static FamiliarEvidence Compose(FamiliarEvidenceKind kind, Guid referenceId, string label) => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ReferenceId = referenceId,
            Label = Truncate(label, FamiliarEvidence.MaxLabelLength)
        };
    }

    /// <summary>
    /// The turns sent to the provider: the most recent <see cref="MaxHistoryTurns"/> visible messages
    /// before this one, oldest first.
    ///
    /// <see cref="FamiliarMessageAuthor.System"/> messages are excluded. They are page-composed error
    /// notes, and re-feeding them teaches a model to imitate error text — to write "the reasoning
    /// provider could not be reached" as though it were an observation of its own.
    /// </summary>
    private async Task<IReadOnlyList<FamiliarTurn>> ReadHistoryAsync(
        Guid conversationId,
        int beforeSequence,
        CancellationToken cancellationToken)
    {
        var recent = await dbContext.FamiliarMessages
            .AsNoTracking()
            .Where(message =>
                message.ConversationId == conversationId
                && message.Sequence < beforeSequence
                && (message.Author == FamiliarMessageAuthor.Human
                    || message.Author == FamiliarMessageAuthor.Familiar))
            .OrderByDescending(message => message.Sequence)
            .Take(MaxHistoryTurns)
            .Select(message => new { message.Author, message.Content, message.Sequence })
            .ToListAsync(cancellationToken);

        return recent
            .OrderBy(message => message.Sequence)
            .Select(message => new FamiliarTurn(message.Author, message.Content))
            .ToList();
    }

    private async Task<int> NextSequenceAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var highest = await dbContext.FamiliarMessages
            .Where(message => message.ConversationId == conversationId)
            .Select(message => (int?)message.Sequence)
            .MaxAsync(cancellationToken);

        return (highest ?? 0) + 1;
    }

    /// <summary>
    /// The sequence a follow-up message should take.
    ///
    /// Normally the one after the human message, but the conversation is re-read rather than assumed:
    /// two people sending at once would each hold a sequence, and the unique index on
    /// <c>(ConversationId, Sequence)</c> would reject a collision rather than quietly interleaving.
    /// </summary>
    private async Task<int> ResolveSequenceAsync(
        Guid conversationId,
        int preferredSequence,
        CancellationToken cancellationToken)
    {
        var next = await NextSequenceAsync(conversationId, cancellationToken);
        return Math.Max(next, preferredSequence);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Database faults this application expects and classifies. Anything else is a real defect and is
    /// left to propagate rather than dressed up as a busy database.
    /// </summary>
    private static bool IsExpectedDatabaseFault(Exception exception) =>
        exception is DbUpdateException or SqliteException;
}
