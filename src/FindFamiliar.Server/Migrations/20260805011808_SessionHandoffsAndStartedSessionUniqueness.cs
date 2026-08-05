using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// Adds the session handoff table (ADR-0010) and makes the one-Started-session-per-task invariant
    /// database-enforced.
    ///
    /// ADR-0005 deliberately deferred that index and tolerated a violating row, surfacing it in the work
    /// queue as NeedsAttention rather than repairing it. A unique index cannot be created over data that
    /// already violates it, and the application migrates at startup, so a violating database would fail
    /// to boot. This migration therefore normalizes first.
    ///
    /// Normalization keeps the most recently started session on each task and cancels the rest, writing
    /// the same Handoff context entry a manual cancellation writes so the record is never silently
    /// discarded, and advancing each affected project's context revision so the surviving session's
    /// assignment packet correctly shows its stale-context warning.
    ///
    /// Down cannot un-cancel those sessions. That is recorded in ADR-0010.
    /// </summary>
    public partial class SessionHandoffsAndStartedSessionUniqueness : Migration
    {
        /// <summary>
        /// The losing Started sessions: every Started session on a task except the most recently
        /// started one, tie-broken by Id — the same ordering WorkQueueService uses to pick the latest
        /// session for a task.
        ///
        /// Declared once and reused verbatim by all three normalization statements. If these
        /// predicates ever diverge, a session could be cancelled without its context entry, or an
        /// entry written for a survivor.
        /// </summary>
        private const string LosingStartedSessions = """
            SELECT "Id" FROM (
                SELECT "Id", ROW_NUMBER() OVER (
                    PARTITION BY "TaskId" ORDER BY "StartedUtc" DESC, "Id" DESC) AS "rn"
                FROM "AgentSessions" WHERE "Status" = 'Started')
            WHERE "rn" > 1
            """;

        /// <summary>
        /// A lowercase hyphenated v4 UUID, matching the TEXT form EF Core's SQLite provider writes for
        /// Guid properties. SQLite has no built-in uuid() function.
        /// </summary>
        private const string NewGuidText = """
            lower(
                substr(hex(randomblob(4)), 1, 8) || '-' ||
                substr(hex(randomblob(2)), 1, 4) || '-4' ||
                substr(hex(randomblob(2)), 2, 3) || '-' ||
                substr('89ab', abs(random()) % 4 + 1, 1) ||
                substr(hex(randomblob(2)), 2, 3) || '-' ||
                substr(hex(randomblob(6)), 1, 12))
            """;

        /// <summary>
        /// UTC now in the TEXT shape EF Core's SQLite provider reads back into a DateTime.
        /// </summary>
        private const string NowText = "strftime('%Y-%m-%d %H:%M:%f', 'now')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (1) Record why each losing session ended, before anything is mutated. Same shape as a
            // manual cancellation: one Handoff entry titled "{Role} session cancelled".
            migrationBuilder.Sql($"""
                INSERT INTO "ContextEntries" (
                    "Id", "ProjectId", "TaskId", "SourceSessionId", "Kind", "Title", "Content",
                    "State", "SupersedesContextEntryId", "CreatedUtc")
                SELECT
                    {NewGuidText},
                    "t"."ProjectId",
                    "s"."TaskId",
                    "s"."Id",
                    'Handoff',
                    "s"."Role" || ' session cancelled',
                    'Cancelled by the SessionHandoffsAndStartedSessionUniqueness migration. This task held more than one Started session, which ADR-0005 tolerated and ADR-0010 now forbids. The most recently started session was kept.',
                    'Active',
                    NULL,
                    {NowText}
                FROM "AgentSessions" AS "s"
                JOIN "Tasks" AS "t" ON "t"."Id" = "s"."TaskId"
                WHERE "s"."Id" IN ({LosingStartedSessions});
                """);

            // (2) Advance the context revision of every affected project. The surviving session read a
            // revision that no longer describes its task, so its packet must warn about stale context.
            migrationBuilder.Sql($"""
                UPDATE "Projects"
                SET "ContextRevision" = "ContextRevision" + 1,
                    "UpdatedUtc" = {NowText}
                WHERE "Id" IN (
                    SELECT DISTINCT "t"."ProjectId"
                    FROM "AgentSessions" AS "s"
                    JOIN "Tasks" AS "t" ON "t"."Id" = "s"."TaskId"
                    WHERE "s"."Id" IN ({LosingStartedSessions}));
                """);

            // (3) Cancel the losers. Runs last: steps (1) and (2) both select on Status = 'Started'.
            migrationBuilder.Sql($"""
                UPDATE "AgentSessions"
                SET "Status" = 'Cancelled',
                    "CompletedUtc" = {NowText}
                WHERE "Id" IN ({LosingStartedSessions});
                """);

            migrationBuilder.CreateTable(
                name: "SessionHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProposedRole = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ObservedContextRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedSessionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionHandoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionHandoffs_AgentSessions_CreatedSessionId",
                        column: x => x.CreatedSessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SessionHandoffs_AgentSessions_SourceSessionId",
                        column: x => x.SourceSessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionHandoffs_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Deliberately not wrapped in any error handling. A database holding SessionHandoffs
            // without this index would run handoff approval against an unenforced invariant, which is
            // strictly worse than refusing to start.
            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskId_Started",
                table: "AgentSessions",
                column: "TaskId",
                unique: true,
                filter: "\"Status\" = 'Started'");

            migrationBuilder.CreateIndex(
                name: "IX_SessionHandoffs_CreatedSessionId",
                table: "SessionHandoffs",
                column: "CreatedSessionId",
                unique: true,
                filter: "\"CreatedSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SessionHandoffs_SourceSessionId",
                table: "SessionHandoffs",
                column: "SourceSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionHandoffs_TaskId_Pending",
                table: "SessionHandoffs",
                column: "TaskId",
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionHandoffs");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_TaskId_Started",
                table: "AgentSessions");

            // The sessions Up cancelled are not restored. Their cancellation context entries remain as
            // the durable record of what happened.
        }
    }
}
