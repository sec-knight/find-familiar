using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// The system-wide Familiar conversation: its list, its transcript, its resume read, and its send.
///
/// Reads are <c>AsNoTracking</c> and ordered by <see cref="FamiliarChatTurn.Sequence"/>, never by
/// timestamp — two turns written in the same tick must still have one correct order.
///
/// Nothing here calls a provider and nothing here writes to project state. A send makes a turn
/// durable and returns; <see cref="FamiliarChatGenerationHost"/> produces the reply out of band. The
/// Sprint 12 constraint carried forward from ADR-0013 is that the talk lane changes nothing, and this
/// file is where that would first be violated: <c>IWorkflowDispatchService</c> is not reachable from
/// it, and neither is <c>FamiliarActionService</c>.
/// </summary>
public sealed class FamiliarChatService(
    FamiliarDbContext dbContext,
    FamiliarChatGenerationQueue queue,
    TimeProvider timeProvider) : IFamiliarChatService
{
    public async Task<IReadOnlyList<FamiliarChatSummary>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.FamiliarChats
            .AsNoTracking()
            .OrderByDescending(chat => chat.UpdatedUtc)
            .ThenByDescending(chat => chat.CreatedUtc)
            .Select(chat => new FamiliarChatSummary(
                chat.Id,
                chat.Title,
                chat.FocusProjectId,
                // Resolved from the project row, so the name in the list is persisted state.
                chat.FocusProject == null ? null : chat.FocusProject.Name,
                chat.Turns.Count,
                chat.Turns.Any(turn =>
                    turn.State == FamiliarChatTurnState.Pending
                    || turn.State == FamiliarChatTurnState.Generating),
                chat.CreatedUtc,
                chat.UpdatedUtc))
            .ToListAsync(cancellationToken);

    public async Task<FamiliarChatView?> GetAsync(
        Guid chatId,
        CancellationToken cancellationToken = default)
    {
        var chat = await dbContext.FamiliarChats
            .AsNoTracking()
            .Where(candidate => candidate.Id == chatId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Title,
                candidate.FocusProjectId,
                FocusProjectName = candidate.FocusProject == null ? null : candidate.FocusProject.Name
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (chat is null)
        {
            return null;
        }

        var turns = await ReadTurnsAsync(chatId, afterSequence: 0, cancellationToken);

        return new FamiliarChatView(chat.Id, chat.Title, chat.FocusProjectId, chat.FocusProjectName, turns);
    }

    public async Task<FamiliarChatTurnPage?> ReadTurnsAfterAsync(
        Guid chatId,
        int afterSequence,
        CancellationToken cancellationToken = default)
    {
        // Existence is checked separately from the turn read. A conversation with no turns after the
        // cursor and a conversation that does not exist are different answers, and collapsing them
        // would leave a client polling a deleted conversation forever.
        var exists = await dbContext.FamiliarChats
            .AsNoTracking()
            .AnyAsync(chat => chat.Id == chatId, cancellationToken);

        if (!exists)
        {
            return null;
        }

        // A negative cursor is normalised rather than refused: it asks for everything, which is what
        // a client with no cursor at all means.
        var cursor = Math.Max(afterSequence, 0);

        var turns = await ReadTurnsAsync(chatId, cursor, cancellationToken);

        // Read from the conversation, not from the returned page: a client that is fully caught up
        // gets no turns back and must still learn the true head of the conversation.
        var latest = await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .Where(turn => turn.ChatId == chatId)
            .Select(turn => (int?)turn.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        var inFlight = await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .AnyAsync(
                turn => turn.ChatId == chatId
                    && (turn.State == FamiliarChatTurnState.Pending
                        || turn.State == FamiliarChatTurnState.Generating),
                cancellationToken);

        return new FamiliarChatTurnPage(chatId, cursor, latest, inFlight, turns);
    }

    public async Task<FamiliarChatSendResult> SendAsync(
        Guid? chatId,
        string message,
        Guid? focusProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (message ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return FamiliarChatSendResult.Invalid("Type a message before sending.");
        }

        if (trimmed.Length > FamiliarChatTurn.MaxUserTextLength)
        {
            return FamiliarChatSendResult.Invalid(
                $"Keep your message to {FamiliarChatTurn.MaxUserTextLength:N0} characters or fewer.");
        }

        AppendedTurn appended;

        try
        {
            appended = await AppendTurnAsync(chatId, trimmed, focusProjectId, cancellationToken);
        }
        catch (Exception exception) when (IsUniqueConstraintViolation(exception))
        {
            dbContext.ChangeTracker.Clear();

            // IX_FamiliarChatTurns_ChatId_InFlight rejected this because another send committed
            // first. Reported as an attach rather than as an error, because that is what happened:
            // a turn is running on this conversation and the sender should watch it. Classified
            // explicitly rather than falling into the busy-database catch below, which would be a
            // false claim — this is not retryable and nothing about it is transient.
            var inFlight = await ReadInFlightSequenceAsync(chatId, cancellationToken);

            return inFlight is { } sequence
                ? FamiliarChatSendResult.Attached(chatId!.Value, sequence)
                : FamiliarChatSendResult.DatabaseBusy;
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            dbContext.ChangeTracker.Clear();

            // Busy and locked are retryable and nothing was written. No competing actor is claimed,
            // because none has been established.
            return FamiliarChatSendResult.DatabaseBusy;
        }

        if (appended.TurnId is { } turnId)
        {
            // Enqueued only after the commit. A turn is durable before it is scheduled, never the
            // other way round — the reverse would let a generator read a row that does not exist.
            queue.Enqueue(turnId);
        }

        return appended.Result;
    }

    /// <summary>
    /// What the write did, plus the row id the caller must schedule. The id stays inside this file:
    /// a turn id is a handle to work in progress, and callers of <see cref="SendAsync"/> address the
    /// conversation by sequence.
    /// </summary>
    private sealed record AppendedTurn(FamiliarChatSendResult Result, Guid? TurnId = null);

    /// <summary>
    /// The whole write, in one transaction: create the conversation if needed, refuse if a turn is
    /// already in flight, take the next sequence, append.
    ///
    /// The in-flight check and the insert are inside the same transaction, and the database's
    /// filtered unique index is what actually enforces the rule — the check here exists so the
    /// ordinary case reports an attach without provoking a constraint violation, not because it is
    /// the guarantee. Two sends that both pass the check still cannot both commit.
    /// </summary>
    private async Task<AppendedTurn> AppendTurnAsync(
        Guid? chatId,
        string message,
        Guid? focusProjectId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        FamiliarChat chat;

        if (chatId is { } existingId)
        {
            var existing = await dbContext.FamiliarChats
                .SingleOrDefaultAsync(candidate => candidate.Id == existingId, cancellationToken);

            if (existing is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AppendedTurn(FamiliarChatSendResult.ChatNotFound);
            }

            chat = existing;

            var inFlight = await dbContext.FamiliarChatTurns
                .AsNoTracking()
                .Where(turn =>
                    turn.ChatId == existingId
                    && (turn.State == FamiliarChatTurnState.Pending
                        || turn.State == FamiliarChatTurnState.Generating))
                .Select(turn => (int?)turn.Sequence)
                .FirstOrDefaultAsync(cancellationToken);

            if (inFlight is { } inFlightSequence)
            {
                // Attach, do not queue. A second sender joins the reply that is running; their text
                // is not written, and the page keeps it in the composer so nothing they typed is
                // lost.
                await transaction.RollbackAsync(cancellationToken);
                return new AppendedTurn(FamiliarChatSendResult.Attached(existingId, inFlightSequence));
            }

            chat.UpdatedUtc = nowUtc;

            if (focusProjectId is not null)
            {
                chat.FocusProjectId = focusProjectId;
            }
        }
        else
        {
            // Created on a send, never on a read — a page somebody only looked at leaves no row, the
            // same rule the per-project conversation holds.
            chat = new FamiliarChat
            {
                Id = Guid.NewGuid(),
                Title = FamiliarChatTitleComposer.Compose(message),
                FocusProjectId = focusProjectId,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };

            dbContext.FamiliarChats.Add(chat);
        }

        var sequence = await NextSequenceAsync(chat.Id, cancellationToken);

        var turn = new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Sequence = sequence,
            State = FamiliarChatTurnState.Pending,
            UserText = message,
            FocusProjectIdAtTime = chat.FocusProjectId,
            Output = string.Empty,
            CreatedUtc = nowUtc
        };

        dbContext.FamiliarChatTurns.Add(turn);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AppendedTurn(FamiliarChatSendResult.Accepted(chat.Id, sequence), turn.Id);
    }

    private async Task<IReadOnlyList<FamiliarChatTurnView>> ReadTurnsAsync(
        Guid chatId,
        int afterSequence,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .Where(turn => turn.ChatId == chatId && turn.Sequence > afterSequence)
            .OrderBy(turn => turn.Sequence)
            .Select(turn => new
            {
                turn.Id,
                turn.Sequence,
                turn.State,
                turn.UserText,
                turn.Output,
                turn.FailureCode,
                turn.CreatedUtc,
                turn.CompletedUtc,
                turn.ProviderName,
                turn.ProviderModel,
                turn.EvidenceEntryIds
            })
            .ToListAsync(cancellationToken);

        var evidence = rows.ToDictionary(
            row => row.Id,
            row => FamiliarChatCitations.ParseEvidence(row.EvidenceEntryIds));

        var resolved = await ResolveCitationsAsync(
            evidence.Values.SelectMany(ids => ids).Distinct().ToList(),
            cancellationToken);

        return rows
            .Select(row => new FamiliarChatTurnView(
                row.Id,
                row.Sequence,
                row.State,
                row.UserText,
                row.Output,
                row.FailureCode,
                row.CreatedUtc,
                row.CompletedUtc,
                row.ProviderName,
                row.ProviderModel,
                evidence[row.Id]
                    .Where(resolved.ContainsKey)
                    .Select(id => resolved[id])
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// Turns offered ids into something displayable, re-applying the sensitivity filter as it goes.
    ///
    /// The filter is here rather than at write time on purpose. A turn records the ids it was given
    /// and never changes; whether a reader may still see an entry is decided now, against the entry's
    /// current flags. So marking a project sensitive today removes its titles from every past
    /// transcript, with no rewriting and nothing left behind to leak.
    ///
    /// An id that resolves to nothing — deleted, or now withheld — simply does not become a chip. The
    /// id still stands in the text, unmarked as a source, which is the honest rendering: the reply did
    /// cite something, and this reader cannot see what.
    /// </summary>
    private async Task<Dictionary<Guid, FamiliarChatCitationView>> ResolveCitationsAsync(
        IReadOnlyCollection<Guid> entryIds,
        CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0)
        {
            return [];
        }

        return await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry =>
                entryIds.Contains(entry.Id)
                && !entry.IsSensitive
                && !entry.Project.IsSensitive)
            .Select(entry => new FamiliarChatCitationView(
                entry.Id,
                entry.ProjectId,
                entry.Kind,
                entry.Title))
            .ToDictionaryAsync(citation => citation.EntryId, cancellationToken);
    }

    private async Task<int?> ReadInFlightSequenceAsync(Guid? chatId, CancellationToken cancellationToken)
    {
        if (chatId is not { } id)
        {
            return null;
        }

        return await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .Where(turn =>
                turn.ChatId == id
                && (turn.State == FamiliarChatTurnState.Pending
                    || turn.State == FamiliarChatTurnState.Generating))
            .Select(turn => (int?)turn.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int> NextSequenceAsync(Guid chatId, CancellationToken cancellationToken)
    {
        var highest = await dbContext.FamiliarChatTurns
            .Where(turn => turn.ChatId == chatId)
            .Select(turn => (int?)turn.Sequence)
            .MaxAsync(cancellationToken);

        return (highest ?? 0) + 1;
    }

    /// <summary>
    /// SQLITE_CONSTRAINT_UNIQUE, distinguished from a busy database because the two call for
    /// opposite responses: one is retryable and one never will be.
    /// </summary>
    private static bool IsUniqueConstraintViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 2067 } }
            or SqliteException { SqliteExtendedErrorCode: 2067 };
}
