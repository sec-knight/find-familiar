using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <inheritdoc />
    public partial class ContextEntryProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Unspecified", not "". The column stores the enum by name, and an empty string is not a
            // member — every row written before this migration would fail to materialise on read.
            // Unspecified is also the honest value for them: those records are not suspect, they
            // predate the question being asked.
            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "ContextEntries",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.AddColumn<string>(
                name: "RecordedBy",
                table: "ContextEntries",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Written out rather than left as two <c>DropColumn</c> calls.
        ///
        /// SQLite cannot drop a column in place, so EF rebuilds the table — and it rebuilds it in the
        /// order the current model declares, not the order the table actually had. The result is a
        /// schema that is semantically identical and textually different, which is exactly what the
        /// migration round-trip tests exist to catch: they assert that rolling back restores the
        /// earlier schema *exactly*, because a rollback that quietly reshapes a table is not a rollback.
        ///
        /// So the original DDL is restored verbatim, including the trailing-comma placement of
        /// <c>IsSensitive</c> that an earlier migration left behind. Reproducing that oddity is the
        /// point: this is the schema that existed, and Down's job is to produce it.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_ContextEntries" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ContextEntries" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "TaskId" TEXT NULL,
                    "SourceSessionId" TEXT NULL,
                    "Kind" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "Content" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "SupersedesContextEntryId" TEXT NULL,
                    "CreatedUtc" TEXT NOT NULL, "IsSensitive" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "FK_ContextEntries_AgentSessions_SourceSessionId" FOREIGN KEY ("SourceSessionId") REFERENCES "AgentSessions" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_ContextEntries_ContextEntries_SupersedesContextEntryId" FOREIGN KEY ("SupersedesContextEntryId") REFERENCES "ContextEntries" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_ContextEntries_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_ContextEntries_Tasks_TaskId" FOREIGN KEY ("TaskId") REFERENCES "Tasks" ("Id") ON DELETE RESTRICT
                );
                """);

            // Every column but the two being dropped. Provenance and RecordedBy are discarded, which is
            // what dropping them means — a rollback loses the facts the columns held, and there is
            // nowhere in the earlier schema to keep them.
            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_ContextEntries" ("Id", "ProjectId", "TaskId", "SourceSessionId", "Kind", "Title", "Content", "State", "SupersedesContextEntryId", "CreatedUtc", "IsSensitive")
                SELECT "Id", "ProjectId", "TaskId", "SourceSessionId", "Kind", "Title", "Content", "State", "SupersedesContextEntryId", "CreatedUtc", "IsSensitive"
                FROM "ContextEntries";
                """);

            migrationBuilder.Sql("""DROP TABLE "ContextEntries";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_ContextEntries" RENAME TO "ContextEntries";""");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");

            migrationBuilder.Sql("""CREATE INDEX "IX_ContextEntries_ProjectId_TaskId_State_CreatedUtc" ON "ContextEntries" ("ProjectId", "TaskId", "State", "CreatedUtc");""");
            migrationBuilder.Sql("""CREATE INDEX "IX_ContextEntries_SourceSessionId" ON "ContextEntries" ("SourceSessionId");""");
            migrationBuilder.Sql("""CREATE INDEX "IX_ContextEntries_SupersedesContextEntryId" ON "ContextEntries" ("SupersedesContextEntryId");""");
            migrationBuilder.Sql("""CREATE INDEX "IX_ContextEntries_TaskId" ON "ContextEntries" ("TaskId");""");
        }
    }
}
