using FindFamiliar.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// How the approval paths classify a database failure.
///
/// This exists because of a defect the Sprint 09 notes flagged and Sprint 10 confirmed: both approval
/// services caught every <see cref="SqliteException"/> and reported it as a lost race. That is wrong
/// in a way that matters. A busy database means nobody won, so the caller should retry; telling them
/// "another change reached this first" sends them looking for a second decision that never happened,
/// and — because the winner of a contended approval can hit SQLITE_BUSY too — it can leave an
/// approval reporting no winner at all.
///
/// A unique-constraint violation is a genuine lost race. A busy or locked database is not.
/// </summary>
public sealed class ApprovalFailureClassificationTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintForeignKey = 787;
    private const int SqliteIoError = 10;

    [Theory]
    [InlineData(SqliteBusy)]
    [InlineData(SqliteLocked)]
    public void A_busy_or_locked_database_is_recognised_as_busy_not_a_race(int errorCode)
    {
        var exception = new SqliteException("database is locked", errorCode);

        Assert.True(SessionHandoffApprovalService.IsDatabaseBusy(exception));
        Assert.False(SessionHandoffApprovalService.IsUniqueConstraintViolation(exception));
    }

    [Fact]
    public void A_busy_database_wrapped_by_ef_is_still_recognised()
    {
        // EF wraps provider exceptions, so the check has to walk the chain rather than test the top.
        var exception = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new SqliteException("database is locked", SqliteBusy));

        Assert.True(SessionHandoffApprovalService.IsDatabaseBusy(exception));
    }

    [Fact]
    public void A_unique_violation_is_a_race_and_not_busy()
    {
        var exception = new SqliteException(
            "UNIQUE constraint failed: AgentSessions.TaskId",
            SqliteConstraint,
            SqliteConstraintUnique);

        Assert.True(SessionHandoffApprovalService.IsUniqueConstraintViolation(exception));
        Assert.False(SessionHandoffApprovalService.IsDatabaseBusy(exception));
    }

    /// <summary>
    /// SQLITE_CONSTRAINT covers foreign-key failures too. Matching the primary code alone would
    /// report an unrelated data fault as "this task already has a running session".
    /// </summary>
    [Fact]
    public void A_foreign_key_violation_is_neither_a_uniqueness_race_nor_busy()
    {
        var exception = new SqliteException(
            "FOREIGN KEY constraint failed",
            SqliteConstraint,
            SqliteConstraintForeignKey);

        Assert.False(SessionHandoffApprovalService.IsUniqueConstraintViolation(exception));
        Assert.False(SessionHandoffApprovalService.IsDatabaseBusy(exception));
    }

    /// <summary>
    /// A genuine infrastructure fault must fall through to the generic conflict path rather than
    /// being dressed up as either a race or a retryable lock.
    /// </summary>
    [Fact]
    public void A_disk_error_is_classified_as_neither()
    {
        var exception = new SqliteException("disk I/O error", SqliteIoError);

        Assert.False(SessionHandoffApprovalService.IsUniqueConstraintViolation(exception));
        Assert.False(SessionHandoffApprovalService.IsDatabaseBusy(exception));
    }

    [Fact]
    public void A_non_sqlite_exception_is_classified_as_neither()
    {
        var exception = new InvalidOperationException("something else entirely");

        Assert.False(SessionHandoffApprovalService.IsUniqueConstraintViolation(exception));
        Assert.False(SessionHandoffApprovalService.IsDatabaseBusy(exception));
    }

    /// <summary>
    /// Both approval paths must be able to say "the database was busy" distinctly from "you lost".
    /// If either enum loses its member the mislabelling returns silently.
    /// </summary>
    [Fact]
    public void Both_approval_paths_expose_a_distinct_database_busy_outcome()
    {
        Assert.True(Enum.IsDefined(WorkApprovalStatus.DatabaseBusy));
        Assert.True(Enum.IsDefined(SessionHandoffDecisionStatus.DatabaseBusy));

        Assert.NotEqual(WorkApprovalStatus.Conflict, WorkApprovalStatus.DatabaseBusy);
        Assert.NotEqual(SessionHandoffDecisionStatus.Conflict, SessionHandoffDecisionStatus.DatabaseBusy);
    }
}
