using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// Inserts rows into a database that has been migrated only part way, using explicit SQL rather than
/// the current EF model.
///
/// Migration tests seed a database as it existed <i>before</i> the migration under test, then apply
/// that migration and assert nothing existing moved. Seeding through EF makes that impossible to do
/// correctly: EF writes every column the <i>current</i> model describes, so the moment any migration
/// adds a column to an existing table, every such test starts inserting a column the older schema
/// does not have and fails with a SQLite error that looks nothing like the thing being tested.
///
/// That flaw was latent for three sprints, because every migration until Sprint 12 added whole tables
/// rather than columns. <c>SensitivityAndCachedTokens</c> added <c>IsSensitive</c> to
/// <c>Projects</c> and <c>ContextEntries</c> and exposed it across three test classes at once.
///
/// The same applies to <i>reading</i> those rows back: a migration test compares every row before and
/// after, and an EF <c>SELECT</c> names the new column just as an <c>INSERT</c> does. So both
/// directions live here.
///
/// Only the tables that gained columns need this treatment; everything else is still inserted and
/// read through EF, because for those the current model and the historical schema agree. If a future
/// migration adds a column to another existing table, that table joins this file — which is the
/// signal, not the inconvenience.
/// </summary>
internal static class LegacyRowSeeder
{
    /// <summary>
    /// A project as <c>Projects</c> looked before <c>IsSensitive</c> existed.
    ///
    /// <c>ContextRevision</c> is set directly rather than through
    /// <see cref="FamiliarProject.IncrementContextRevision"/>, because the point is to write a
    /// specific historical row rather than to exercise the domain.
    /// </summary>
    public static Task InsertProjectAsync(
        FamiliarDbContext dbContext,
        Guid id,
        string name,
        string purpose,
        ProjectStatus status,
        int contextRevision,
        DateTime nowUtc) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Projects" (
                "Id", "Name", "Purpose", "Status", "ContextRevision", "CreatedUtc", "UpdatedUtc")
            VALUES (
                {id}, {name}, {purpose}, {status.ToString()}, {contextRevision}, {nowUtc}, {nowUtc});
            """);

    /// <summary>A context entry as <c>ContextEntries</c> looked before <c>IsSensitive</c> existed.</summary>
    public static Task InsertContextEntryAsync(
        FamiliarDbContext dbContext,
        Guid id,
        Guid projectId,
        Guid? taskId,
        Guid? sourceSessionId,
        ContextEntryKind kind,
        string title,
        string content,
        ContextEntryState state,
        DateTime nowUtc) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "ContextEntries" (
                "Id", "ProjectId", "TaskId", "SourceSessionId", "Kind", "Title", "Content",
                "State", "SupersedesContextEntryId", "CreatedUtc")
            VALUES (
                {id}, {projectId}, {taskId}, {sourceSessionId}, {kind.ToString()}, {title}, {content},
                {state.ToString()}, NULL, {nowUtc});
            """);

    /// <summary>Insert an AgentSession using the schema before WorkerFailureDiagnostics existed.</summary>
    public static Task InsertAgentSessionAsync(
        FamiliarDbContext dbContext,
        AgentSession session) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "AgentSessions" (
                "Id", "TaskId", "Role", "Provider", "ExternalSessionReference", "Status",
                "ContextRevisionRead", "StartedUtc", "CompletedUtc", "ClaimedByWorkerId",
                "ClaimedUtc", "ClaimExpiresUtc", "ClaimId")
            VALUES (
                {session.Id}, {session.TaskId}, {session.Role.ToString()}, {session.Provider},
                {session.ExternalSessionReference}, {session.Status.ToString()}, {session.ContextRevisionRead},
                {session.StartedUtc}, {session.CompletedUtc}, {session.ClaimedByWorkerId},
                {session.ClaimedUtc}, {session.ClaimExpiresUtc}, {session.ClaimId});
            """);

    /// <summary>Read sessions without naming the later diagnostic columns.</summary>
    public static Task<List<string>> ReadAgentSessionRowsAsync(FamiliarDbContext dbContext) =>
        dbContext.Database
            .SqlQuery<string>(
                $"""
                SELECT 'Session ' || "Id" || ' ' || "TaskId" || ' ' || "Role" || ' ' || "Status" || ' '
                    || "ContextRevisionRead" || ' ' || "StartedUtc" || ' ' || COALESCE("CompletedUtc", '-') AS "Value"
                FROM "AgentSessions" ORDER BY "Id";
                """)
            .ToListAsync();

    /// <summary>
    /// Every project row, rendered as ordered text, reading only the columns that predate Sprint 12.
    ///
    /// Deliberately not a <c>SELECT *</c>: the point is to compare the same columns before and after
    /// a migration, and a wildcard would silently start including whatever the migration added,
    /// making the comparison pass for the wrong reason.
    /// </summary>
    public static Task<List<string>> ReadProjectRowsAsync(FamiliarDbContext dbContext) =>
        dbContext.Database
            .SqlQuery<string>(
                $"""
                SELECT 'Project ' || "Id" || ' ' || "Name" || ' ' || "Status" || ' '
                    || "ContextRevision" || ' ' || "Purpose" AS "Value"
                FROM "Projects" ORDER BY "Id";
                """)
            .ToListAsync();

    /// <summary>Every context entry row, on the same terms.</summary>
    public static Task<List<string>> ReadContextEntryRowsAsync(FamiliarDbContext dbContext) =>
        dbContext.Database
            .SqlQuery<string>(
                $"""
                SELECT 'ContextEntry ' || "Id" || ' ' || "ProjectId" || ' ' || COALESCE("TaskId", '-')
                    || ' ' || "Kind" || ' ' || "State" || ' ' || "Title" || ' ' || "Content" AS "Value"
                FROM "ContextEntries" ORDER BY "Id";
                """)
            .ToListAsync();
}
