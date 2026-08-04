using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <inheritdoc />
    public partial class WorkerRegistrationAndSessionClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimExpiresUtc",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedByWorkerId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUtc",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkerKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Capabilities = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RegisteredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastClaimUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ClaimedByWorkerId",
                table: "AgentSessions",
                column: "ClaimedByWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_Status_ClaimExpiresUtc",
                table: "AgentSessions",
                columns: new[] { "Status", "ClaimExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Workers_WorkerKey",
                table: "Workers",
                column: "WorkerKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_Workers_ClaimedByWorkerId",
                table: "AgentSessions",
                column: "ClaimedByWorkerId",
                principalTable: "Workers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_Workers_ClaimedByWorkerId",
                table: "AgentSessions");

            migrationBuilder.DropTable(
                name: "Workers");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_ClaimedByWorkerId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_Status_ClaimExpiresUtc",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaimExpiresUtc",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaimId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaimedByWorkerId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaimedUtc",
                table: "AgentSessions");
        }
    }
}
