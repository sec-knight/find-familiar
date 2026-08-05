using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public enum SessionHandoffDecisionStatus
{
    /// <summary>A session was created by this request.</summary>
    Approved,

    /// <summary>The handoff was already approved. Carries the session the winner created.</summary>
    AlreadyApproved,

    /// <summary>A human already declined this step.</summary>
    AlreadyDeclined,

    /// <summary>A newer terminal event on the task replaced this decision point.</summary>
    Superseded,

    /// <summary>This request was declined by a human.</summary>
    Declined,

    NotFound,

    /// <summary>The presented token is not the current one — the page was rendered before a change.</summary>
    StaleHandoff,

    /// <summary>The task already owns a Started session, so another cannot begin.</summary>
    SessionAlreadyStarted,

    /// <summary>The task is Completed; no further work starts on it.</summary>
    TaskClosed,

    ProjectInactive,

    /// <summary>Lost a race in a way none of the states above describes.</summary>
    Conflict
}

public sealed record SessionHandoffDecisionRequest(Guid HandoffId, Guid ExpectedConcurrencyToken);

public sealed record SessionHandoffDecisionOutcome(
    SessionHandoffDecisionStatus Status,
    Guid? SessionId = null,
    Guid? TaskId = null,
    AgentSessionRole? Role = null)
{
    public static readonly SessionHandoffDecisionOutcome NotFound = new(SessionHandoffDecisionStatus.NotFound);
}

public interface ISessionHandoffApprovalService
{
    Task<SessionHandoffDecisionOutcome> ApproveAsync(
        SessionHandoffDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionHandoffDecisionOutcome> DeclineAsync(
        SessionHandoffDecisionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sprint 09's critical transaction: the single point where a human's approval becomes a session.
///
/// Three mechanisms are involved, and confusing them is how the invariant gets lost later:
///
/// 1. <b>The conditional consume is authoritative for this handoff.</b> The first statement in the
///    transaction matches only a Pending handoff still carrying the reviewed token and still holding no
///    created session, so exactly one contender can win. The database chooses, not a preflight read.
/// 2. <b>The partial unique index is authoritative for the invariant.</b>
///    <c>IX_AgentSessions_TaskId_Started</c> is what actually guarantees one Started session per task,
///    across every write path — this service, the manual start form, and direct SQL alike. It is the
///    only enforcement that does not depend on a caller remembering to check.
/// 3. <b>The Started-session check below is a friendly pre-check.</b> It is reliable here only because
///    step 1 already holds SQLite's write lock, so nothing can commit between the check and the insert.
///    It exists to return a typed outcome instead of a constraint violation in the common case. It is
///    not the enforcement, and removing the index because "the service already checks" would silently
///    relax the invariant.
///
/// Writing the conditional update first also keeps SQLite honest: the transaction takes its write lock
/// immediately instead of upgrading from a read.
///
/// The created session is an ordinary <see cref="AgentSession"/> produced by the same
/// <see cref="IWorkflowDispatchService"/> the manual pages use. No task is created — the work already
/// exists, only the role is new. Nothing downstream can tell it apart from a manually started session.
/// </summary>
public sealed class SessionHandoffApprovalService(
    FamiliarDbContext dbContext,
    IWorkflowDispatchService workflowDispatch,
    TimeProvider timeProvider) : ISessionHandoffApprovalService
{
    public async Task<SessionHandoffDecisionOutcome> ApproveAsync(
        SessionHandoffDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var handoff = await ReadAsync(request.HandoffId, cancellationToken);
        if (handoff is null)
        {
            return SessionHandoffDecisionOutcome.NotFound;
        }

        var terminal = DescribeTerminal(handoff);
        if (terminal is not null)
        {
            return terminal;
        }

        if (handoff.ConcurrencyToken != request.ExpectedConcurrencyToken)
        {
            return new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.StaleHandoff,
                TaskId: handoff.TaskId);
        }

        return await DispatchAsync(request, handoff, cancellationToken);
    }

    public async Task<SessionHandoffDecisionOutcome> DeclineAsync(
        SessionHandoffDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var handoff = await ReadAsync(request.HandoffId, cancellationToken);
        if (handoff is null)
        {
            return SessionHandoffDecisionOutcome.NotFound;
        }

        var terminal = DescribeTerminal(handoff);
        if (terminal is not null)
        {
            return terminal;
        }

        if (handoff.ConcurrencyToken != request.ExpectedConcurrencyToken)
        {
            return new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.StaleHandoff,
                TaskId: handoff.TaskId);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        // Same fence as approval. Declining creates nothing, so it needs no transaction around it:
        // the conditional update is the whole effect.
        var declined = await dbContext.SessionHandoffs
            .Where(candidate =>
                candidate.Id == handoff.Id
                && candidate.Status == SessionHandoffStatus.Pending
                && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken
                && candidate.CreatedSessionId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, SessionHandoffStatus.Declined)
                    .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                    .SetProperty(candidate => candidate.DecidedUtc, nowUtc)
                    .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                cancellationToken);

        if (declined != 1)
        {
            return await DescribeLostRaceAsync(handoff.Id, handoff.TaskId, cancellationToken);
        }

        return new SessionHandoffDecisionOutcome(
            SessionHandoffDecisionStatus.Declined,
            TaskId: handoff.TaskId,
            Role: handoff.ProposedRole);
    }

    private async Task<SessionHandoffDecisionOutcome> DispatchAsync(
        SessionHandoffDecisionRequest request,
        SessionHandoff handoff,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // (1) The fence.
            var consumed = await dbContext.SessionHandoffs
                .Where(candidate =>
                    candidate.Id == handoff.Id
                    && candidate.Status == SessionHandoffStatus.Pending
                    && candidate.ConcurrencyToken == request.ExpectedConcurrencyToken
                    && candidate.CreatedSessionId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, SessionHandoffStatus.Approved)
                        .SetProperty(candidate => candidate.ConcurrencyToken, Guid.NewGuid())
                        .SetProperty(candidate => candidate.DecidedUtc, nowUtc)
                        .SetProperty(candidate => candidate.UpdatedUtc, nowUtc),
                    cancellationToken);

            if (consumed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DescribeLostRaceAsync(handoff.Id, handoff.TaskId, cancellationToken);
            }

            // (2) Authoritative re-read inside the transaction. The reads before it were only fast,
            // friendly failures; these are the checks that actually gate dispatch.
            var task = await dbContext.Tasks
                .Include(candidate => candidate.Project)
                .SingleOrDefaultAsync(candidate => candidate.Id == handoff.TaskId, cancellationToken);

            if (task is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return SessionHandoffDecisionOutcome.NotFound;
            }

            if (task.Project.Status != ProjectStatus.Active)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SessionHandoffDecisionOutcome(
                    SessionHandoffDecisionStatus.ProjectInactive,
                    TaskId: task.Id);
            }

