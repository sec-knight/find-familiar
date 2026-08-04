using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <inheritdoc />
    public partial class ConversationalWorkIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApprovedTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApprovedSessionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_AgentSessions_ApprovedSessionId",
                        column: x => x.ApprovedSessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Tasks_ApprovedTaskId",
                        column: x => x.ApprovedTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RequestedOutcome = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ObservedContextRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedSessionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkProposals_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkProposals_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId_Sequence",
                table: "ConversationMessages",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ApprovedSessionId",
                table: "Conversations",
                column: "ApprovedSessionId",
                unique: true,
                filter: "\"ApprovedSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ApprovedTaskId",
                table: "Conversations",
                column: "ApprovedTaskId",
                unique: true,
                filter: "\"ApprovedTaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Status_UpdatedUtc",
                table: "Conversations",
                columns: new[] { "Status", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkProposals_ConversationId",
                table: "WorkProposals",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkProposals_ProjectId",
                table: "WorkProposals",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkProposals_Status_ConcurrencyToken",
                table: "WorkProposals",
                columns: new[] { "Status", "ConcurrencyToken" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "WorkProposals");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
