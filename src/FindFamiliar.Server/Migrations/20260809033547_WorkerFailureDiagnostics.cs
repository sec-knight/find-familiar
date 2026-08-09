using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <inheritdoc />
    public partial class WorkerFailureDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailureAdapterExitCode",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCategory",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureMessage",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureProviderExitCode",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FailureProviderLaunched",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureAdapterExitCode",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "FailureCategory",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "FailureMessage",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "FailureProviderExitCode",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "FailureProviderLaunched",
                table: "AgentSessions");
        }
    }
}
