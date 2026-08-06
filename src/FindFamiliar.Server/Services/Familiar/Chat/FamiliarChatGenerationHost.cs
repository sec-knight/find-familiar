using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// Generation, detached from whichever connection asked for it.
///
/// This is the property Sprint 12 called structural from commit one. A send commits a Pending turn
/// and returns; this host picks it up, moves it to Generating, accumulates output into the row, and
/// writes a terminal state. No step of that depends on the sender still being connected, so closing
/// a laptop mid-reply loses nothing, and a phone picking the conversation up reads the same row.
///
/// Two entry points, and the second is what makes the first safe to lose:
///
/// - the in-memory queue, for turns committed by a live request;
/// - a sweep at startup, which fails every turn left Generating by a process that died and
///   re-enqueues every turn left Pending.
///
/// The sweep runs before the queue is read, so a restart is a full recovery rather than a partial
/// one. Nothing here retries a failed turn: a turn that failed has an honest terminal record, and
/// re-running it silently would replace that record with a different one.
/// </summary>
public sealed class FamiliarChatGenerationHost(
    FamiliarChatGenerationQueue queue,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<FamiliarChatGenerationHost> logger) : BackgroundService
{
    /// <summary>A turn whose generator did not survive the process. Written by the startup sweep.</summary>
    public const string InterruptedFailureCode = "generation-interrupted";

    /// <summary>
    /// A generator that threw, which its interface says it must not. Recorded as this application's
    /// own classification; the exception's message is logged for an operator and never persisted.
    /// </summary>
    public const string FaultedFailureCode = "generation-faulted";

    public const string InterruptedSentence =
        "This reply was interrupted when the server restarted, so it was never finished. Nothing else was lost — ask again.";

    public const string FaultedSentence =
        "This reply could not be produced. Nothing about the conversation was lost — ask again.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var turnId in queue.ReadAllAsync(stoppingToken))
        {
            // One scope per turn: a DbContext is not shared across turns, and a turn that faults
            // cannot leave a poisoned change tracker behind for the next one.
            await ProcessAsync(turnId, stoppingToken);
        }
    }

    /// <summary>
    /// Reconciles what the last process left behind, before any new turn is taken.
    ///
    /// Generating turns are failed rather than restarted. The generator that held one is gone, its
    /// partial output is already in the row, and re-running it would append a second reply to the
    /// same turn. Saying plainly that it was interrupted is the honest record, and it also releases
    /// the in-flight slot that <c>IX_FamiliarChatTurns_ChatId_InFlight</c> holds — otherwise a
    /// conversation would be permanently unable to accept another turn.
    /// </summary>
    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

            var interrupted = await dbContext.FamiliarChatTurns
                .Where(turn => turn.State == FamiliarChatTurnState.Generating)
                .ToListAsync(cancellationToken);

            foreach (var turn in interrupted)
            {
                // Partial output is kept. It is what the person already saw, and deleting it would
                // make the transcript disagree with the screen they were looking at.
                turn.State = FamiliarChatTurnState.Failed;
                turn.FailureCode = InterruptedFailureCode;
                turn.Output = turn.Output.Length == 0
                    ? InterruptedSentence
                    : turn.Output;
                turn.CompletedUtc = nowUtc;
            }

            if (interrupted.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning(
                    "Failed {Count} Familiar chat turn(s) left generating by a previous process.",
                    interrupted.Count);
            }

            // Pending turns are simply unscheduled — the queue that held them was in memory. They
            // are still durable and still valid, so they go back on it.
            var pending = await dbContext.FamiliarChatTurns
                .AsNoTracking()
                .Where(turn => turn.State == FamiliarChatTurnState.Pending)
                .OrderBy(turn => turn.CreatedUtc)
                .Select(turn => turn.Id)
                .ToListAsync(cancellationToken);

            foreach (var turnId in pending)
            {
                queue.Enqueue(turnId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sweep that fails must not take the host down with it: the queue still works, and new
            // turns must still generate. The next restart sweeps again.
            logger.LogError(exception, "The Familiar chat recovery sweep did not complete.");
        }
    }

    /// <summary>
    /// Generates one turn, start to terminal state.
    ///
    /// Idempotent by state: a turn that is not Pending is skipped. That is what makes re-enqueueing
    /// safe, whether it comes from the startup sweep or from a duplicate send.
    /// </summary>
    internal async Task ProcessAsync(Guid turnId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IFamiliarChatGenerator>();

        FamiliarChatTurn? turn;

        try
        {
            turn = await dbContext.FamiliarChatTurns
                .SingleOrDefaultAsync(candidate => candidate.Id == turnId, cancellationToken);

            if (turn is null || turn.State != FamiliarChatTurnState.Pending)
            {
                return;
            }

            turn.State = FamiliarChatTurnState.Generating;
            turn.StartedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            // The turn stays Pending and durable. The next startup sweep re-enqueues it, which is
            // the same recovery path a lost queue takes.
            logger.LogWarning(exception, "Could not take Familiar chat turn {TurnId}; it stays pending.", turnId);
            return;
        }

        var request = new FamiliarChatGenerationRequest(
            turn.ChatId,
            turn.Id,
            turn.Sequence,
            turn.UserText,
            turn.FocusProjectIdAtTime,
            turn.RequestedPlan);

        var sink = new FamiliarChatTurnOutputSink(dbContext, turn, timeProvider);

        FamiliarChatGenerationOutcome outcome;

        try
        {
            outcome = await generator.GenerateAsync(request, sink, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application is shutting down. The turn is deliberately left Generating: the next
            // process's sweep is the one place an interrupted turn is classified, and doing it here
            // as well would be a second, racing writer of the same fact.
            return;
        }
        catch (Exception exception)
        {
            // The interface says GenerateAsync never throws. One that does has broken its contract,
            // and the conversation must still reach a terminal state rather than sit in Generating
            // forever holding the in-flight slot.
            logger.LogError(exception, "The Familiar chat generator faulted on turn {TurnId}.", turnId);
            outcome = FamiliarChatGenerationOutcome.Failed(FaultedFailureCode, FaultedSentence);
        }

        try
        {
            // Unconditionally, before the terminal state: the last characters of a reply must not be
            // the ones the throttle left behind.
            await sink.FlushAsync(cancellationToken);

            if (outcome.Metadata is { } metadata)
            {
                turn.ProviderName = Truncate(metadata.ProviderName, FamiliarChatTurn.MaxProviderNameLength);
                turn.ProviderModel = Truncate(metadata.ProviderModel, FamiliarChatTurn.MaxProviderModelLength);
                turn.InputTokens = metadata.InputTokens;
                turn.OutputTokens = metadata.OutputTokens;
                turn.CachedInputTokens = metadata.CachedInputTokens;
            }

            if (outcome.Succeeded)
            {
                turn.State = FamiliarChatTurnState.Completed;
                turn.FailureCode = null;
            }
            else
            {
                turn.State = FamiliarChatTurnState.Failed;
                turn.FailureCode = outcome.FailureCode;

                // The sentence is this application's own, and it replaces nothing: it is written
                // only where no output was produced, so a partial reply is never overwritten by an
                // explanation of why it stopped.
                if (turn.Output.Length == 0 && outcome.Sentence is { Length: > 0 } sentence)
                {
                    turn.Output = Truncate(sentence);
                }
            }

            turn.CompletedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            // The turn stays Generating and the next sweep classifies it as interrupted, which is
            // exactly what it is from a reader's point of view: a reply that never landed.
            logger.LogError(exception, "Could not record the outcome of Familiar chat turn {TurnId}.", turnId);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= FamiliarChatTurn.MaxOutputLength
            ? value
            : value[..FamiliarChatTurn.MaxOutputLength];

    /// <summary>
    /// Trims metadata to its column. A provider that returns an unexpectedly long model name must not
    /// fail the write that records an otherwise good reply.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// Accumulates a generator's output into the persisted turn, committing periodically rather than per
/// fragment.
///
/// A streaming provider emits a fragment per token, and a commit per token would be thousands of
/// SQLite writes for one reply — enough to make the database the bottleneck in a lane whose whole
/// point is latency. So the in-memory turn is updated on every fragment and the transaction is
/// committed on a bound: whichever of <see cref="FlushCharacters"/> or <see cref="FlushInterval"/>
/// comes first.
///
/// The cost of that choice is bounded and stated: a process that dies mid-reply loses at most the
/// text written since the last flush, and the next process's sweep marks the turn interrupted with
/// the partial output that did land. Readers never see a torn write — they see a slightly older one.
/// </summary>
internal sealed class FamiliarChatTurnOutputSink(
    FamiliarDbContext dbContext,
    FamiliarChatTurn turn,
    TimeProvider timeProvider) : IFamiliarChatOutputSink
{
    /// <summary>Roughly a sentence. Small enough that a reader sees text move, large enough to batch.</summary>
    public const int FlushCharacters = 180;

    /// <summary>So a slow stream still reaches the page promptly rather than waiting for volume.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private long _lastFlushTimestamp = timeProvider.GetTimestamp();
    private int _unflushedCharacters;

    public async Task RecordEvidenceAsync(
        IReadOnlyCollection<Guid> entryIds,
        CancellationToken cancellationToken = default)
    {
        turn.EvidenceEntryIds = FamiliarChatCitations.SerialiseEvidence(entryIds);

        // Committed now, not on the throttle. The first sentence of a reply can contain a citation,
        // and a chip that cannot be checked yet would render as unsupported and then correct itself —
        // which is worse than not rendering, because a reader would have seen the accusation.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AppendAsync(string fragment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            return;
        }

        var remaining = FamiliarChatTurn.MaxOutputLength - turn.Output.Length;

        if (remaining <= 0)
        {
            // Silently discarded rather than allowed to fail the write. The cap is this
            // application's bound on one reply; hitting it must not cost the reply already stored.
            return;
        }

        var accepted = fragment.Length <= remaining ? fragment : fragment[..remaining];
        turn.Output += accepted;
        _unflushedCharacters += accepted.Length;

        if (_unflushedCharacters >= FlushCharacters
            || timeProvider.GetElapsedTime(_lastFlushTimestamp) >= FlushInterval)
        {
            await FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Commits whatever is buffered. Called on the bounds above and unconditionally by the host
    /// before it writes a terminal state, so the last few characters of a reply are never the ones
    /// left behind.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_unflushedCharacters == 0)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        _unflushedCharacters = 0;
        _lastFlushTimestamp = timeProvider.GetTimestamp();
    }
}
