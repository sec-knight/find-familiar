using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// Records who answered a conversational turn and what it cost (Sprint 12, slice 2).
    ///
    /// Four nullable columns added to <c>FamiliarChatTurns</c>, and nothing else. They arrive now
    /// rather than in slice 1 because slice 1 had no provider to name — a column with no writer is a
    /// column nothing can be asserted about.
    ///
    /// Nullable on purpose, and not backfilled. A turn from slice 1 genuinely had no provider behind
    /// it, and inventing an attribution for it would make the transcript claim something untrue about
    /// its own history.
    ///
    /// The token counts are <c>InputTokens</c> and <c>OutputTokens</c>, not Prompt/Completion, because
    /// <c>FamiliarConversationModelTests</c> rejects any column name containing "Prompt" and that guard
    /// is worth more blunt than precise. See the remarks on <c>FamiliarChatTurn.InputTokens</c>.
    ///
    /// Still no column for a prompt, a system prompt, a raw payload or a provider exception. Adding the
    /// streaming provider is exactly when that would have slipped in, so it is worth restating: the
    /// column does not exist, so nothing can write to it.
    /// </summary>
    public partial class FamiliarChatProviderMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "FamiliarChatTurns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "FamiliarChatTurns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderModel",
                table: "FamiliarChatTurns",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "FamiliarChatTurns",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "FamiliarChatTurns");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "FamiliarChatTurns");

            migrationBuilder.DropColumn(
                name: "ProviderModel",
                table: "FamiliarChatTurns");

            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "FamiliarChatTurns");
        }
    }
}
