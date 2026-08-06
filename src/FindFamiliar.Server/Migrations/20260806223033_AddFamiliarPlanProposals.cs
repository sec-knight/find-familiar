using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// Durable multi-item plans drafted in conversation (Sprint 13, slice 3).
    ///
    /// Two new tables and one nullable-defaulted column. Nothing existing is read or rewritten.
    ///
    /// <c>IX_FamiliarPlanProposals_ChatId_Pending</c> is the load-bearing object here: at most one
    /// undecided plan per conversation, the same shape as
    /// <c>IX_FamiliarActionProposals_ConversationId_Pending</c> and
    /// <c>IX_FamiliarChatTurns_ChatId_InFlight</c>. Contenders race for one row, a human decides once,
    /// and a half-approved plan cannot exist in this database. The filter matches the stored TEXT
    /// because Status is converted to a string; removing that conversion would silently delete the
    /// invariant, which is what FamiliarPlanPendingUniqueIndexTests exists to catch.
    ///
    /// Items are typed columns, not a JSON blob, for the reason FamiliarActionProposals gives: a blob
    /// moves validation out of the schema and into a parser reading model output. Nothing here names a
    /// handler, a command, a service or a table — <c>Role</c> is a closed three-member enum, so model
    /// text cannot select executable behaviour.
    ///
    /// A row in either table proposes work and authorises none. No task, no session, no context entry,
    /// no revision change happens until a human approves (ADR-0014).
    ///
    /// Still no column for a prompt, a raw payload or a provider exception.
    /// </summary>
    public partial class AddFamiliarPlanProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequestedPlan",
                table: "FamiliarChatTurns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FamiliarPlanProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservedContextRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarPlanProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarPlanProposals_FamiliarChatTurns_TurnId",
                        column: x => x.TurnId,
                        principalTable: "FamiliarChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FamiliarPlanProposals_FamiliarChats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "FamiliarChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FamiliarPlanProposals_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FamiliarPlanItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RequestedOutcome = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EvidenceEntryIds = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedTaskId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarPlanItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarPlanItems_FamiliarPlanProposals_PlanId",
                        column: x => x.PlanId,
                        principalTable: "FamiliarPlanProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarPlanItems_CreatedTaskId",
                table: "FamiliarPlanItems",
                column: "CreatedTaskId",
                unique: true,
                filter: "\"CreatedTaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarPlanItems_PlanId_Position",
                table: "FamiliarPlanItems",
                columns: new[] { "PlanId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarPlanProposals_ChatId_Pending",
                table: "FamiliarPlanProposals",
                column: "ChatId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarPlanProposals_ProjectId",
                table: "FamiliarPlanProposals",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarPlanProposals_TurnId",
                table: "FamiliarPlanProposals",
                column: "TurnId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FamiliarPlanItems");

            migrationBuilder.DropTable(
                name: "FamiliarPlanProposals");

            // Raw ALTER TABLE, for the reason recorded on SensitivityAndCachedTokens: EF's SQLite
            // provider implements DropColumn by rebuilding the table, which reorders columns and
            // leaves a schema that no longer matches what a forward migration produces.
            migrationBuilder.Sql("ALTER TABLE \"FamiliarChatTurns\" DROP COLUMN \"RequestedPlan\";");
        }
    }
}