            if (task.Status == TaskStatus.Completed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SessionHandoffDecisionOutcome(
                    SessionHandoffDecisionStatus.TaskClosed,
                    TaskId: task.Id);
            }

            // (3) The friendly pre-check described in the class summary. The index is the enforcement.
            if (await workflowDispatch.HasStartedSessionAsync(task.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SessionHandoffDecisionOutcome(
                    SessionHandoffDecisionStatus.SessionAlreadyStarted,
                    TaskId: task.Id,
                    Role: handoff.ProposedRole);
            }

            // (4) An ordinary session through the shared seam. No task is created, and task.UpdatedUtc
            // is deliberately untouched so this path's effects match a manual start exactly.
            var session = workflowDispatch.StartSession(
                task,
                task.Project,
                handoff.ProposedRole,
                provider: null,
                externalSessionReference: null,
                startedUtc: nowUtc);

            await dbContext.SaveChangesAsync(cancellationToken);

            // (5) The durable link, written after the row it references exists.
            await dbContext.SessionHandoffs
                .Where(candidate => candidate.Id == handoff.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.CreatedSessionId, session.Id),
                    cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.Approved,
                SessionId: session.Id,
                TaskId: task.Id,
                Role: session.Role);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();

            // A unique-constraint failure here means the index caught a Started session this
            // transaction could not see. Anything else is a race we cannot name precisely.
            return IsUniqueConstraintViolation(exception)
                ? new SessionHandoffDecisionOutcome(
                    SessionHandoffDecisionStatus.SessionAlreadyStarted,
                    TaskId: handoff.TaskId,
                    Role: handoff.ProposedRole)
                : new SessionHandoffDecisionOutcome(
                    SessionHandoffDecisionStatus.Conflict,
                    TaskId: handoff.TaskId);
        }
    }

    private Task<SessionHandoff?> ReadAsync(Guid handoffId, CancellationToken cancellationToken) =>
        dbContext.SessionHandoffs
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == handoffId, cancellationToken);

    /// <summary>
    /// Maps an already-terminal handoff to its outcome. A replayed approval returns the session the
    /// winner created, so a double-submitted button is inert rather than an error.
    /// </summary>
    private static SessionHandoffDecisionOutcome? DescribeTerminal(SessionHandoff handoff) =>
        handoff.Status switch
        {
            SessionHandoffStatus.Approved => new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.AlreadyApproved,
                SessionId: handoff.CreatedSessionId,
                TaskId: handoff.TaskId,
                Role: handoff.ProposedRole),

            SessionHandoffStatus.Declined => new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.AlreadyDeclined,
                TaskId: handoff.TaskId,
                Role: handoff.ProposedRole),

            SessionHandoffStatus.Superseded => new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.Superseded,
                TaskId: handoff.TaskId,
                Role: handoff.ProposedRole),

            _ => null
        };

    /// <summary>
    /// Re-reads current state after losing the conditional consume, so the caller learns what actually
    /// happened rather than a generic failure.
    /// </summary>
    private async Task<SessionHandoffDecisionOutcome> DescribeLostRaceAsync(
        Guid handoffId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        var current = await ReadAsync(handoffId, cancellationToken);
        if (current is null)
        {
            return SessionHandoffDecisionOutcome.NotFound;
        }

        return DescribeTerminal(current)
            ?? new SessionHandoffDecisionOutcome(
                SessionHandoffDecisionStatus.StaleHandoff,
                TaskId: taskId,
                Role: current.ProposedRole);
    }

    internal static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                sqlite.SqliteErrorCode == SqliteConstraintErrorCode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>SQLITE_CONSTRAINT.</summary>
    private const int SqliteConstraintErrorCode = 19;
}
