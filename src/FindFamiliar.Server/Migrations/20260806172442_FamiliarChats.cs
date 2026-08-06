using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// Adds the two tables the system-wide Familiar conversation needs (Sprint 12, slice 1): the
    /// conversation itself, and its ordered turns.
    ///
    /// Additive only. Nothing existing is altered, read or written — in particular the Sprint 11
    /// per-project <c>FamiliarConversations</c> is untouched, because this is a different aggregate
    /// living in its own tables rather than a reshaping of that one. <c>Down</c> is a clean reversal:
    /// dropping these two leaves the Sprint 11 schema exactly as it was.
    ///
    /// <c>IX_FamiliarChatTurns_ChatId_InFlight</c> is the sprint's structural invariant: at most one
    /// turn in flight per conversation, enforced here rather than by a check a caller might not run.
    /// Its filter is the SQL literal <c>"State" IN ('Pending', 'Generating')</c>, which matches only
    /// because <c>FamiliarChatTurn.State</c> is stored via <c>HasConversion&lt;string&gt;()</c>.
    ///
    /// No column here holds a prompt, a system contract, a thinking block, a raw provider payload, an
    /// exception or a credential. That absence is the schema-level half of the rule that model output
    /// is inert data — there is nowhere for hidden reasoning to be written.
    /// </summary>
    public partial class FamiliarChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FamiliarChats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    FocusProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarChats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarChats_Projects_FocusProjectId",
                        column: x => x.FocusProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FamiliarChatTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UserText = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    FocusProjectIdAtTime = table.Column<Guid>(type: "TEXT", nullable: true),
                    Output = table.Column<string>(type: "TEXT", maxLength: 24000, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarChatTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarChatTurns_FamiliarChats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "FamiliarChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarChats_FocusProjectId",
                table: "FamiliarChats",
                column: "FocusProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarChats_UpdatedUtc",
                table: "FamiliarChats",
                column: "UpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarChatTurns_ChatId_InFlight",
                table: "FamiliarChatTurns",
                column: "ChatId",
                unique: true,
                filter: "\"State\" IN ('Pending', 'Generating')");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarChatTurns_ChatId_Sequence",
                table: "FamiliarChatTurns",
                columns: new[] { "ChatId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FamiliarChatTurns");

            migrationBuilder.DropTable(
                name: "FamiliarChats");
        }
    }
}
